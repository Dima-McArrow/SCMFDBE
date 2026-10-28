using Csmfd.Core.Input;
using Csmfd.Core.State;
using Csmfd.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csmfd.Core.GameLog;

/// <summary>
/// Turns verified log events into published state (spec 41.8, 41.9, 41.13, 41.14):
/// ship context lifecycle, QT target of the current ship and the ATC request indicator.
/// Lock order: this class never calls the executor while holding its own lock, because the
/// executor calls <see cref="OnInputStarted"/> while holding its lock.
/// Debug messages explain each decision (event type, offset, revision, entity ids, ignore reasons) but
/// never contain the player handle (channel owner) or raw log lines.
/// </summary>
public sealed partial class GameLogProcessor : IDisposable
{
    public static readonly TimeSpan HangarTimeout = TimeSpan.FromSeconds(120);

    private const int MaxRemembered = 128;
    private const string RequestLandingAction = "requestLanding";

    private readonly ActivityJournal? _activity;
    private readonly Lock _gate = new();
    private readonly StateStore _store;
    private readonly InputExecutor _executor;
    private readonly TimeProvider _time;
    private readonly Func<long> _probeWatermark;
    private readonly ILogger _logger;
    private readonly Dictionary<string, VehicleSpawning> _spawns = new(StringComparer.Ordinal);
    private readonly Queue<string> _spawnOrder = new();
    private readonly Dictionary<string, VehicleObserved> _observations = new(StringComparer.Ordinal);
    private readonly Queue<string> _observationOrder = new();

    private Guid _generation;
    private long _revision;
    private Guid? _contextId;
    private ActiveShip? _active;
    private ShipContextReason _lastReason = ShipContextReason.Startup;
    private ShipContextStatus _status = ShipContextStatus.Unknown;
    private DateTimeOffset? _contextObservedAt;
    private string? _lastPrefix;
    private QtTargetState _qt = QtTargetState.Unknown;
    private PendingHangar? _hangar;
    private HangarRequestState _hangarState = HangarRequestState.Idle;
    private long _hangarToken;

    public GameLogProcessor(StateStore store, InputExecutor executor, TimeProvider time, Guid initialGeneration, Func<long> probeWatermark, ILogger? logger = null, ActivityJournal? activity = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _probeWatermark = probeWatermark ?? throw new ArgumentNullException(nameof(probeWatermark));
        _logger = logger ?? NullLogger.Instance;
        _generation = initialGeneration;
        _activity = activity;
    }

    public void OnLine(LogLine line)
    {
        _activity?.OnLine(line);
        var parsed = GameLogParser.Parse(line.Text);
        if (parsed is null)
        {
            return;
        }

        LogRecognized(_logger, parsed.GetType().Name, line.Offset);

        var cancelInput = false;
        lock (_gate)
        {
            if (line.Generation != _generation)
            {
                return; // Stale line from a previous generation.
            }

            cancelInput = Apply(parsed, line);
        }

        if (cancelInput)
        {
            _executor.CancelAll(CancelReason.ShipContextChanged);
        }
    }

    /// <summary>The log was replaced/truncated/lost: nothing from before can be trusted (spec 41.14).</summary>
    public void OnGenerationReset(Guid generation)
    {
        _activity?.Reset(generation);
        lock (_gate)
        {
            _generation = generation;
            _spawns.Clear();
            _spawnOrder.Clear();
            _observations.Clear();
            _observationOrder.Clear();
            _active = null;
            _contextId = null;
            LogGenerationReset(_logger, generation);
            Transition(ShipContextStatus.Unknown, ShipContextReason.LogReset, _time.GetUtcNow());
        }

        _executor.CancelAll(CancelReason.ShipContextChanged);
    }

    public void OnLogStatus(LogStatus status, Guid generation)
    {
        _store.Update(s => s.Log.Status == status && s.Log.Generation == generation
            ? s
            : s with { Log = new LogState(status, generation, _time.GetUtcNow()) });
    }

