using Csmfd.Core.Input;
using Csmfd.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Csmfd.Tests.Core;

public sealed class InputExecutorTests : IDisposable
{
    private static readonly Guid Connection = Guid.Parse("00000000-0000-4000-8000-000000000001");
    private static readonly Guid OtherConnection = Guid.Parse("00000000-0000-4000-8000-000000000002");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero));
    private readonly FakeGameWindow _game = new();
    private readonly FakeKeyboard _keyboard = new();
    private readonly RecordingOutput _output = new();
    private readonly FakeInputSink _sink;
    private readonly InputExecutor _executor;

    public InputExecutorTests()
    {
        _sink = new FakeInputSink(_time);
        _executor = CreateExecutor(new InputExecutorOptions());
    }

    public void Dispose() => _executor.Dispose();

    [Fact]
    public void TapPressesModifiersBeforeKeyAndAcks()
    {
        var down = Submit("requestLanding", KeyPhase.Down);

        Assert.Equal(["0:altv", "0:nv"], _sink.Timeline);
        Assert.Equal((AckStatus.Sent, (RejectReason?)null), _output.AckFor(down));
        Assert.Equal([(down, "requestLanding")], _output.Started);
    }

    [Fact]
    public void QuickUpIsDeferredToTapMinimumAndAckedAfterRelease()
    {
        Submit("requestLanding", KeyPhase.Down);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        var up = Submit("requestLanding", KeyPhase.Up);

        Assert.DoesNotContain(_output.Acks, a => a.Command == up);
        _time.Advance(TimeSpan.FromMilliseconds(49));
        Assert.Equal(2, _sink.Timeline.Count);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(["0:altv", "0:nv", "60:n^", "60:alt^"], _sink.Timeline);
        Assert.Equal(AckStatus.Sent, _output.AckFor(up).Status);
        Assert.False(_executor.HasActiveInput);
    }

    [Fact]
    public void SlowUpReleasesImmediately()
    {
        Submit("lights", KeyPhase.Down);
        _time.Advance(TimeSpan.FromMilliseconds(300));
        var up = Submit("lights", KeyPhase.Up);

        Assert.Equal(["0:lv", "300:l^"], _sink.Timeline);
        Assert.Equal(AckStatus.Sent, _output.AckFor(up).Status);
    }

    [Fact]
    public void SecondUpWhileReleaseIsScheduledIsIgnored()
    {
        Submit("lights", KeyPhase.Down);
        var first = Submit("lights", KeyPhase.Up);
        var second = Submit("lights", KeyPhase.Up);

        Assert.Equal(AckStatus.Ignored, _output.AckFor(second).Status);
        _time.Advance(TimeSpan.FromMilliseconds(60));
        Assert.Equal(AckStatus.Sent, _output.AckFor(first).Status);
    }

    [Fact]
    public void HoldReleasesAtDeadlineWithoutExtraMessages()
    {
        var hold = Submit("quantumEngage", KeyPhase.Hold, ms: 1500);
        _time.Advance(TimeSpan.FromMilliseconds(1499));
        Assert.Equal(["0:mouse1v"], _sink.Timeline);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(["0:mouse1v", "1500:mouse1^"], _sink.Timeline);
        Assert.Single(_output.Acks);
        Assert.Equal(AckStatus.Sent, _output.AckFor(hold).Status);
        Assert.Empty(_output.Errors);
    }

    [Fact]
    public void UpCancelsHoldImmediately()
    {
        Submit("quantumEngage", KeyPhase.Hold, ms: 1500);
        _time.Advance(TimeSpan.FromMilliseconds(20));
        var up = Submit("quantumEngage", KeyPhase.Up);

        Assert.Equal(["0:mouse1v", "20:mouse1^"], _sink.Timeline);
        Assert.Equal(AckStatus.Sent, _output.AckFor(up).Status);
        Assert.Empty(_output.Errors);
    }

    [Fact]
    public void StaleTimerDoesNotReleaseNextHold()
    {
        Submit("masterMode", KeyPhase.Hold, ms: 1000);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        Submit("masterMode", KeyPhase.Up);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Submit("masterMode", KeyPhase.Hold, ms: 1000);

        _time.Advance(TimeSpan.FromMilliseconds(400)); // old deadline (1000 ms) passes
        Assert.Equal(["0:bv", "500:b^", "600:bv"], _sink.Timeline);

        _time.Advance(TimeSpan.FromMilliseconds(600));
        Assert.Equal(["0:bv", "500:b^", "600:bv", "1600:b^"], _sink.Timeline);
    }

    [Theory]
    [InlineData("landingGear", KeyPhase.Down, null)]
    [InlineData("masterMode", KeyPhase.Hold, 500)]
    public void NewGestureWhileActiveIsBusy(string action, KeyPhase phase, int? ms)
    {
        Submit("autoLand", KeyPhase.Hold, ms: 1000);
        var second = Submit(action, phase, ms);

        Assert.Equal((AckStatus.Rejected, RejectReason.Busy), _output.AckFor(second));
        Assert.Equal(["0:nv"], _sink.Timeline);
    }

    [Theory]
    [InlineData(GameFocus.Background, RejectReason.GameNotFocused)]
    [InlineData(GameFocus.NotRunning, RejectReason.GameNotRunning)]
    public void RejectsWhenGameIsNotFocused(GameFocus focus, RejectReason reason)
    {
        _game.Focus = focus;
        var down = Submit("lights", KeyPhase.Down);

        Assert.Equal((AckStatus.Rejected, reason), _output.AckFor(down));
        Assert.Empty(_sink.Timeline);
    }

    [Theory]
    [InlineData("alt")]
    [InlineData("ctrl")]
    [InlineData("n")]
    public void PhysicallyHeldConflictingKeyIsBusy(string physicalKey)
    {
        _keyboard.Down.Add(physicalKey);
        var down = Submit("landingGear", KeyPhase.Down);

        Assert.Equal((AckStatus.Rejected, RejectReason.Busy), _output.AckFor(down));
        Assert.Empty(_sink.Timeline);
    }

    [Fact]
    public void ExpiredCommandIsRejected()
    {
        var received = _time.GetTimestamp();
        _time.Advance(TimeSpan.FromMilliseconds(251));
        var id = Guid.NewGuid();
        _executor.Submit(Connection, Key(id, "lights", KeyPhase.Down), received);

        Assert.Equal((AckStatus.Rejected, RejectReason.Expired), _output.AckFor(id));
    }

    [Fact]
    public void PauseReleasesActiveInputAndRejectsNewGestures()
    {
        var hold = Submit("exitSeat", KeyPhase.Hold, ms: 1000);
        _executor.SetPaused(true);

        Assert.Equal(["0:yv", "0:y^"], _sink.Timeline);
        Assert.Equal([(Connection, ErrorCode.InputCancelled, hold, CancelReason.Paused)], _output.Errors);

        var next = Submit("lights", KeyPhase.Down);
        Assert.Equal((AckStatus.Rejected, RejectReason.InputPaused), _output.AckFor(next));
    }

    [Fact]
    public void SessionLockReleasesActiveInputWithSessionLockedReason()
    {
        var hold = Submit("quantumEngage", KeyPhase.Hold, ms: 1500);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        _executor.SetSessionLocked(true);

        Assert.Equal(["0:mouse1v", "200:mouse1^"], _sink.Timeline);
        Assert.Equal([(Connection, ErrorCode.InputCancelled, hold, CancelReason.SessionLocked)], _output.Errors);
        Assert.False(_executor.HasActiveInput);
    }

    [Fact]
    public void LockedSessionRejectsNewGesturesEvenIfTheGameLooksFocused()
    {
        // The secure desktop can leave the game as the foreground window: the lock flag decides.
        _executor.SetSessionLocked(true);

        var tap = Submit("lights", KeyPhase.Down);
        var chat = Chat("/wave");

        Assert.Equal((AckStatus.Rejected, RejectReason.GameNotFocused), _output.AckFor(tap));
        Assert.Equal((AckStatus.Rejected, RejectReason.GameNotFocused), _output.AckFor(chat));
        Assert.Empty(_sink.Timeline);
    }

    [Fact]
    public void UnlockedSessionAcceptsInputAgain()
    {
        _executor.SetSessionLocked(true);
        _executor.SetSessionLocked(false);

        var tap = Submit("lights", KeyPhase.Down);

        Assert.Equal(AckStatus.Sent, _output.AckFor(tap).Status);
        Assert.Equal(["0:lv"], _sink.Timeline);
    }

    [Fact]
    public void SafetyCancelBypassesTapMinimum()
    {
        var down = Submit("requestLanding", KeyPhase.Down);
        _time.Advance(TimeSpan.FromMilliseconds(5));
        _executor.CancelAll(CancelReason.FocusLost);

        Assert.Equal(["0:altv", "0:nv", "5:n^", "5:alt^"], _sink.Timeline);
        Assert.Equal([(Connection, ErrorCode.InputCancelled, down, CancelReason.FocusLost)], _output.Errors);
    }

    [Fact]
    public void SafetyCancelFulfillsPendingUpInsteadOfReportingCancel()
    {
        Submit("lights", KeyPhase.Down);
        var up = Submit("lights", KeyPhase.Up);
        _executor.CancelAll(CancelReason.FocusLost);

        Assert.Equal(AckStatus.Sent, _output.AckFor(up).Status);
        Assert.Empty(_output.Errors);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(["0:lv", "0:l^"], _sink.Timeline);
    }

    [Fact]
    public void TapWithoutUpIsAutoReleasedAtCeiling()
    {
        var down = Submit("lights", KeyPhase.Down);
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(["0:lv", "10000:l^"], _sink.Timeline);
        Assert.Equal([(Connection, ErrorCode.AutoReleased, down, CancelReason.Watchdog)], _output.Errors);
    }

    [Fact]
    public void UpWithoutMatchingGestureIsIgnored()
    {
        var orphan = Submit("lights", KeyPhase.Up);
        Submit("lights", KeyPhase.Down);
        var wrongAction = Submit("vtol", KeyPhase.Up);
        var otherConnection = Submit("lights", KeyPhase.Up, connection: OtherConnection);

        Assert.Equal(AckStatus.Ignored, _output.AckFor(orphan).Status);
        Assert.Equal(AckStatus.Ignored, _output.AckFor(wrongAction).Status);
        Assert.Equal(AckStatus.Ignored, _output.AckFor(otherConnection).Status);
        Assert.Equal(["0:lv"], _sink.Timeline);
    }

    [Fact]
    public void ReleaseAllReleasesOwnInputOnly()
    {
        var hold = Submit("selfDestruct", KeyPhase.Hold, ms: 3000);

        var foreign = ReleaseAll(OtherConnection);
        Assert.Equal(AckStatus.Ignored, _output.AckFor(foreign).Status);

        var own = ReleaseAll(Connection);
        Assert.Equal(AckStatus.Sent, _output.AckFor(own).Status);
        Assert.Equal([(Connection, ErrorCode.InputCancelled, hold, CancelReason.ReleaseAll)], _output.Errors);
        Assert.Equal(["0:backspacev", "0:backspace^"], _sink.Timeline);

        var nothing = ReleaseAll(Connection);
        Assert.Equal(AckStatus.Ignored, _output.AckFor(nothing).Status);
    }

    [Fact]
    public void ConnectionCancelOnlyAffectsThatConnection()
    {
        Submit("lights", KeyPhase.Down);
        _executor.CancelConnection(OtherConnection, CancelReason.Disconnected);
        Assert.True(_executor.HasActiveInput);

        _executor.CancelConnection(Connection, CancelReason.ClientReplaced);
        Assert.False(_executor.HasActiveInput);
        Assert.Equal(CancelReason.ClientReplaced, Assert.Single(_output.Errors).Reason);
    }

    [Fact]
    public void PartialInsertionReleasesPressedKeysAndRejects()
    {
        _sink.InsertLimit = 1;
        var down = Submit("requestLanding", KeyPhase.Down);

        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(down));
        Assert.Equal(["0:altv", "0:alt^"], _sink.Timeline);
        Assert.False(_executor.HasActiveInput);
        Assert.Empty(_output.Started);
    }

    [Fact]
    public void FailedReleaseBlocksInputUntilLocalReset()
    {
        Submit("lights", KeyPhase.Down);
        _sink.InsertLimit = 0;
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var up = Submit("lights", KeyPhase.Up);

        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(up));
        Assert.True(_executor.IsBlocked);
        Assert.Equal(1, _output.BlockedCount);

        _sink.InsertLimit = null;
        var blocked = Submit("vtol", KeyPhase.Down);
        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(blocked));

        _executor.ResetBlock();
        var allowed = Submit("vtol", KeyPhase.Down);
        Assert.Equal(AckStatus.Sent, _output.AckFor(allowed).Status);
    }

    [Fact]
    public void ChatRunsFullSequenceAndAcksAfterEnter()
    {
        var chat = Chat("/dance3");
        Assert.DoesNotContain(_output.Acks, a => a.Command == chat);

        _time.Advance(TimeSpan.FromMilliseconds(60));
        _time.Advance(TimeSpan.FromMilliseconds(300));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _time.Advance(TimeSpan.FromMilliseconds(60));

        Assert.Equal(["0:enterv", "60:enter^", "360:text(/dance3)", "460:enterv", "520:enter^"], _sink.Timeline);
        Assert.Equal(AckStatus.Sent, _output.AckFor(chat).Status);
        Assert.False(_executor.HasActiveInput);
    }

    [Fact]
    public void ChatUsesConfiguredOpenKey()
    {
        using var executor = CreateExecutor(new InputExecutorOptions { ChatKey = ChatKey.F12 });
        var id = Guid.NewGuid();
        executor.Submit(Connection, new ChatCommand(id, "/wave"), _time.GetTimestamp());

        // FakeTimeProvider moves the clock to the end before firing, so chained timers need one step each.
        foreach (var step in new[] { 60, 300, 100, 60 })
        {
            _time.Advance(TimeSpan.FromMilliseconds(step));
        }

        Assert.Equal(["0:f12v", "60:f12^", "360:text(/wave)", "460:enterv", "520:enter^"], _sink.Timeline);
    }

    [Fact]
    public void ChatLosingFocusNeverPressesEnter()
    {
        var chat = Chat("/wave");
        _time.Advance(TimeSpan.FromMilliseconds(60));
        _game.Focus = GameFocus.Background;
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(["0:enterv", "60:enter^"], _sink.Timeline);
        Assert.Equal((AckStatus.Rejected, RejectReason.GameNotFocused), _output.AckFor(chat));
    }

    [Fact]
    public void ChatBlocksOtherGesturesUntilDone()
    {
        Chat("/wave");
        var key = Submit("lights", KeyPhase.Down);

        Assert.Equal((AckStatus.Rejected, RejectReason.Busy), _output.AckFor(key));
    }

    [Fact]
    public void ChatOutsideAllowlistIsInvalid()
    {
        var chat = Chat("/notanemote");

        Assert.Equal((AckStatus.Rejected, RejectReason.Invalid), _output.AckFor(chat));
        Assert.Empty(_sink.Timeline);
    }

    [Fact]
    public void DisposeReleasesActiveInput()
    {
        var hold = Submit("autoLand", KeyPhase.Hold, ms: 1000);
        _executor.Dispose();

        Assert.Equal(["0:nv", "0:n^"], _sink.Timeline);
        Assert.Equal(CancelReason.Shutdown, Assert.Single(_output.Errors).Reason);
        Assert.Equal(hold, _output.Errors[0].Command);
    }

    [Fact]
    public void FailedChatTextNeverSubmitsAnEmptyMessageAndExecutorCanRecover()
    {
        var chat = Chat("/wave");
        _sink.FailText = true;
        _time.Advance(TimeSpan.FromMilliseconds(60));
        _time.Advance(TimeSpan.FromMilliseconds(300));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(chat));
        Assert.Equal(["0:enterv", "60:enter^"], _sink.Timeline);
        var next = Submit("lights", KeyPhase.Down);
        Assert.Equal((AckStatus.Sent, (RejectReason?)null), _output.AckFor(next));
    }

    [Fact]
    public void FailedChatCleanupBlocksNewInputUntilLocalReset()
    {
        Chat("/wave");
        _sink.FailText = true;
        _sink.TextReleaseFailed = true;
        _time.Advance(TimeSpan.FromMilliseconds(60));
        _time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(_executor.IsBlocked);
        Assert.Equal(1, _output.BlockedCount);
        var next = Submit("lights", KeyPhase.Down);
        Assert.Equal((AckStatus.Rejected, RejectReason.InputFailed), _output.AckFor(next));
    }

    private InputExecutor CreateExecutor(InputExecutorOptions options) =>
        new(_time, _sink, _game, _keyboard, new EmoteAllowlist(["wave", "dance"]), _output, options);

    private static KeyCommand Key(Guid id, string action, KeyPhase phase, int? ms = null) =>
        new(id, ActionCatalog.All[action], phase, ms);

    private Guid Submit(string action, KeyPhase phase, int? ms = null, Guid? connection = null)
    {
        var id = Guid.NewGuid();
        _executor.Submit(connection ?? Connection, Key(id, action, phase, ms), _time.GetTimestamp());
        return id;
    }

    private Guid Chat(string text)
    {
        var id = Guid.NewGuid();
        _executor.Submit(Connection, new ChatCommand(id, text), _time.GetTimestamp());
        return id;
    }

    private Guid ReleaseAll(Guid connection)
    {
        var id = Guid.NewGuid();
        _executor.Submit(connection, new ReleaseAllCommand(id), _time.GetTimestamp());
        return id;
    }
}
