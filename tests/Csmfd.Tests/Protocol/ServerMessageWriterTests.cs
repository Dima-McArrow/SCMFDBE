using System.Text.Json.Nodes;
using Csmfd.Protocol;

namespace Csmfd.Tests.Protocol;

public sealed class ServerMessageWriterTests
{
    private static readonly Guid SessionId = Guid.Parse("7a42291e-21db-4ac5-a3e1-ea278bb54900");
    private static readonly Guid ServerId = Guid.Parse("366b189f-9bb1-4aeb-bf8b-235a5cdcf9e1");
    private static readonly Guid CommandId = Guid.Parse("4d88b2ad-d306-443a-8524-155575227708");
    private static readonly Guid ContextId = Guid.Parse("e21848e3-803a-4257-8706-53df116bf9ce");
    private static readonly DateTimeOffset Midnight = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WritesAckFixtures()
    {
        Fixture.AssertJsonEqual(
            Fixture.Read("server-ack-sent.json"),
            ServerMessageWriter.Ack(CommandId, AckStatus.Sent));

        Fixture.AssertJsonEqual(
            Fixture.Read("server-ack-rejected-game-not-focused.json"),
            ServerMessageWriter.Ack(Guid.Parse("9b38e72e-4b86-455c-95c6-e3c2d782f172"), AckStatus.Rejected, RejectReason.GameNotFocused));
    }

    [Fact]
    public void WritesHandshakeFixtures()
    {
        Fixture.AssertJsonEqual(Fixture.Read("server-pair-required.json"), ServerMessageWriter.PairRequired());

        Fixture.AssertJsonEqual(
            Fixture.Read("server-paired.json"),
            ServerMessageWriter.Paired(ServerId, new string('A', TextRules.TokenLength)));

        var welcome = new WelcomeInfo(
            "CSMFD-Server",
            "0.1.0",
            ServerId,
            SessionId,
            Guid.Parse("a42a3cd9-a193-457b-b2dd-a973e3d9b35a"),
            ChatKey.Enter);
        Fixture.AssertJsonEqual(Fixture.Read("server-welcome.json"), ServerMessageWriter.Welcome(welcome));
    }

    [Fact]
    public void WritesErrorAndPongFixtures()
    {
        Fixture.AssertJsonEqual(
            Fixture.Read("server-error-input-cancelled.json"),
            ServerMessageWriter.Error(ErrorCode.InputCancelled, "Input cancelled", CommandId, CancelReason.FocusLost));

        Fixture.AssertJsonEqual(Fixture.Read("server-pong.json"), ServerMessageWriter.Pong("p-123", 1_793_040_000_000));
    }

    [Fact]
    public void WritesFullStateFixture()
    {
        var snapshot = new StateSnapshot(
            new GameState(Running: true, Focused: true, Build: null),
            new LogState(LogStatus.Ready, Guid.Parse("982ba1b6-1aa8-4e7e-a73b-9a9e51abb8d1"), Midnight),
            new ShipState("Drake Cutter Rambler", "DRAK_Cutter_Rambler", "903627021939", "DRAK", Confidence.Assumed, Midnight),
            new ShipContextState(3, ShipContextStatus.Assumed, ShipContextReason.ChannelJoined, ContextId, "DRAK", Midnight),
            QtTargetState.Unknown,
            HangarRequestState.Idle);

        Fixture.AssertJsonEqual(Fixture.Read("server-state-full.json"), ServerMessageWriter.FullState(SessionId, 12, snapshot));
    }

    [Fact]
    public void WritesClearDriverDeltaFixture()
    {
        var delta = new StateDelta
        {
            Ship = new ShipUpdate(null),
            ShipContext = new ShipContextState(
                4, ShipContextStatus.NoShip, ShipContextReason.ClearDriver, ContextId, "DRAK", Midnight.AddMinutes(1)),
            QtTarget = QtTargetState.Unknown,
            HangarRequest = HangarRequestState.Idle,
        };

        Fixture.AssertJsonEqual(
            Fixture.Read("server-state-delta-clear-driver.json"),
            ServerMessageWriter.DeltaState(SessionId, 13, delta));
    }

