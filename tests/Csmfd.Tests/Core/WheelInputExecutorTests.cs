using Csmfd.Core.Input;
using Csmfd.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Csmfd.Tests.Core;

public sealed class WheelInputExecutorTests : IDisposable
{
    private static readonly Guid Connection = Guid.Parse("00000000-0000-4000-8000-000000000001");
    private readonly FakeTimeProvider _time = new();
    private readonly FakeGameWindow _game = new();
    private readonly FakeKeyboard _keyboard = new();
    private readonly RecordingOutput _output = new();
    private readonly FakeInputSink _sink;
    private readonly InputExecutor _executor;

    public WheelInputExecutorTests()
    {
        _sink = new FakeInputSink(_time);
        _executor = new InputExecutor(_time, _sink, _game, _keyboard, new EmoteAllowlist(["wave"]), _output);
    }

    public void Dispose() => _executor.Dispose();

    [Theory]
    [InlineData("speedLimiter", "alt", 1)]
    [InlineData("speedLimiter", "alt", -1)]
    [InlineData("accelerationLimiter", "ralt", 1)]
    [InlineData("accelerationLimiter", "ralt", -1)]
    public void StepIsOneBalancedBatchWithNoTimerOrExtraAck(string action, string modifier, int step)
    {
        var id = Submit(action, step);

        Assert.Equal([new InputEvent(modifier, true), InputEvent.Wheel(step), new InputEvent(modifier, false)], Assert.Single(_sink.Batches));
        Assert.Equal((AckStatus.Sent, (RejectReason?)null), _output.AckFor(id));
        Assert.False(_executor.HasActiveInput);
        Assert.False(_executor.IsBlocked);
        _executor.CancelConnection(Connection, CancelReason.Disconnected);
        _executor.CancelAll(CancelReason.FocusLost);
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Single(_sink.Batches);
        Assert.Single(_output.Acks);
        Assert.Empty(_output.Errors);
        Assert.Empty(_output.Started);
    }

    [Theory]
    [InlineData(GameFocus.Background, RejectReason.GameNotFocused)]
    [InlineData(GameFocus.NotRunning, RejectReason.GameNotRunning)]
    public void UnfocusedOrMissingGameGetsNoInput(GameFocus focus, RejectReason reason)
    {
        _game.Focus = focus;
        var id = Submit();
        Assert.Equal((AckStatus.Rejected, reason), _output.AckFor(id));
        Assert.Empty(_sink.Batches);
    }

    [Theory]
    [InlineData("ctrl")]
    [InlineData("shift")]
    [InlineData("alt")]
    [InlineData("ralt")]
    public void AnyPhysicalModifierConflictsWithEitherLimiter(string modifier)
    {
        _keyboard.Down.Add(modifier);
        foreach (var action in WheelActionCatalog.All.Keys)
        {
            var id = Submit(action);
            Assert.Equal((AckStatus.Rejected, RejectReason.Busy), _output.AckFor(id));
        }
        Assert.Empty(_sink.Batches);
    }

