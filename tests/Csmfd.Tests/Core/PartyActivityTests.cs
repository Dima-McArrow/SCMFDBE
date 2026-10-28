using Csmfd.Core.GameLog;
using Csmfd.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Csmfd.Tests.Core;

public sealed class PartyActivityTests
{
    private static readonly Guid Generation = Guid.Parse("982ba1b6-1aa8-4e7e-a73b-9a9e51abb8d1");

    // Anonymized full notification bodies, including the real timestamped continuation shape.
    [Theory]
    [InlineData("You have created\nparty: ", "partyCreated", "")]
    [InlineData("You have joined\nparty: ", "partyJoined", "")]
    [InlineData("New Member Joined\nPilot_One has joined the party.: ", "partyMemberJoined", "Pilot_One")]
    [InlineData("Member Left\nPilot_One has left the party.: ", "partyMemberLeft", "Pilot_One")]
    [InlineData("New Party Leader\nPilot_One is now party leader.: ", "partyLeaderChanged", "Pilot_One")]
    [InlineData("Party Disbanded\nThe party has been disbanded.: ", "partyDisbanded", "")]
    public void MultilineNotificationsProduceOnlyBoundedFacts(string body, string kind, string name)
    {
        var journal = new ActivityJournal(new FakeTimeProvider(), Generation);
        var lines = ActivityJournalTests.Notification(body).Split('\n');
        journal.OnLine(new LogLine(lines[0], 10, Generation));
        Assert.Empty(journal.Read(null, 0)!.Events);
        journal.OnLine(new LogLine("<2026-10-27T12:00:00.000Z> " + lines[1], 20, Generation));
        var item = Assert.Single(journal.Read(null, 0)!.Events);
        Assert.Equal(kind, item.Kind);
        Assert.Equal(name, item.Text);
        Assert.Null(item.MissionId);
        Assert.Null(item.ObjectiveId);
        Assert.Null(item.Amount);
        Assert.Null(item.Currency);
        Assert.NotEmpty(ServerMessageWriter.Activity(Guid.NewGuid(), journal.Read(null, 0)!));
    }

    [Theory]
    [InlineData("Pilot_One Party Invite Received: Accept Invitation?: ")]
    [InlineData("New Member Joined Pilot_One has joined the channel 'Drake Cutter : Pilot_One'.: ")]
    [InlineData("Member Left Pilot_One has left the channel 'Drake Cutter : Pilot_One'.: ")]
    [InlineData("Party Launch Accepted \uFFFD\uFFFD: ")]
    [InlineData("New Member Joined Pilot\u200BOne has joined the party.: ")]
    [InlineData("Member Left Pilot\uFFFDOne has left the party.: ")]
    [InlineData("New Party Leader  Pilot_One is now party leader.: ")]
    public void InvitesChannelsAndDamagedNamesCannotChangeParty(string body)
    {
        var journal = new ActivityJournal(new FakeTimeProvider(), Generation);
        journal.OnLine(new LogLine(ActivityJournalTests.Notification(body), 10, Generation));
        Assert.Empty(journal.Read(null, 0)!.Events);
    }

    [Fact]
    public void NamesAreNeverTruncatedAndNoiseCannotBecomeMembership()
    {
        var journal = new ActivityJournal(new FakeTimeProvider(), Generation);
        journal.OnLine(new LogLine(ActivityJournalTests.Notification("New Member Joined " + new string('a', 97) + " has joined the party.: "), 10, Generation));
        journal.OnLine(new LogLine("<Accept invitation> Client 1 accept invitation test", 20, Generation));
        journal.OnLine(new LogLine("<CPartyMarkerComponent UFES> Streaming out party marker id 2. TrackedEntityId: 1", 30, Generation));
        journal.OnLine(new LogLine("<Leave group> Client 1 leave group test", 40, Generation));
        Assert.Empty(journal.Read(null, 0)!.Events);
    }

    [Fact]
    public void QueueReprintsUpdatesAndDuplicateAdditionsDoNotDuplicateParty()
    {
        var journal = new ActivityJournal(new FakeTimeProvider(), Generation);
        var notification = ActivityJournalTests.Notification("New Member Joined Pilot_One has joined the party.: ");
        journal.OnLine(new LogLine(notification, 10, Generation));
        journal.OnLine(new LogLine(notification.Replace("Added notification", "Notification", StringComparison.Ordinal), 20, Generation));
        journal.OnLine(new LogLine(notification.Replace("<SHUDEvent_OnNotification>", "<UpdateNotificationItem>", StringComparison.Ordinal).Replace("Added notification", "Notification", StringComparison.Ordinal), 30, Generation));
        journal.OnLine(new LogLine(notification, 40, Generation));
        Assert.Single(journal.Read(null, 0)!.Events);
        journal.OnLine(new LogLine(ActivityJournalTests.Notification("New Member Joined Pilot_One has joined the party.: ", 2), 50, Generation));
        Assert.Equal(2, journal.Read(null, 0)!.Events.Count);
        journal.Reset(Guid.NewGuid());
        Assert.Empty(journal.Read(null, 0)!.Events);
    }

    [Fact]
    public void PartyUsesTheSameBoundedJournalAsRewards()
    {
        var journal = new ActivityJournal(new FakeTimeProvider(), Generation);
        for (var i = 0; i < 300; i++)
        {
            journal.OnLine(new LogLine(ActivityJournalTests.Notification("New Party Leader Pilot_One is now party leader.: ", i), i * 100, Generation));
        }
        var page = journal.Read(null, 0)!;
        Assert.Equal(45, page.FirstSeq);
        Assert.Equal(300, page.LatestSeq);
        Assert.Equal(4, page.Events.Count);
    }
}