    [Fact]
    public void DeltaOmitsUnchangedKeysAndKeepsExplicitNull()
    {
        var onlyQt = JsonNode.Parse(ServerMessageWriter.DeltaState(SessionId, 1, new StateDelta { QtTarget = QtTargetState.Unknown }))!.AsObject();
        Assert.False(onlyQt.ContainsKey("ship"));
        Assert.False(onlyQt.ContainsKey("game"));
        Assert.True(onlyQt.ContainsKey("qtTarget"));

        var cleared = JsonNode.Parse(ServerMessageWriter.DeltaState(SessionId, 2, new StateDelta { Ship = new ShipUpdate(null) }))!.AsObject();
        Assert.True(cleared.ContainsKey("ship"));
        Assert.Null(cleared["ship"]);
    }

    [Fact]
    public void WritesTimestampsAsUtcWithMilliseconds()
    {
        var local = new DateTimeOffset(2026, 10, 26, 16, 56, 52, 708, TimeSpan.FromHours(1));
        var delta = new StateDelta { Log = new LogState(LogStatus.Ready, Guid.Empty, local) };

        var log = JsonNode.Parse(ServerMessageWriter.DeltaState(SessionId, 1, delta))!["log"]!;

        Assert.Equal("2026-10-26T15:56:52.708Z", log["observedAt"]!.GetValue<string>());
    }

    [Fact]
    public void RejectsAckReasonMismatch()
    {
        Assert.Throws<ArgumentException>(() => ServerMessageWriter.Ack(CommandId, AckStatus.Rejected));
        Assert.Throws<ArgumentException>(() => ServerMessageWriter.Ack(CommandId, AckStatus.Sent, RejectReason.Busy));
    }

    [Fact]
    public void RejectsErrorCommandFieldMismatch()
    {
        Assert.Throws<ArgumentException>(() => ServerMessageWriter.Error(ErrorCode.InputCancelled, "x"));
        Assert.Throws<ArgumentException>(() => ServerMessageWriter.Error(ErrorCode.AutoReleased, "x", CommandId));
        Assert.Throws<ArgumentException>(() => ServerMessageWriter.Error(ErrorCode.Malformed, "x", CommandId, CancelReason.Watchdog));
        Assert.Throws<ArgumentException>(() => ServerMessageWriter.Error(ErrorCode.Malformed, new string('x', ProtocolLimits.MaxErrorMessageScalars + 1)));
    }

    [Fact]
    public void RejectsInvalidQtTargets()
    {
        var unknownWithName = new QtTargetState(QtMode.Unknown, "Stanton_4a", null, Confidence.Stale, null);
        var unknownConfirmed = new QtTargetState(QtMode.Unknown, null, null, Confidence.Confirmed, null);
        var destinationWithoutName = new QtTargetState(QtMode.Destination, null, null, Confidence.Confirmed, Midnight);

        Assert.Throws<ArgumentException>(() => WriteQt(unknownWithName));
        Assert.Throws<ArgumentException>(() => WriteQt(unknownConfirmed));
        Assert.Throws<ArgumentException>(() => WriteQt(destinationWithoutName));
        _ = WriteQt(new QtTargetState(QtMode.Destination, "Stanton_4a", "Calliope", Confidence.Confirmed, Midnight));
    }

    [Fact]
    public void RejectsEmptyStringsInsteadOfNull()
    {
        var ship = new ShipState("Cutter", Model: string.Empty, null, null, Confidence.Assumed, Midnight);

        Assert.Throws<ArgumentException>(() => ServerMessageWriter.DeltaState(SessionId, 1, new StateDelta { Ship = new ShipUpdate(ship) }));
        Assert.Throws<ArgumentException>(() => ServerMessageWriter.DeltaState(SessionId, 1, new StateDelta { Game = new GameState(true, true, string.Empty) }));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(ProtocolLimits.MaxSafeInteger + 1)]
    public void RejectsCountersOutsideSafeRange(long counter)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WriteQt(QtTargetState.Unknown, counter));

        var context = new ShipContextState(counter, ShipContextStatus.Unknown, ShipContextReason.Startup, null, null, null);
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerMessageWriter.DeltaState(SessionId, 1, new StateDelta { ShipContext = context }));
    }

    [Fact]
    public void RejectsEmptyDelta()
    {
        Assert.Throws<ArgumentException>(() => ServerMessageWriter.DeltaState(SessionId, 1, new StateDelta()));
    }

    private static byte[] WriteQt(QtTargetState target, long seq = 1) =>
        ServerMessageWriter.DeltaState(SessionId, seq, new StateDelta { QtTarget = target });
}
