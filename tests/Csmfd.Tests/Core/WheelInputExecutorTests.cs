using Csmfd.Core.Input;
using Csmfd.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Csmfd.Tests.Core;

public sealed class WheelInputExecutorTests : IDisposable
{
    private static readonly Guid Connection = Guid.Parse("00000000-0000-4000-8000-000000000001");
    private static readonly TimeSpan Lead = new InputExecutorOptions().WheelModifierLead;
    private static readonly TimeSpan Trail = new InputExecutorOptions().WheelModifierTrail;
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
    public void StepHoldsTheModifierAroundTheWheelAndAcksAtTheEnd(string action, string modifier, int step)
    {
        var id = Submit(action, step);
        Assert.Equal([$"0:{modifier}v"], _sink.Timeline);
        Assert.Empty(_output.Acks);
        Assert.True(_executor.HasActiveInput);

        _time.Advance(Lead);
        Assert.Equal([$"0:{modifier}v", $"50:wheel({step * 120})"], _sink.Timeline);
        Assert.Empty(_output.Acks);

        _time.Advance(Trail);
        Assert.Equal([$"0:{modifier}v", $"50:wheel({step * 120})", $"100:{modifier}^"], _sink.Timeline);
        Assert.Equal(
            [[new InputEvent(modifier, true)], [InputEvent.Wheel(step)], [new InputEvent(modifier, false)]],
            _sink.Batches);
        Assert.Equal((AckStatus.Sent, (RejectReason?)null), _output.AckFor(id));
        Assert.False(_executor.HasActiveInput);
        Assert.False(_executor.IsBlocked);

        // A completed step leaves no timer or later message behind.
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(3, _sink.Timeline.Count);
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
        CompleteStep();
        Assert.Equal(AckStatus.Sent, _output.AckFor(next).Status);
    }

    [Fact]
    public void StepInProgressRejectsAnotherStepAsBusyWithoutQueueing()
    {
        var first = Submit();
        var second = Submit("accelerationLimiter", -1);
        Assert.Equal((AckStatus.Rejected, RejectReason.Busy), _output.AckFor(second));

        CompleteStep();
        Assert.Equal(AckStatus.Sent, _output.AckFor(first).Status);
        Assert.Equal(["0:altv", "50:wheel(120)", "100:alt^"], _sink.Timeline);
    }

    [Fact]
    public void FocusLostDuringLeadReleasesTheModifierWithoutScrolling()
    {
        var id = Submit();
        _game.Focus = GameFocus.Background;
        _time.Advance(Lead);

        Assert.Equal(["0:altv", "50:alt^"], _sink.Timeline);
        Assert.Equal((AckStatus.Rejected, RejectReason.GameNotFocused), _output.AckFor(id));
        Assert.False(_executor.HasActiveInput);
        Assert.False(_executor.IsBlocked);
    }

    [Fact]
    public void ModifierPressFailureRejectsWithoutAnyWheel()
    {
        _sink.InsertResults.Enqueue(0);
        var id = Submit();

        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(id));
        Assert.Single(_sink.Batches);
        Assert.Empty(_sink.Timeline);
        Assert.False(_executor.HasActiveInput);
        Assert.False(_executor.IsBlocked);
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Single(_sink.Batches);
    }

    [Theory]
    [InlineData("speedLimiter", "alt")]
    [InlineData("accelerationLimiter", "ralt")]
    public void WheelInsertionFailureReleasesOnlyTheModifierAndNeverReplays(string action, string modifier)
    {
        _sink.InsertResults.Enqueue(1);
        _sink.InsertResults.Enqueue(0);
        var id = Submit(action);
        _time.Advance(Lead);

        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(id));
        Assert.Equal([$"0:{modifier}v", $"50:{modifier}^"], _sink.Timeline);
        Assert.Equal(3, _sink.Batches.Count);
        Assert.False(_executor.HasActiveInput);
        Assert.False(_executor.IsBlocked);
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(3, _sink.Batches.Count);
        Assert.Single(_output.Acks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedModifierReleaseBlocksWheelAndKeyUntilLocalReset(bool wheelInserted)
    {
        _sink.InsertResults.Enqueue(1);
        _sink.InsertResults.Enqueue(wheelInserted ? 1 : 0);
        _sink.InsertResults.Enqueue(0);
        var failed = Submit();
        _time.Advance(Lead);
        if (wheelInserted)
        {
            _time.Advance(Trail);
        }

        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(failed));
        Assert.True(_executor.IsBlocked);
        Assert.Equal(1, _output.BlockedCount);
        Assert.False(_executor.HasActiveInput);

        var wheel = Submit();
        var key = new KeyCommand(Guid.NewGuid(), ActionCatalog.All["lights"], KeyPhase.Down, null);
        _executor.Submit(Connection, key, _time.GetTimestamp());
        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(wheel));
        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(key.Id));
        Assert.Equal(3, _sink.Batches.Count);

        _executor.ResetBlock();
        var next = Submit();
        CompleteStep();
        Assert.Equal(AckStatus.Sent, _output.AckFor(next).Status);
        Assert.Equal(6, _sink.Batches.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SafetyCancelReleasesTheModifierAndRejectsTheStepAsCancelled(bool wheelSent)
    {
        var id = Submit();
        if (wheelSent)
        {
            _time.Advance(Lead);
        }

        _executor.CancelAll(CancelReason.FocusLost);

        var releasedAt = wheelSent ? "50" : "0";
        Assert.Equal($"{releasedAt}:alt^", _sink.Timeline[^1]);
        Assert.Equal(wheelSent ? 1 : 0, _sink.Timeline.Count(e => e.Contains("wheel(", StringComparison.Ordinal)));
        Assert.Equal((AckStatus.Rejected, RejectReason.Cancelled), _output.AckFor(id));
        Assert.Empty(_output.Errors);
        Assert.False(_executor.HasActiveInput);

        // The stale timer must not send a wheel or a second release.
        var count = _sink.Timeline.Count;
        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(count, _sink.Timeline.Count);
        Assert.Single(_output.Acks);
    }

    [Fact]
    public void ReleaseAllEndsTheStepAndAcksBoth()
    {
        var id = Submit();
        var releaseAll = new ReleaseAllCommand(Guid.NewGuid());
        _executor.Submit(Connection, releaseAll, _time.GetTimestamp());

        Assert.Equal(["0:altv", "0:alt^"], _sink.Timeline);
        Assert.Equal((AckStatus.Rejected, RejectReason.Cancelled), _output.AckFor(id));
        Assert.Equal(AckStatus.Sent, _output.AckFor(releaseAll.Id).Status);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _sink.Timeline.Count);
    }

    [Fact]
    public void ConsecutiveStepsRecheckFocus()
    {
        var first = Submit();
        CompleteStep();
        Assert.Equal(AckStatus.Sent, _output.AckFor(first).Status);

        _game.Focus = GameFocus.Background;
        Assert.Equal((AckStatus.Rejected, RejectReason.GameNotFocused), _output.AckFor(Submit()));

        _game.Focus = GameFocus.Focused;
        var third = Submit("accelerationLimiter", -1);
        CompleteStep();
        Assert.Equal(AckStatus.Sent, _output.AckFor(third).Status);
        Assert.Equal(6, _sink.Batches.Count);
    }

    /// <summary>Each stage timer is created when the previous one fires, so time advances per stage.</summary>
    private void CompleteStep()
    {
        _time.Advance(Lead);
        _time.Advance(Trail);
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
