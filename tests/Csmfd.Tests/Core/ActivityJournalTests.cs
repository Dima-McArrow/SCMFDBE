using Csmfd.Core.GameLog;
using Csmfd.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Csmfd.Tests.Core;

public sealed class ActivityJournalTests
{
    private static readonly Guid Generation = Guid.Parse("982ba1b6-1aa8-4e7e-a73b-9a9e51abb8d1");
    private const string Mission = "addd0f67-f57d-4173-a212-7a8f46e4b3fd";
    private readonly ActivityJournal _journal = new(new FakeTimeProvider(), Generation);

    internal static string Notification(string body, int notificationId = 1, string mission = "00000000-0000-0000-0000-000000000000", string objective = "") =>
        $"<2026-10-27T12:00:00.000Z> [Notice] <SHUDEvent_OnNotification> Added notification \"{body}\" [{notificationId}] to queue. New queue size: 1, MissionId: [{mission}], ObjectiveId: [{objective}]";

    // Ship channels drive the state message only (one path for Deck and theme). Both the single-line test
    // shape and the real two-line shape of the notification must stay out of the activity feed.
    [Theory]
    [InlineData("You have joined channel 'Drake Cutter Rambler : Test_Pilot'.")]
    [InlineData("You have left the channel 'Drake Cutter Rambler : Test_Pilot'.")]
    public void ShipChannelNotificationsAreNotActivity(string body)
    {
        _journal.OnLine(new LogLine(Notification(body), 10, Generation));
        _journal.OnLine(new LogLine($"<2026-10-27T12:00:05.000Z> [Notice] <SHUDEvent_OnNotification> Added notification \"{body}", 20, Generation));
        _journal.OnLine(new LogLine("<2026-10-27T12:00:05.000Z> : \" [9] to queue. New queue size: 1, MissionId: [00000000-0000-0000-0000-000000000000], ObjectiveId: [] [Team_CoreGameplayFeatures][Missions][Comms]", 30, Generation));

        Assert.Empty(_journal.Read(null, 0)!.Events);
    }

    [Fact]
    public void RewardsHaveAnExplicitAmountAndNoInventedMission()
    {
        _journal.OnLine(new LogLine(Notification("Awarded 1,000 aUEC: "), 10, Generation));
        var page = Assert.IsType<ActivityPage>(_journal.Read(null, 0));
        var item = Assert.Single(page.Events);
        Assert.Equal("reward", item.Kind);
        Assert.Equal(1000, item.Amount);
        Assert.Equal("aUEC", item.Currency);
        Assert.Null(item.MissionId);
    }

    [Fact]
    public void DisplayReprintsRepeatedOffsetsAndRepeatedNotificationIdentityDoNotDoubleCount()
    {
        var text = Notification("Awarded 500 aUEC: ");
        _journal.OnLine(new LogLine(text, 10, Generation));
        _journal.OnLine(new LogLine(text, 10, Generation));
        _journal.OnLine(new LogLine(text.Replace("Added notification", "Notification", StringComparison.Ordinal), 20, Generation));
        _journal.OnLine(new LogLine(text, 30, Generation));
        Assert.Single(_journal.Read(null, 0)!.Events);
    }

    [Fact]
    public void SameRewardWithDifferentNotificationIdsCountsSeparately()
    {
        _journal.OnLine(new LogLine(Notification("Awarded 500 aUEC: ", 1), 10, Generation));
        _journal.OnLine(new LogLine(Notification("Awarded 500 aUEC: ", 2), 20, Generation));
        Assert.Equal(2, _journal.Read(null, 0)!.Events.Count);
    }

    [Fact]
    public void MultilineObjectiveRetainsMissionAndObjectiveIdentity()
    {
        var full = Notification("New Objective: Deliver cargo to\nLorville: ", mission: Mission, objective: "dropoff_1").Split('\n');
        _journal.OnLine(new LogLine(full[0], 10, Generation));
        Assert.Empty(_journal.Read(null, 0)!.Events);
        _journal.OnLine(new LogLine("<2026-10-27T12:00:00.000Z> " + full[1], 20, Generation));
        var item = Assert.Single(_journal.Read(null, 0)!.Events);
        Assert.Equal("newObjective", item.Kind);
        Assert.Equal(Guid.Parse(Mission), item.MissionId);
        Assert.Equal("dropoff_1", item.ObjectiveId);
        Assert.Equal("Deliver cargo to Lorville", item.Text);
    }

    [Theory]
    [InlineData("Transaction Complete: ")]
    [InlineData("Fine paid: 500 UEC")]
    [InlineData("Awarded -500 aUEC: ")]
    [InlineData("Awarded 1000000001 aUEC: ")]
    [InlineData("Contract Accepted: missing mission")]
    public void UnsupportedOrUnconfirmedMoneyAndUnidentifiedContractsAreExcluded(string text)
    {
        _journal.OnLine(new LogLine(Notification(text), 10, Generation));
        Assert.Empty(_journal.Read(null, 0)!.Events);
    }

    [Fact]
    public void ReplayIsBoundedPagedAndExplicitAboutItsEarliestAvailableSequence()
    {
        for (var i = 0; i < 300; i++)
        {
            _journal.OnLine(new LogLine(Notification("Awarded 500 aUEC: ", i), i * 100, Generation));
        }

        var page = _journal.Read(null, 0)!;
        Assert.Equal(45, page.FirstSeq);
        Assert.Equal(300, page.LatestSeq);
        Assert.Equal(4, page.Events.Count);
        Assert.Equal(45, page.Events[0].Seq);
        var next = _journal.Read(page.StreamId, page.Events[^1].Seq)!;
        Assert.Equal(49, next.Events[0].Seq);
        Assert.Null(_journal.Read(page.StreamId, 300));
    }

    [Fact]
    public void ResetStartsANewStreamAndIgnoresStaleGeneration()
    {
        var before = _journal.Read(null, 0)!;
        var generation = Guid.NewGuid();
        _journal.Reset(generation);
        _journal.OnLine(new LogLine(Notification("Awarded 500 aUEC: "), 10, Generation));
        var after = _journal.Read(before.StreamId, 0)!;
        Assert.NotEqual(before.StreamId, after.StreamId);
        Assert.Equal(0, after.LatestSeq);
        Assert.Empty(after.Events);
    }

    [Fact]
    public void EndIsIdempotentAndDoesNotClearCapturedRewards()
    {
        _journal.OnLine(new LogLine(Notification("Awarded 500 aUEC: "), 10, Generation));
        _journal.End();
        _journal.End();
        var page = _journal.Read(null, 0)!;
        Assert.True(page.Ended);
        Assert.Equal(2, page.Events.Count);
        Assert.Equal("reward", page.Events[0].Kind);
        Assert.Equal("sessionEnded", page.Events[1].Kind);
    }
}
