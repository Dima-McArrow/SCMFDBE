using System.Text.Json;
using Csmfd.Protocol;

namespace Csmfd.Tests.Protocol;

public sealed class ActivityWriterTests
{
    [Theory]
    [InlineData("shipChannelJoined")]
    [InlineData("shipChannelLeft")]
    public void ShipChannelKindsAreNotActivity(string kind)
    {
        var at = DateTimeOffset.UnixEpoch;
        var item = new ActivityEvent(1, kind, at, "Drake Cutter", null, null, null, null);
        Assert.Throws<ArgumentException>(() => ServerMessageWriter.Activity(Guid.NewGuid(), new ActivityPage(Guid.NewGuid(), at, 1, 1, false, [item])));
    }

    [Fact]
    public void WritesSharedPartyFixtureAlongsideRewards()
    {
        var at = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        ActivityEvent[] events =
        [
            new(1, "partyJoined", at.AddSeconds(1), "", null, null, null, null),
            new(2, "partyMemberJoined", at.AddSeconds(2), "Pilot_One", null, null, null, null),
            new(3, "partyLeaderChanged", at.AddSeconds(3), "Pilot_One", null, null, null, null),
            new(4, "reward", at.AddSeconds(4), "Awarded 500 aUEC:", null, null, 500, "aUEC"),
        ];
        var page = new ActivityPage(Guid.Parse("982ba1b6-1aa8-4e7e-a73b-9a9e51abb8d1"), at, 1, 4, false, events);
        Fixture.AssertJsonEqual(Fixture.Read("server-activity-party.json"),
            ServerMessageWriter.Activity(Guid.Parse("7a42291e-21db-4ac5-a3e1-ea278bb54900"), page));
    }

    [Theory]
    [InlineData("partyJoined", "invented name")]
    [InlineData("partyCreated", "invented name")]
    [InlineData("partyDisbanded", "invented name")]
    [InlineData("partyMemberJoined", "")]
    [InlineData("partyMemberLeft", "bad\uFFFDname")]
    [InlineData("partyLeaderChanged", "bad\u200Bname")]
    public void MalformedPartyFactsCannotBeWritten(string kind, string name)
    {
        var at = DateTimeOffset.UnixEpoch;
        var item = new ActivityEvent(1, kind, at, name, null, null, null, null);
        Assert.Throws<ArgumentException>(() => ServerMessageWriter.Activity(Guid.NewGuid(), new ActivityPage(Guid.NewGuid(), at, 1, 1, false, [item])));
    }

    [Fact]
    public void PartyCannotCarryContractOrMoneyFields()
    {
        var at = DateTimeOffset.UnixEpoch;
        var item = new ActivityEvent(1, "partyJoined", at, "", null, null, null, null);
        ActivityEvent[] invalid =
        [
            item with { MissionId = Guid.NewGuid() }, item with { ObjectiveId = "objective" },
            item with { Amount = 1, Currency = "aUEC" },
        ];
        foreach (var bad in invalid)
        {
            Assert.Throws<ArgumentException>(() => ServerMessageWriter.Activity(Guid.NewGuid(), new ActivityPage(Guid.NewGuid(), at, 1, 1, false, [bad])));
        }
    }

    [Fact]
    public void WritesSharedActivityFixture()
    {
        var at = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var item = new ActivityEvent(1, "reward", at.AddSeconds(1), "Awarded 500 aUEC:", null, null, 500, "aUEC");
        var page = new ActivityPage(Guid.Parse("982ba1b6-1aa8-4e7e-a73b-9a9e51abb8d1"), at, 1, 1, false, [item]);
        Fixture.AssertJsonEqual(Fixture.Read("server-activity-rewards.json"),
            ServerMessageWriter.Activity(Guid.Parse("7a42291e-21db-4ac5-a3e1-ea278bb54900"), page));
    }

    [Fact]
    public void WorstCaseUnicodePageFitsTheExistingFrameLimit()
    {
        var at = DateTimeOffset.Parse("2026-10-27T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var text = string.Concat(Enumerable.Repeat("\U0001f680", ActivityLimits.TextScalars));
        var objective = string.Concat(Enumerable.Repeat("\U0001f680", ActivityLimits.ObjectiveScalars));
        var events = Enumerable.Range(1, ActivityLimits.PageSize).Select(i => new ActivityEvent(i, "newObjective", at, text, Guid.NewGuid(), objective, null, null)).ToArray();
        var data = ServerMessageWriter.Activity(Guid.NewGuid(), new ActivityPage(Guid.NewGuid(), at, 1, 4, false, events));
        Assert.True(data.Length <= ProtocolLimits.MaxMessageBytes);
        using var json = JsonDocument.Parse(data);
        Assert.Equal("activity", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(text, json.RootElement.GetProperty("events")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void InvalidRewardCannotBePublishedAsIncome()
    {
        var at = DateTimeOffset.UnixEpoch;
        var item = new ActivityEvent(1, "reward", at, "reward", null, null, -500, "aUEC");
        Assert.Throws<ArgumentException>(() => ServerMessageWriter.Activity(Guid.NewGuid(), new ActivityPage(Guid.NewGuid(), at, 1, 1, false, [item])));
    }
}
