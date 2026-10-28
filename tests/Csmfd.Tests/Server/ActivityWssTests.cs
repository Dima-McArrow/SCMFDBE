using Csmfd.Core.GameLog;
using Csmfd.Tests.Core;

namespace Csmfd.Tests.Server;

public sealed class ActivityWssTests
{
    [Fact]
    public async Task ActivityIsPagedAfterAuthenticationAndReplayedWithStableIdentityOnReconnect()
    {
        await using var server = await WssTestServer.StartAsync(activity: true);
        for (var i = 0; i < 6; i++)
        {
            server.Services.Activity!.OnLine(new LogLine(ActivityJournalTests.Notification("Awarded 500 aUEC: ", i), i * 100, server.Services.Store.Current.Log.Generation));
        }

        var (client, token) = await server.PairAsync();
        string stream;
        using (client)
        {
            var first = await client.ReceiveTypeAsync("activity");
            stream = first["streamId"]!.GetValue<string>();
            Assert.Equal(4, first["events"]!.AsArray().Count);
            Assert.Equal(1, first["events"]![0]!["seq"]!.GetValue<int>());
            var second = await client.ReceiveTypeAsync("activity");
            Assert.Equal(5, second["events"]![0]!["seq"]!.GetValue<int>());
            Assert.Equal(2, second["events"]!.AsArray().Count);
        }

        using var reconnect = await server.ConnectAsync();
        await reconnect.SendAsync($$"""{"v":2,"type":"hello","client":"test","appVersion":"1","deviceName":"test","token":"{{token}}"}""");
        Assert.Equal("welcome", (await reconnect.ReceiveAsync())["type"]!.GetValue<string>());
        Assert.Equal("state", (await reconnect.ReceiveAsync())["type"]!.GetValue<string>());
        var replay = await reconnect.ReceiveTypeAsync("activity");
        Assert.Equal(stream, replay["streamId"]!.GetValue<string>());
        Assert.Equal(1, replay["events"]![0]!["seq"]!.GetValue<int>());
    }

    [Fact]
    public async Task PartyAndRewardsShareAuthenticatedPagingAndStableReplay()
    {
        await using var server = await WssTestServer.StartAsync(activity: true);
        string[] bodies =
        [
            "You have joined party: ",
            "New Member Joined Pilot_One has joined the party.: ",
            "Awarded 500 aUEC: ",
            "New Party Leader Pilot_One is now party leader.: ",
            "Member Left Pilot_One has left the party.: ",
            "Party Disbanded The party has been disbanded.: ",
        ];
        for (var i = 0; i < bodies.Length; i++)
        {
            server.Services.Activity!.OnLine(new LogLine(ActivityJournalTests.Notification(bodies[i], i), i * 100,
                server.Services.Store.Current.Log.Generation));
        }

        var (client, token) = await server.PairAsync();
        string stream;
        using (client)
        {
            var first = await client.ReceiveTypeAsync("activity");
            stream = first["streamId"]!.GetValue<string>();
            Assert.Equal(4, first["events"]!.AsArray().Count);
            Assert.Equal("partyJoined", first["events"]![0]!["kind"]!.GetValue<string>());
            Assert.Equal("Pilot_One", first["events"]![1]!["text"]!.GetValue<string>());
            Assert.Equal(500, first["events"]![2]!["amount"]!.GetValue<int>());
            Assert.Equal("partyLeaderChanged", first["events"]![3]!["kind"]!.GetValue<string>());
            var second = await client.ReceiveTypeAsync("activity");
            Assert.Equal(5, second["events"]![0]!["seq"]!.GetValue<int>());
            Assert.Equal("partyMemberLeft", second["events"]![0]!["kind"]!.GetValue<string>());
            Assert.Equal("partyDisbanded", second["events"]![1]!["kind"]!.GetValue<string>());
        }

        using var reconnect = await server.ConnectAsync();
        await reconnect.SendAsync($$"""{"v":2,"type":"hello","client":"test","appVersion":"1","deviceName":"test","token":"{{token}}"}""");
        Assert.Equal("welcome", (await reconnect.ReceiveAsync())["type"]!.GetValue<string>());
        Assert.Equal("state", (await reconnect.ReceiveAsync())["type"]!.GetValue<string>());
        var replay = await reconnect.ReceiveTypeAsync("activity");
        Assert.Equal(stream, replay["streamId"]!.GetValue<string>());
        Assert.Equal(1, replay["events"]![0]!["seq"]!.GetValue<int>());
        Assert.Equal("partyJoined", replay["events"]![0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnpairedClientCannotReadCapturedActivity()
    {
        await using var server = await WssTestServer.StartAsync(activity: true);
        server.Services.Activity!.OnLine(new LogLine(ActivityJournalTests.Notification("Awarded 500 aUEC: "), 10, server.Services.Store.Current.Log.Generation));
        server.Services.Activity.OnLine(new LogLine(ActivityJournalTests.Notification("New Party Leader Pilot_One is now party leader.: ", 2), 20, server.Services.Store.Current.Log.Generation));
        using var client = await server.ConnectAsync();
        await client.SendAsync("""{"v":2,"type":"hello","client":"test","appVersion":"1","deviceName":"test","token":null}""");
        Assert.Equal("error", (await client.ReceiveAsync())["type"]!.GetValue<string>());
        Assert.Equal(Csmfd.Protocol.CloseCodes.Unauthenticated, await client.ReceiveCloseAsync());
    }
}
