using System.Globalization;
using Csmfd.Core.Input;
using Csmfd.Protocol;

namespace Csmfd.Tests.Core;

/// <summary>Records injected input as "ms:key" + "v" (down) / "^" (up), relative to creation time.</summary>
internal sealed class FakeInputSink(TimeProvider time) : IInputSink
{
    private readonly long _start = time.GetTimestamp();

    public List<string> Timeline { get; } = [];

    /// <summary>When set, at most this many events are inserted per Send call (simulates UIPI/partial failure).</summary>
    public int? InsertLimit { get; set; }

    public bool FailText { get; set; }

    public int Send(ReadOnlySpan<InputEvent> events)
    {
        var count = Math.Min(events.Length, InsertLimit ?? int.MaxValue);
        for (var i = 0; i < count; i++)
        {
            Timeline.Add($"{Now()}:{events[i].Key}{(events[i].Down ? "v" : "^")}");
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
