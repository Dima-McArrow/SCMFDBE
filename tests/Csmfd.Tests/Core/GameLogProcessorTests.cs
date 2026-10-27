using Csmfd.Core.GameLog;
using Csmfd.Core.Input;
using Csmfd.Core.State;
using Csmfd.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Csmfd.Tests.Core;

/// <summary>Ship lifecycle, QT and ATC rows of spec 41.18, driven by real (anonymized) log lines.</summary>
public sealed class GameLogProcessorTests : IDisposable
{
    private const string Spawn = "<2026-10-26T15:55:58.056Z> [Notice] <CEntityComponentShipListProvider::SetVehicleSpawningInformations> SetVehicleSpawningInformations - VehicleEntityId: [903627021939], LandingArea: Test_Pilot's";
    private const string Join = "<2026-10-26T15:56:32.460Z> [Notice] <SHUDEvent_OnNotification> Added notification \"You have joined channel 'Drake Cutter Rambler : Test_Pilot'.";
    private const string NoRoute = "<2026-10-26T15:56:52.708Z> [Notice] <Failed to get starmap route data!> [ItemNavigation][CL][24180] | NOT AUTH | DRAK_Cutter_Rambler_903627021939[903627021939]|CSCItemNavigation::GetStarmapRouteSegmentData|No Route loaded! [Team_CGP4][QuantumTravel]";
    private const string Hangar = "<2026-10-26T15:56:53.896Z> [Notice] <SHUDEvent_OnNotification> Added notification \"Hangar Request Completed: \" [10] to queue.";
    private const string Target = "<2026-10-26T15:57:10.000Z> [Notice] <Player Selected Quantum Target - Local> [ItemNavigation][CL][4976] | NOT AUTH | DRAK_Cutter_Rambler_903627021939[903627021939]|CSCItemNavigation::OnPlayerSelectedQuantumTarget|Player has selected point Stanton4a as their destination, routing locally [Team_CGP4][QuantumTravel]";
    private const string OtherTarget = "<2026-10-26T15:57:20.000Z> [Notice] <Player Selected Quantum Target - Local> [ItemNavigation][CL][4976] | NOT AUTH | MRAI_Guardian_MX_Collector_Military_902412078691[902412078691]|CSCItemNavigation::OnPlayerSelectedQuantumTarget|Player has selected point rs_ext_pyro6_leo as their destination, routing locally [Team_CGP4][QuantumTravel]";
    private const string OtherClear = "<2026-10-26T15:58:00.000Z> [Notice] <Vehicle Control Flow> CVehicleMovementBase::ClearDriver: Local client node [100000000001] releasing control token for 'RSI_Perseus_901556573221' [901556573221] [Team_CGP4][Vehicle]";
    private const string Clear = "<2026-10-26T15:58:41.704Z> [Notice] <Vehicle Control Flow> CVehicleMovementBase::ClearDriver: Local client node [100000000001] releasing control token for 'DRAK_Cutter_Rambler_903627021939' [903627021939] [Team_CGP4][Vehicle]";
    private const string Quit = "<2026-10-26T15:59:20.289Z> [Notice] <SystemQuit> CSystem::Quit invoked with - cause=30016, reason=Quit via console command, exitCode=0 [Team_Unknown][System]";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 15, 56, 0, TimeSpan.Zero));
    private readonly StateStore _store = new();
    private readonly FakeInputSink _sink;
    private readonly InputExecutor _executor;
    private readonly GameLogProcessor _processor;
    private readonly Guid _generation = Guid.NewGuid();
    private readonly RecordingLogger _log = new();
    private long _watermark;
    private long _offset;

    public GameLogProcessorTests()
    {
        _sink = new FakeInputSink(_time);
        _executor = new InputExecutor(_time, _sink, new FakeGameWindow(), new FakeKeyboard(), new EmoteAllowlist([]), new RecordingOutput());
        _processor = new GameLogProcessor(_store, _executor, _time, _generation, () => _watermark, _log);
    }

    private StateSnapshot State => _store.Current;

    public void Dispose()
    {
        _processor.Dispose();
        _executor.Dispose();
    }

    [Fact]
    public void ChannelJoinStartsAnAssumedShipContext()
    {
        Feed(Join);

        var ship = Assert.IsType<ShipState>(State.Ship);
        Assert.Equal("Drake Cutter Rambler", ship.Name);
        Assert.Equal("DRAK", ship.ManufacturerPrefix);
        Assert.Equal(Confidence.Assumed, ship.Confidence);
        Assert.Null(ship.EntityId);
        Assert.Equal(1, State.ShipContext.Revision);
        Assert.Equal(ShipContextStatus.Assumed, State.ShipContext.Status);
        Assert.Equal(ShipContextReason.ChannelJoined, State.ShipContext.Reason);
        Assert.NotNull(State.ShipContext.ContextId);
    }

    [Fact]
    public void SpawnAndNavigationLineBindTheEntityWithoutANewRevision()
    {
        Feed(Spawn, Join, NoRoute);

        Assert.Equal("903627021939", State.Ship!.EntityId);
        Assert.Equal("DRAK_Cutter_Rambler", State.Ship.Model);
        Assert.Equal(1, State.ShipContext.Revision);
    }

    [Fact]
    public void QuantumTargetOfOurShipIsConfirmedAndOthersAreIgnored()
    {
        Feed(Spawn, Join, NoRoute, OtherTarget);
        Assert.Equal(QtMode.Unknown, State.QtTarget.Mode);

        Feed(Target);
        Assert.Equal(new QtTargetState(QtMode.Destination, "Stanton4a", null, Confidence.Confirmed, State.QtTarget.ObservedAt), State.QtTarget);
    }

    [Fact]
    public void ClearDriverOfOurShipEndsContextKeepsThemeAndReleasesInput()
    {
        Feed(Spawn, Join, NoRoute, Target);
        var contextId = State.ShipContext.ContextId;
        _executor.Submit(Guid.NewGuid(), new KeyCommand(Guid.NewGuid(), ActionCatalog.All["lights"], KeyPhase.Down, null), _time.GetTimestamp());

        Feed(OtherClear);
        Assert.NotNull(State.Ship);

        Feed(Clear);
        Assert.Null(State.Ship);
        Assert.Equal(2, State.ShipContext.Revision);
        Assert.Equal(ShipContextStatus.NoShip, State.ShipContext.Status);
        Assert.Equal(ShipContextReason.ClearDriver, State.ShipContext.Reason);
        Assert.Equal(contextId, State.ShipContext.ContextId);
        Assert.Equal("DRAK", State.ShipContext.ManufacturerPrefix);
        Assert.Equal(QtTargetState.Unknown, State.QtTarget);
        Assert.Equal(["0:lv", "0:l^"], _sink.Timeline);
    }

    [Fact]
    public void ClearDriverWithoutBindingFallsBackToModelMatch()
    {
        Feed(Join, Clear);

        Assert.Null(State.Ship);
        Assert.Equal(ShipContextReason.ClearDriver, State.ShipContext.Reason);
    }

    [Fact]
    public void RepeatedClearDriverAfterManualOverrideProducesNewRevision()
    {
        Feed(Spawn, Join, NoRoute, Clear);
        Assert.Equal(2, State.ShipContext.Revision);

        Feed(Clear);

        Assert.Equal(3, State.ShipContext.Revision);
        Assert.Equal(ShipContextStatus.NoShip, State.ShipContext.Status);
    }

    [Fact]
    public void RejoiningTheSameChannelIsANewContext()
    {
        Feed(Join);
        var first = State.ShipContext.ContextId;

        Feed(Join);

        Assert.Equal(2, State.ShipContext.Revision);
        Assert.NotEqual(first, State.ShipContext.ContextId);
    }

    [Fact]
    public void HangarAnswerMustComeAfterTheWatermarkInTheSameContext()
    {
        Feed(Join);
        _offset = 5_000;
        _watermark = 5_000;
        var command = Guid.NewGuid();
        _processor.OnInputStarted(command, ActionCatalog.All["requestLanding"]);
        Assert.Equal(HangarRequestStatus.Pending, State.HangarRequest.Status);
        Assert.Equal(command, State.HangarRequest.CommandId);

        // Written before the request (still unparsed when it was sent): must not answer it.
        FeedAt(4_900, Hangar);
        Assert.Equal(HangarRequestStatus.Pending, State.HangarRequest.Status);

        FeedAt(5_100, Hangar);
        Assert.Equal(HangarRequestStatus.Answered, State.HangarRequest.Status);
        Assert.Equal(command, State.HangarRequest.CommandId);
    }

    [Fact]
    public void HangarRequestTimesOutAfter120Seconds()
    {
        Feed(Join);
        _processor.OnInputStarted(Guid.NewGuid(), ActionCatalog.All["requestLanding"]);

        _time.Advance(GameLogProcessor.HangarTimeout);

        Assert.Equal(HangarRequestStatus.Timeout, State.HangarRequest.Status);
    }

    [Fact]
    public void OtherActionsAndNotificationsWithoutRequestDoNothing()
    {
        Feed(Join);
        _processor.OnInputStarted(Guid.NewGuid(), ActionCatalog.All["landingGear"]);
        Feed(Hangar);

        Assert.Equal(HangarRequestState.Idle, State.HangarRequest);
    }

    [Fact]
    public void ContextChangeResetsAtc()
    {
        Feed(Spawn, Join, NoRoute);
        _processor.OnInputStarted(Guid.NewGuid(), ActionCatalog.All["requestLanding"]);

        Feed(Clear);

        Assert.Equal(HangarRequestState.Idle, State.HangarRequest);
        _time.Advance(GameLogProcessor.HangarTimeout);
        Assert.Equal(HangarRequestState.Idle, State.HangarRequest);
    }

    [Fact]
    public void QuitAndProcessExitAreOneGameEndedTransition()
    {
        Feed(Join, Quit);
        Assert.Equal(2, State.ShipContext.Revision);
        Assert.Equal(ShipContextReason.GameEnded, State.ShipContext.Reason);

        _processor.OnGameProcessExited();

        Assert.Equal(2, State.ShipContext.Revision);
    }

    [Fact]
    public void GenerationResetClearsEverythingAndIgnoresStaleLines()
    {
        Feed(Spawn, Join, NoRoute);
        var next = Guid.NewGuid();

        _processor.OnGenerationReset(next);

        Assert.Null(State.Ship);
        Assert.Equal(ShipContextStatus.Unknown, State.ShipContext.Status);
        Assert.Equal(ShipContextReason.LogReset, State.ShipContext.Reason);
        Assert.Null(State.ShipContext.ContextId);

        Feed(Join); // old generation
        Assert.Null(State.Ship);

        _processor.OnLine(new LogLine(Join, 0, next));
        Assert.NotNull(State.Ship);
    }

    [Fact]
    public void ReplayingTheWholeFixtureEndsWithNoShip()
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GameLog", "ship-session.log"));

        Feed(lines);

        Assert.Null(State.Ship);
        Assert.Equal(ShipContextReason.GameEnded, State.ShipContext.Reason);
        Assert.Equal(3, State.ShipContext.Revision); // join, ClearDriver, game ended
    }

    [Fact]
    public void DebugLogExplainsDecisionsWithoutThePlayerHandle()
    {
        Feed(Spawn, Join, NoRoute, Target, OtherTarget, OtherClear, Hangar, Clear, Quit);

        var messages = _log.Entries.Select(e => e.Message).ToList();
        Assert.All(_log.Entries, e => Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, e.Level));

        // The channel owner is the player's handle: personal data, never logged (spec 41.16).
        Assert.DoesNotContain(messages, m => m.Contains("Test_Pilot", StringComparison.Ordinal));

        Assert.Contains("Ship context revision 1: Assumed / ChannelJoined, ship Drake Cutter Rambler, prefix DRAK", messages);
        Assert.Contains(messages, m => m.StartsWith("Ship bound to model", StringComparison.Ordinal) && m.EndsWith("entity 903627021939", StringComparison.Ordinal));
        Assert.Contains("QT destination Stanton4a accepted for entity 903627021939", messages);
        Assert.Contains("QT target of entity 902412078691 ignored (our ship entity: 903627021939)", messages);
        Assert.Contains("ClearDriver for entity 901556573221 ignored: another vehicle", messages);
        Assert.Contains(messages, m => m.StartsWith("Hangar notification at offset", StringComparison.Ordinal) && m.EndsWith("no pending request from the iPad", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Ship context revision 2: NoShip / ClearDriver", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Ship context revision 3: NoShip / GameEnded", StringComparison.Ordinal));
    }

    private void Feed(params string[] lines)
    {
        foreach (var line in lines)
        {
            FeedAt(_offset, line);
        }
    }

    private void FeedAt(long offset, string line)
    {
        _processor.OnLine(new LogLine(line, offset, _generation));
        _offset = offset + line.Length + 2;
    }
}