    /// <summary>The game process exited (from the process monitor).</summary>
    public void OnGameProcessExited()
    {
        _activity?.End();
        bool changed;
        lock (_gate)
        {
            changed = EndSession(_time.GetUtcNow());
        }

        if (changed)
        {
            _executor.CancelAll(CancelReason.GameEnded);
        }
    }

    /// <summary>
    /// Called by the executor after a successful down/hold. Only requestLanding starts ATC tracking;
    /// the watermark is the file length now, so an older notification can never answer it (spec 41.13).
    /// </summary>
    public void OnInputStarted(Guid commandId, ActionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!string.Equals(spec.Action, RequestLandingAction, StringComparison.Ordinal))
        {
            return;
        }

        var watermark = _probeWatermark();
        lock (_gate)
        {
            _hangar?.Timer.Dispose();
            var token = ++_hangarToken;
            var timer = _time.CreateTimer(
                static state =>
                {
                    var (processor, hangarToken) = ((GameLogProcessor, long))state!;
                    processor.OnHangarTimeout(hangarToken);
                },
                (this, token),
                HangarTimeout,
                Timeout.InfiniteTimeSpan);

            var now = _time.GetUtcNow();
            _hangar = new PendingHangar(token, commandId, _contextId, _generation, watermark, timer);
            _hangarState = new HangarRequestState(HangarRequestStatus.Pending, commandId, _contextId, now, now);
            LogAtcPending(_logger, commandId, watermark);
            Publish(urgent: false);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _hangar?.Timer.Dispose();
            _hangar = null;
        }
    }

    /// <summary>Returns true when synthetic input must be released (outside the lock).</summary>
    private bool Apply(LogEvent parsed, LogLine line)
    {
        switch (parsed)
        {
            case ShipChannelJoined joined:
                // Every new ship channel join is a new context, even for the same ship (spec 41.9).
                _active = new ActiveShip(joined.ShipName, joined.Owner, joined.ManufacturerPrefix, joined.At);
                _contextId = Guid.NewGuid();
                _lastPrefix = joined.ManufacturerPrefix;
                TryBind();
                Transition(ShipContextStatus.Assumed, ShipContextReason.ChannelJoined, joined.At);
                return true;

            case ShipChannelLeft left:
                return ApplyChannelLeft(left);

            case VehicleSpawning spawning:
                Remember(_spawns, _spawnOrder, spawning.EntityId, spawning);
                return EnrichIfBound();

            case VehicleObserved observed:
                Remember(_observations, _observationOrder, observed.EntityId, observed);
                return EnrichIfBound();

            case QuantumTargetSelected target:
                Remember(_observations, _observationOrder, target.EntityId, new VehicleObserved(target.At, target.Model, target.EntityId));
                EnrichIfBound();
                if (_active?.EntityId is { } entityId && string.Equals(entityId, target.EntityId, StringComparison.Ordinal))
                {
                    _qt = new QtTargetState(QtMode.Destination, target.Target, DisplayName: null, Confidence.Confirmed, target.At);
                    LogQtAccepted(_logger, target.Target, target.EntityId);
                    Publish(urgent: false);
                }
                else
                {
                    LogQtIgnored(_logger, target.EntityId, _active?.EntityId ?? "unknown");
                }

                return false;

            case LocalClearDriver clear:
                return ApplyClearDriver(clear);

            case HangarRequestCompleted completed:
                ApplyHangarCompleted(completed, line);
                return false;

            case GameSessionEnded ended:
                if (line.Text.Contains("<SystemQuit>", StringComparison.Ordinal))
                {
                    _activity?.End();
                }

                return EndSession(ended.At);

            default:
                return false;
        }
    }

    /// <summary>
    /// Leaving the seat only releases held input (returns true). The ship context, and with it the iPad's
    /// Deck and theme, follows the ship channel: join and leave (owner decision, 2026-10-28).
    /// </summary>
    private bool ApplyClearDriver(LocalClearDriver clear)
    {
        if (_active is not { } active)
        {
            LogClearDriverIgnored(_logger, clear.EntityId, "no ship context");
            return false;
        }

        var matches = active.EntityId is { } entityId
            ? string.Equals(entityId, clear.EntityId, StringComparison.Ordinal)
            : GameLogParser.ShipNameMatchesModel(active.Name, clear.Model);
        if (!matches)
        {
            LogClearDriverIgnored(_logger, clear.EntityId, "another vehicle");
            return false;
        }

        LogClearDriverReleased(_logger, clear.EntityId);
        return true;
    }

    /// <summary>Only the channel of the current ship ends its context; leaving an older channel changes nothing.</summary>
    private bool ApplyChannelLeft(ShipChannelLeft left)
    {
        if (_active is not { } active
            || !string.Equals(active.Name, left.ShipName, StringComparison.Ordinal)
            || !string.Equals(active.Owner, left.Owner, StringComparison.Ordinal))
        {
            LogChannelLeftIgnored(_logger, left.ShipName);
            return false;
        }

        _active = null;
        Transition(ShipContextStatus.NoShip, ShipContextReason.ChannelLeft, left.At);
        return true;
    }

    private void ApplyHangarCompleted(HangarRequestCompleted completed, LogLine line)
    {
        if (_hangar is not { } pending)
        {
            LogAtcIgnored(_logger, line.Offset, "no pending request from the iPad");
            return;
        }

        var ignored = pending.Generation != line.Generation ? "other log generation"
            : line.Offset < pending.Watermark ? "written before the request (watermark)"
            : pending.ContextId != _contextId ? "other ship context"
            : null;
        if (ignored is not null)
        {
            LogAtcIgnored(_logger, line.Offset, ignored);
            return;
        }

        pending.Timer.Dispose();
        _hangar = null;
        _hangarState = _hangarState with { Status = HangarRequestStatus.Answered, UpdatedAt = completed.At };
        LogAtcAnswered(_logger, pending.CommandId, line.Offset);
        Publish(urgent: false);
    }

    private void OnHangarTimeout(long token)
    {
        lock (_gate)
        {
            if (_hangar is not { } pending || pending.Token != token)
            {
                return;
            }

            pending.Timer.Dispose();
            _hangar = null;
            _hangarState = _hangarState with { Status = HangarRequestStatus.Timeout, UpdatedAt = _time.GetUtcNow() };
            LogAtcTimeout(_logger, pending.CommandId);
            Publish(urgent: false);
        }
    }

    private bool EndSession(DateTimeOffset at)
    {
        if (_active is null && _status == ShipContextStatus.NoShip && _lastReason == ShipContextReason.GameEnded)
        {
            return false; // SystemQuit followed by process exit: one transition only.
        }

        _active = null;
        Transition(ShipContextStatus.NoShip, ShipContextReason.GameEnded, at);
        return true;
    }

    /// <summary>A real context transition: new revision, QT and ATC cleared, published urgently.</summary>
    private void Transition(ShipContextStatus status, ShipContextReason reason, DateTimeOffset at)
    {
        _revision++;
        _status = status;
        _lastReason = reason;
        _contextObservedAt = at;
        _qt = QtTargetState.Unknown;
        _hangar?.Timer.Dispose();
        _hangar = null;
        _hangarState = HangarRequestState.Idle;
        LogTransition(_logger, _revision, status, reason, _active?.Name ?? "none", _lastPrefix ?? "none");
        Publish(urgent: true);
    }

    /// <summary>Binding model/entityId enriches the ship without a new revision (spec 41.8).</summary>
    private bool EnrichIfBound()
    {
        if (TryBind())
        {
            Publish(urgent: false);
        }

        return false;
    }

    /// <summary>
    /// Binds the channel ship to exactly one observed vehicle whose model matches the ship name and which
    /// was spawned for the same owner within 10 minutes of the join (rule validated by the Windows MVP).
    /// Several candidates leave the id unknown; a binding never proves ownership.
    /// </summary>
    private bool TryBind()
    {
        if (_active is not { EntityId: null } active)
        {
            return false;
        }

        VehicleObserved? candidate = null;
        foreach (var observed in _observations.Values)
        {
            if (!GameLogParser.ShipNameMatchesModel(active.Name, observed.Model)
                || !_spawns.TryGetValue(observed.EntityId, out var spawn)
                || !string.Equals(spawn.Owner, active.Owner, StringComparison.Ordinal)
                || (spawn.At - active.JoinedAt).Duration() > TimeSpan.FromMinutes(10))
            {
                continue;
            }

            if (candidate is not null && candidate.EntityId != observed.EntityId)
            {
                LogBindAmbiguous(_logger, candidate.EntityId, observed.EntityId);
                return false; // Ambiguous: keep the id unknown.
            }

            candidate = observed;
        }

        if (candidate is null)
        {
            return false;
        }

        _active = active with { Model = candidate.Model, EntityId = candidate.EntityId };
        LogBound(_logger, candidate.Model, candidate.EntityId);
        return true;
    }

    private void Publish(bool urgent)
    {
        var ship = _active is { } active
            ? new ShipState(active.Name, active.Model, active.EntityId, active.Prefix, Confidence.Assumed, active.JoinedAt)
            : null;
        var context = new ShipContextState(_revision, _status, _lastReason, _contextId, _lastPrefix, _contextObservedAt);
        var qt = _qt;
        var hangar = _hangarState;

        _store.Update(
            s => s with { Ship = ship, ShipContext = context, QtTarget = qt, HangarRequest = hangar },
            urgent);
    }

    private static void Remember<T>(Dictionary<string, T> map, Queue<string> order, string key, T value)
    {
        if (!map.ContainsKey(key))
        {
            order.Enqueue(key);
            if (order.Count > MaxRemembered)
            {
                map.Remove(order.Dequeue());
            }
        }

        map[key] = value;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Game.log: {Event} at offset {Offset}")]
    private static partial void LogRecognized(ILogger logger, string @event, long offset);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Game.log reset: new generation {Generation}")]
    private static partial void LogGenerationReset(ILogger logger, Guid generation);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ship context revision {Revision}: {Status} / {Reason}, ship {Ship}, prefix {Prefix}")]
    private static partial void LogTransition(ILogger logger, long revision, ShipContextStatus status, ShipContextReason reason, string ship, string prefix);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ship bound to model {Model}, entity {EntityId}")]
    private static partial void LogBound(ILogger logger, string model, string entityId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ship binding ambiguous (entities {First} and {Second}): entity stays unknown")]
    private static partial void LogBindAmbiguous(ILogger logger, string first, string second);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ClearDriver for entity {EntityId} ignored: {Reason}")]
    private static partial void LogClearDriverIgnored(ILogger logger, string entityId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ClearDriver of our ship (entity {EntityId}): input released, ship context kept")]
    private static partial void LogClearDriverReleased(ILogger logger, string entityId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Left channel of {Ship} ignored: not the current ship channel")]
    private static partial void LogChannelLeftIgnored(ILogger logger, string ship);

    [LoggerMessage(Level = LogLevel.Debug, Message = "QT destination {Target} accepted for entity {EntityId}")]
    private static partial void LogQtAccepted(ILogger logger, string target, string entityId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "QT target of entity {EntityId} ignored (our ship entity: {Bound})")]
    private static partial void LogQtIgnored(ILogger logger, string entityId, string bound);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ATC pending for command {CommandId}, watermark {Watermark}")]
    private static partial void LogAtcPending(ILogger logger, Guid commandId, long watermark);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ATC answered for command {CommandId} by offset {Offset}")]
    private static partial void LogAtcAnswered(ILogger logger, Guid commandId, long offset);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Hangar notification at offset {Offset} ignored: {Reason}")]
    private static partial void LogAtcIgnored(ILogger logger, long offset, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ATC timeout for command {CommandId}")]
    private static partial void LogAtcTimeout(ILogger logger, Guid commandId);

    private sealed record ActiveShip(string Name, string Owner, string Prefix, DateTimeOffset JoinedAt)
    {
        public string? Model { get; init; }

        public string? EntityId { get; init; }
    }

    private sealed record PendingHangar(long Token, Guid CommandId, Guid? ContextId, Guid Generation, long Watermark, ITimer Timer);
}
