using Csmfd.Core.GameLog;

namespace Csmfd.Tests.Core;

public sealed class GameLogParserTests
{
    private static readonly string[] SessionLines = File.ReadAllLines(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "GameLog", "ship-session.log"));

    [Fact]
    public void ExtractsOnlyTheVerifiedEventsFromARealSession()
    {
        var events = SessionLines.Select(GameLogParser.Parse).OfType<LogEvent>().ToList();

        Assert.Collection(
            events,
            e => Assert.Equal(new VehicleSpawning(At("15:55:58.056"), "903627021939", "Test_Pilot"), e),
            e => Assert.Equal(new ShipChannelJoined(At("15:56:32.460"), "Drake Cutter Rambler", "Test_Pilot", "DRAK"), e),
            e => Assert.Equal(new VehicleObserved(At("15:56:52.708"), "DRAK_Cutter_Rambler", "903627021939"), e),
            e => Assert.Equal(new HangarRequestCompleted(At("15:56:53.896")), e),
            e => Assert.Equal(new QuantumTargetSelected(At("15:57:10.000"), "DRAK_Cutter_Rambler", "903627021939", "Stanton4a"), e),
            e => Assert.Equal(new QuantumTargetSelected(At("15:57:20.000"), "MRAI_Guardian_MX_Collector_Military", "902412078691", "rs_ext_pyro6_leo"), e),
            e => Assert.Equal(new LocalClearDriver(At("15:58:00.000"), "RSI_Perseus", "901556573221"), e),
            e => Assert.Equal(new LocalClearDriver(At("15:58:41.704"), "DRAK_Cutter_Rambler", "903627021939"), e),
            e => Assert.Equal(new GameSessionEnded(At("15:59:07.165")), e),
            e => Assert.Equal(new GameSessionEnded(At("15:59:20.289")), e));
    }

    [Theory]
    [InlineData("""<2026-10-26T15:56:32.460Z>    "You have joined channel 'Drake Cutter Rambler : Test_Pilot'.""")]
    [InlineData("""<2026-10-12T18:33:46.921Z>    "You have left the channel 'RSI Aurora Mk I LN : Test_Pilot'.""")]
    [InlineData("""<2026-10-12T18:33:46.925Z> [Notice] <UpdateNotificationItem> Notification "You have left the channel 'RSI Aurora Mk I LN : Test_Pilot'.""")]
    [InlineData("""<2026-10-26T15:56:53.896Z>    "Hangar Request Completed: " [10]""")]
    [InlineData("""<2026-10-26T15:56:32.460Z> [Notice] <SHUDEvent_OnNotification> Added notification "You have joined channel 'General : Stanton'.""")]
    [InlineData("""<2026-10-26T15:56:32.460Z> [Notice] <SHUDEvent_OnNotification> Added notification "You have joined channel 'Drakeville Social : Someone'.""")]
    [InlineData("""<2026-10-26T15:49:29.707Z> [Notice] <Channel Disconnected> cause=30010 reason="Nub destroyed" isRemote=0 gamerules="SC_Frontend" [Team_Network]""")]
    [InlineData("""garbage without timestamp""")]
    [InlineData("")]
    public void IgnoresEchoesOtherChannelsAndNoise(string line)
    {
        Assert.Null(GameLogParser.Parse(line));
    }

    [Theory]
    [InlineData("Drake Cutter Rambler", "DRAK")]
    [InlineData("RSI Perseus", "RSI")]
    [InlineData("Consolidated Outland Pioneer", "CNOU")]
    [InlineData("MISC Prospector", "MISC")]
    [InlineData("Mirai Guardian MX", "MRAI")]
    [InlineData("Drakeville", null)]
    [InlineData("General", null)]
    public void MapsChannelBrandToManufacturerPrefix(string shipName, string? prefix)
    {
        Assert.Equal(prefix, GameLogParser.PrefixForShipName(shipName));
    }

    [Theory]
    [InlineData("RSI Aurora Mk I LN", "RSI_Aurora_GS_LN", true)]
    [InlineData("RSI Aurora Mk I LN", "RSI_Aurora_ES", false)]
    [InlineData("Anvil C8R Pisces Rescue", "ANVL_C8R_Pisces", true)]
    [InlineData("Anvil C8R Pisces Rescue", "ANVL_C8_Pisces", false)]
    [InlineData("Drake Cutter Rambler", "DRAK_Cutter_Rambler", true)]
    [InlineData("Mirai Guardian MX", "MRAI_Guardian_MX_Collector_Military", true)]
    [InlineData("RSI Constellation Mk5 Centaurus", "RSI_Constellation_Mk5_Centaurus", true)]
    [InlineData("Drake Cutter Rambler", "DRAK_Cutter", false)]
    [InlineData("Drake Cutter Rambler", "RSI_Perseus", false)]
    public void MatchesShipNameToModel(string shipName, string model, bool expected)
    {
        Assert.Equal(expected, GameLogParser.ShipNameMatchesModel(shipName, model));
    }

    [Fact]
    public void ShipChannelNameCanContainAnApostrophe()
    {
        var parsed = GameLogParser.Parse("<2026-10-27T23:14:15.499Z> [Notice] <SHUDEvent_OnNotification> Added notification \"You have joined channel 'Drake Vulture Teach's Special : Test_Pilot'.");
        var joined = Assert.IsType<ShipChannelJoined>(parsed);
        Assert.Equal("Drake Vulture Teach's Special", joined.ShipName);
    }

    [Fact]
    public void ParsesTheVerifiedShipChannelLeaveLine()
    {
        // Real line shape (2026-10-12 log), owner anonymised.
        var parsed = GameLogParser.Parse("<2026-10-12T18:33:46.921Z> [Notice] <SHUDEvent_OnNotification> Added notification \"You have left the channel 'RSI Aurora Mk I LN : Test_Pilot'.");

        var left = Assert.IsType<ShipChannelLeft>(parsed);
        Assert.Equal(new ShipChannelLeft(DateTimeOffset.Parse("2026-10-12T18:33:46.921Z", System.Globalization.CultureInfo.InvariantCulture), "RSI Aurora Mk I LN", "Test_Pilot", "RSI"), left);
    }

    [Fact]
    public void ParsesTheVerifiedVehicleDestroyedLine()
    {
        // Real line (2026-10-11 log), player handle anonymised.
        var parsed = GameLogParser.Parse("<2026-10-11T21:50:53.383Z> [Notice] <[ActorState] Dead> [ACTOR STATE][CSCActorControlStateDead::PrePhysicsUpdate] Actor 'Test_Pilot' [210417741776] ejected from zone 'RSI_Aurora_GS_LN_872969214881' [872969214881] to zone 'social_001_keeger_segment_rckcrk_095' [862501742384] due to previous zone being in a destroyed vehicle with detached interior. [Team_ActorTech][Actor]");

        var destroyed = Assert.IsType<VehicleDestroyed>(parsed);
        Assert.Equal("RSI_Aurora_GS_LN", destroyed.Model);
        Assert.Equal("872969214881", destroyed.EntityId);
    }

    private static DateTimeOffset At(string time) =>
        DateTimeOffset.Parse("2026-10-26T" + time + "Z", System.Globalization.CultureInfo.InvariantCulture);
}
