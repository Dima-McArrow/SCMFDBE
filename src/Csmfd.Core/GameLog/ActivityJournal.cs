using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Csmfd.Protocol;

namespace Csmfd.Core.GameLog;

/// <summary>One reader, bounded notification assembly and replay. No wallet inference or disk history.</summary>
public sealed partial class ActivityJournal
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Queue<ActivityEvent> _events = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly StringBuilder _notification = new();
    private Guid _generation;
    private Guid _streamId = Guid.NewGuid();
    private DateTimeOffset _startedAt;
    private DateTimeOffset _notificationAt;
    private long _seq;
    private long _lastOffset = -1;
    private int _notificationLines;
    private bool _assembling;
    private bool _ended;

    public ActivityJournal(TimeProvider time, Guid generation)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _generation = generation;
        _startedAt = time.GetUtcNow();
    }

    public event Action? Changed;

    public void Reset(Guid generation)
    {
        lock (_gate)
        {
            _generation = generation;
            _streamId = Guid.NewGuid();
            _startedAt = _time.GetUtcNow();
            _events.Clear();
            _seen.Clear();
            _seenOrder.Clear();
            _notification.Clear();
            _assembling = false;
            _lastOffset = -1;
            _seq = 0;
            _ended = false;
        }

        Changed?.Invoke();
    }

    public ActivityPage? Read(Guid? streamId, long afterSeq)
    {
        lock (_gate)
        {
            if (streamId == _streamId && afterSeq >= _seq)
            {
                return null;
            }

            var cursor = streamId == _streamId ? afterSeq : 0;
            return new ActivityPage(_streamId, _startedAt, _events.TryPeek(out var first) ? first.Seq : 1,
                _seq, _ended, _events.Where(e => e.Seq > cursor).Take(ActivityLimits.PageSize).ToArray());
        }
    }

    public void End()
    {
        lock (_gate)
        {
            if (_ended)
            {
                return;
            }

            Add("sessionEnded", _time.GetUtcNow(), string.Empty, null, null, null, null);
            _ended = true;
        }

        Changed?.Invoke();
    }

    public void OnLine(LogLine line)
    {
        // Most game lines are unrelated. Keep the hot path free of locks and allocations.
        if (!_assembling && !line.Text.Contains("<SHUDEvent_OnNotification> Added notification \"", StringComparison.Ordinal))
        {
            return;
        }

        var changed = false;
        lock (_gate)
        {
            if (line.Generation != _generation || line.Offset <= _lastOffset || _ended)
            {
                return;
            }

            _lastOffset = line.Offset;
            var start = line.Text.IndexOf("<SHUDEvent_OnNotification> Added notification \"", StringComparison.Ordinal);
            if (start >= 0)
            {
                _assembling = TryTime(line.Text, out _notificationAt);
                _notification.Clear();
                _notificationLines = 0;
            }
            else if (!_assembling)
            {
                return;
            }
            else if (line.Text.Contains("[Notice]", StringComparison.Ordinal) || line.Text.Contains("[Error]", StringComparison.Ordinal))
            {
                _assembling = false;
                return;
            }

            if (!_assembling)
            {
                return;
            }

            var part = start >= 0 ? line.Text[(start + "<SHUDEvent_OnNotification> Added notification \"".Length)..] : WithoutTimestamp(line.Text);
            if (++_notificationLines > 8 || _notification.Length + part.Length > 4096)
            {
                _assembling = false;
                return;
            }

            _notification.Append(part);
            if (part.Contains("to queue.", StringComparison.Ordinal))
            {
                _assembling = false;
                try
                {
                    changed = CompleteNotification();
                }
                catch (RegexMatchTimeoutException)
                {
                    // A diagnostic line must never terminate the existing reader.
                }
            }
            else
            {
                _notification.Append(' ');
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private bool CompleteNotification()
    {
        var notification = _notification.ToString();
        var end = notification.LastIndexOf("\" [", StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        var body = notification[..end].Trim();
        var kind = string.Empty;
        long? amount = null;
        string? currency = null;
        // Ship channel join/leave is not activity: it drives the ship context in the state message only
        // (one path for the iPad's Deck and theme, owner decision 2026-10-28).
        var reward = RewardRegex().Match(body);
        if (reward.Success && long.TryParse(reward.Groups["amount"].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value is > 0 and <= 1_000_000_000)
        {
            kind = "reward";
            amount = value;
            currency = reward.Groups["currency"].Value;
        }
        else
        {
            foreach (var (prefix, eventKind) in Prefixes)
            {
                if (body.StartsWith(prefix, StringComparison.Ordinal))
                {
                    kind = eventKind;
                    body = body[prefix.Length..].Trim().TrimEnd(':').TrimEnd();
                    break;
                }
            }
        }

        if (kind.Length == 0)
        {
            return false;
        }

        var mission = MissionRegex().Match(notification);
        Guid? missionId = mission.Success && Guid.TryParse(mission.Groups[1].Value, out var id) && id != Guid.Empty ? id : null;
        if (kind != "reward" && missionId is null)
        {
            return false;
        }

        var identity = _notificationAt.ToString("O", CultureInfo.InvariantCulture) + notification[end..];
        if (!_seen.Add(identity))
        {
            return false;
        }

        _seenOrder.Enqueue(identity);
        if (_seenOrder.Count > ActivityLimits.History * 2)
        {
            _seen.Remove(_seenOrder.Dequeue());
        }

        var objective = ObjectiveRegex().Match(notification);
        var objectiveId = objective.Success && TextRules.IsValidText(objective.Groups[1].Value, 1, ActivityLimits.ObjectiveScalars)
            ? objective.Groups[1].Value : null;
        Add(kind, _notificationAt, Clean(body, ActivityLimits.TextScalars), missionId, objectiveId, amount, currency);
        return true;
    }

    private void Add(string kind, DateTimeOffset at, string text, Guid? missionId, string? objectiveId, long? amount, string? currency)
    {
        _events.Enqueue(new ActivityEvent(++_seq, kind, at, text, missionId, objectiveId, amount, currency));
        if (_events.Count > ActivityLimits.History)
        {
            _events.Dequeue();
        }
    }

    private static string Clean(string text, int limit)
    {
        var runes = text.EnumerateRunes().Where(r => !TextRules.IsControlOrFormat(r)).Take(limit + 1).ToArray();
        return runes.Length <= limit
            ? string.Concat(runes.Select(r => r.ToString()))
            : string.Concat(runes.Take(limit - 1).Select(r => r.ToString())) + "…";
    }

    private static string WithoutTimestamp(string line) => line.StartsWith('<') && line.IndexOf('>', StringComparison.Ordinal) is var end && end > 0 ? line[(end + 1)..].TrimStart() : line;

    private static bool TryTime(string line, out DateTimeOffset time)
    {
        var end = line.IndexOf('>', StringComparison.Ordinal);
        time = default;
        return end > 1 && DateTimeOffset.TryParse(line.AsSpan(1, end - 1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);
    }

    private static readonly (string Prefix, string Kind)[] Prefixes =
    [
        ("Contract Accepted:", "contractAccepted"), ("Contract Complete:", "contractComplete"),
        ("Contract Withdrawn:", "contractWithdrawn"), ("Contract Failed:", "contractFailed"),
        ("New Objective:", "newObjective"), ("Objective Complete:", "objectiveComplete"),
    ];

    [GeneratedRegex(@"^Awarded (?<amount>[0-9][0-9,]*) (?<currency>aUEC|UEC):?\s*$", RegexOptions.CultureInvariant, 50)]
    private static partial Regex RewardRegex();

    [GeneratedRegex(@"MissionId: \[([0-9a-fA-F-]{36})\]", RegexOptions.CultureInvariant, 50)]
    private static partial Regex MissionRegex();

    [GeneratedRegex(@"ObjectiveId: \[([^\]]*)\]", RegexOptions.CultureInvariant, 50)]
    private static partial Regex ObjectiveRegex();
}
