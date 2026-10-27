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
    [InlineData("Drake Cutter Rambler", "DRAK_Cutter_Rambler", true)]
    [InlineData("Mirai Guardian MX", "MRAI_Guardian_MX_Collector_Military", true)]
    [InlineData("RSI Constellation Mk5 Centaurus", "RSI_Constellation_Mk5_Centaurus", true)]
    [InlineData("Drake Cutter Rambler", "DRAK_Cutter", false)]
    [InlineData("Drake Cutter Rambler", "RSI_Perseus", false)]
    public void MatchesShipNameToModel(string shipName, string model, bool expected)
    {
        Assert.Equal(expected, GameLogParser.ShipNameMatchesModel(shipName, model));
    }

    private static DateTimeOffset At(string time) =>
        DateTimeOffset.Parse("2026-10-26T" + time + "Z", System.Globalization.CultureInfo.InvariantCulture);
}
