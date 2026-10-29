using System.Globalization;
using Csmfd.Core.Input;
using Csmfd.Protocol;

namespace Csmfd.Tests.Core;

/// <summary>Records injected input as "ms:key" + "v" (down) / "^" (up), relative to creation time.</summary>
internal sealed class FakeInputSink(TimeProvider time) : IInputSink
{
    private readonly long _start = time.GetTimestamp();

    public List<string> Timeline { get; } = [];

    public List<InputEvent[]> Batches { get; } = [];

    /// <summary>Optional per-call insertion results, consumed before InsertLimit.</summary>
    public Queue<int> InsertResults { get; } = new();

    /// <summary>When set, at most this many events are inserted per Send call (simulates UIPI/partial failure).</summary>
    public int? InsertLimit { get; set; }

    public bool FailText { get; set; }

    public bool TextReleaseFailed { get; set; }

    public int Send(ReadOnlySpan<InputEvent> events)
    {
        Batches.Add(events.ToArray());
        var count = Math.Min(events.Length, InsertResults.TryDequeue(out var result) ? result : InsertLimit ?? int.MaxValue);
        for (var i = 0; i < count; i++)
        {
            var description = events[i].WheelDelta != 0
                ? string.Create(CultureInfo.InvariantCulture, $"wheel({events[i].WheelDelta})")
                : $"{events[i].Key}{(events[i].Down ? "v" : "^")}";
            Timeline.Add($"{Now()}:{description}");
        }

        return count;
    }

    public bool SendText(string text)
    {
        if (FailText)
        {
            return false;
        }

        Timeline.Add($"{Now()}:text({text})");
        return true;
    }

    private string Now() =>
        ((long)time.GetElapsedTime(_start).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Keeps every formatted message with its level; all levels are enabled.</summary>
internal sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
{
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

internal sealed class FakeGameWindow : IGameWindow
{
    public GameFocus Focus { get; set; } = GameFocus.Focused;

    public GameFocus Check() => Focus;
}

internal sealed class FakeKeyboard : IPhysicalKeyboard
{
    public HashSet<string> Down { get; } = [];

    public bool IsDown(string key) => Down.Contains(key);
}

internal sealed class RecordingOutput : IExecutorOutput
{
    public List<(Guid Connection, Guid Command, AckStatus Status, RejectReason? Reason)> Acks { get; } = [];

    public List<(Guid Connection, ErrorCode Code, Guid Command, CancelReason Reason)> Errors { get; } = [];

    public List<(Guid Command, string Action)> Started { get; } = [];

    public int BlockedCount { get; private set; }

    public void Ack(Guid connectionId, Guid commandId, AckStatus status, RejectReason? reason = null) =>
        Acks.Add((connectionId, commandId, status, reason));

    public void InputError(Guid connectionId, ErrorCode code, Guid commandId, CancelReason reason) =>
        Errors.Add((connectionId, code, commandId, reason));

    public void InputStarted(Guid connectionId, Guid commandId, ActionSpec spec) =>
        Started.Add((commandId, spec.Action));

    public void InputBlocked() => BlockedCount++;

    public (AckStatus Status, RejectReason? Reason) AckFor(Guid command)
    {
        var matches = Acks.Where(a => a.Command == command).ToList();
        var ack = Assert.Single(matches);
        return (ack.Status, ack.Reason);
    }
}