    [Theory]
    [InlineData("paused", RejectReason.InputPaused)]
    [InlineData("locked", RejectReason.GameNotFocused)]
    [InlineData("expired", RejectReason.Expired)]
    [InlineData("disposed", RejectReason.Cancelled)]
    public void ExistingAdmissionChecksApplyToWheel(string condition, RejectReason reason)
    {
        var received = _time.GetTimestamp();
        switch (condition)
        {
            case "paused": _executor.SetPaused(true); break;
            case "locked": _executor.SetSessionLocked(true); break;
            case "expired": _time.Advance(TimeSpan.FromMilliseconds(251)); break;
            case "disposed": _executor.Dispose(); break;
        }
        var command = Wheel();
        _executor.Submit(Connection, command, received);
        Assert.Equal((AckStatus.Rejected, reason), _output.AckFor(command.Id));
        Assert.Empty(_sink.Batches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveKeyOrChatRejectsWheelWithoutChangingTheGesture(bool chat)
    {
        CommandMessage active = chat
            ? new ChatCommand(Guid.NewGuid(), "/wave")
            : new KeyCommand(Guid.NewGuid(), ActionCatalog.All["landingGear"], KeyPhase.Down, null);
        _executor.Submit(Connection, active, _time.GetTimestamp());
        var wheel = Submit();
        Assert.Equal((AckStatus.Rejected, RejectReason.Busy), _output.AckFor(wheel));
        Assert.Single(_sink.Batches);
        Assert.True(_executor.HasActiveInput);

        _executor.CancelConnection(Connection, CancelReason.Disconnected);
        Assert.False(_executor.HasActiveInput);
        var next = Submit();
        Assert.Equal(AckStatus.Sent, _output.AckFor(next).Status);
    }

    [Theory]
    [InlineData("speedLimiter", "alt", 0)]
    [InlineData("speedLimiter", "alt", 1)]
    [InlineData("speedLimiter", "alt", 2)]
    [InlineData("accelerationLimiter", "ralt", 0)]
    [InlineData("accelerationLimiter", "ralt", 1)]
    [InlineData("accelerationLimiter", "ralt", 2)]
    public void PartialInsertionReleasesOnlyInsertedModifierAndNeverReplaysWheel(string action, string modifier, int inserted)
    {
        _sink.InsertResults.Enqueue(inserted);
        var id = Submit(action);

        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(id));
        Assert.Equal(inserted == 0 ? 1 : 2, _sink.Batches.Count);
        if (inserted > 0)
        {
            Assert.Equal([new InputEvent(modifier, false)], _sink.Batches[1]);
            Assert.EndsWith(modifier + "^", _sink.Timeline[^1], StringComparison.Ordinal);
        }
        Assert.Equal(inserted == 2 ? 1 : 0, _sink.Timeline.Count(e => e.Contains("wheel(", StringComparison.Ordinal)));
        Assert.False(_executor.HasActiveInput);
        Assert.False(_executor.IsBlocked);
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Single(_output.Acks);
        Assert.Equal(inserted == 0 ? 1 : 2, _sink.Batches.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedCleanupBlocksWheelAndKeyUntilLocalReset(int inserted)
    {
        _sink.InsertResults.Enqueue(inserted);
        _sink.InsertResults.Enqueue(0);
        var failed = Submit();
        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(failed));
        Assert.True(_executor.IsBlocked);
        Assert.Equal(1, _output.BlockedCount);

        var wheel = Submit();
        var key = new KeyCommand(Guid.NewGuid(), ActionCatalog.All["lights"], KeyPhase.Down, null);
        _executor.Submit(Connection, key, _time.GetTimestamp());
        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(wheel));
        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(key.Id));
        Assert.Equal(2, _sink.Batches.Count);

        _executor.ResetBlock();
        var next = Submit();
        Assert.Equal(AckStatus.Sent, _output.AckFor(next).Status);
        Assert.Equal(3, _sink.Batches.Count);
        Assert.False(_executor.HasActiveInput);
    }

    [Fact]
    public void ConsecutiveStepsRecheckFocusAndHaveNoDelayedWork()
    {
        Assert.Equal(AckStatus.Sent, _output.AckFor(Submit()).Status);
        _game.Focus = GameFocus.Background;
        Assert.Equal((AckStatus.Rejected, RejectReason.GameNotFocused), _output.AckFor(Submit()));
        _game.Focus = GameFocus.Focused;
        Assert.Equal(AckStatus.Sent, _output.AckFor(Submit("accelerationLimiter", -1)).Status);
        Assert.Equal(2, _sink.Batches.Count);
    }

    private static WheelCommand Wheel(string action = "speedLimiter", int step = 1) =>
        new(Guid.NewGuid(), WheelActionCatalog.All[action], step);

    private Guid Submit(string action = "speedLimiter", int step = 1)
    {
        var command = Wheel(action, step);
        _executor.Submit(Connection, command, _time.GetTimestamp());
        return command.Id;
    }
}
