using Csmfd.Core.Input;
using Csmfd.Core.State;
using Csmfd.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Csmfd.Tests.Core;

public sealed class CommandGateTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void RejectsDuplicateIdEvenWithDifferentPayload()
    {
        var gate = new CommandGate(_time);
        var id = Guid.NewGuid();

        Assert.Null(gate.Admit(new KeyCommand(id, ActionCatalog.All["lights"], KeyPhase.Down, null)));
        Assert.Equal(RejectReason.Duplicate, gate.Admit(new KeyCommand(id, ActionCatalog.All["vtol"], KeyPhase.Down, null)));
        Assert.Equal(RejectReason.Duplicate, gate.Admit(new ReleaseAllCommand(id)));
    }

    [Fact]
    public void ForgetsIdsBeyondCapacity()
    {
        var gate = new CommandGate(_time, dedupCapacity: 3);
        var first = Guid.NewGuid();
        gate.Admit(new ReleaseAllCommand(first));
        for (var i = 0; i < 3; i++)
        {
            gate.Admit(new ReleaseAllCommand(Guid.NewGuid()));
        }

        Assert.Null(gate.Admit(new ReleaseAllCommand(first)));
    }

    [Fact]
    public void LimitsNewGesturesButNeverUpOrReleaseAll()
    {
        var gate = new CommandGate(_time);
        for (var i = 0; i < 30; i++)
        {
            Assert.Null(gate.Admit(Down()));
        }

        Assert.Equal(RejectReason.RateLimited, gate.Admit(Down()));
        Assert.Equal(RejectReason.RateLimited, gate.Admit(new ChatCommand(Guid.NewGuid(), "/wave")));
        Assert.Null(gate.Admit(new KeyCommand(Guid.NewGuid(), ActionCatalog.All["lights"], KeyPhase.Up, null)));
        Assert.Null(gate.Admit(new ReleaseAllCommand(Guid.NewGuid())));

        _time.Advance(TimeSpan.FromMilliseconds(100)); // refills 3 tokens
        Assert.Null(gate.Admit(Down()));
        Assert.Null(gate.Admit(Down()));
        Assert.Null(gate.Admit(Down()));
        Assert.Equal(RejectReason.RateLimited, gate.Admit(Down()));
    }

    private static KeyCommand Down() => new(Guid.NewGuid(), ActionCatalog.All["lights"], KeyPhase.Down, null);
}

public sealed class StateStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartsUnknownAndSeqIncreasesPerTake()
    {
        var store = new StateStore();
        var (snapshot, seq) = store.TakeFull();

        Assert.Null(snapshot.Ship);
        Assert.Equal(ShipContextStatus.Unknown, snapshot.ShipContext.Status);
        Assert.Equal(LogStatus.Missing, snapshot.Log.Status);
        Assert.Equal(1, seq);
        Assert.Null(store.TakeDelta());
        Assert.Equal(2, store.TakeFull().Seq);
    }

    [Fact]
    public void MergesChangedTopLevelKeysOnly()
    {
        var store = new StateStore();
        store.TakeFull();

        store.Update(s => s with { Game = new GameState(true, false, null) });
        store.Update(s => s with { Game = new GameState(true, true, null) });
        store.Update(s => s with { QtTarget = QtTargetState.Unknown }); // no-op

        var (delta, seq) = store.TakeDelta()!.Value;
        Assert.Equal(new GameState(true, true, null), delta.Game);
        Assert.Null(delta.QtTarget);
        Assert.Null(delta.Ship);
        Assert.Equal(2, seq);
        Assert.Null(store.TakeDelta());
    }

    [Fact]
    public void ClearingShipIsAnExplicitChange()
    {
        var store = new StateStore();
        store.Update(s => s with { Ship = new ShipState("Cutter", null, null, "DRAK", Confidence.Assumed, Now) });
        store.TakeFull();

        store.Update(s => s with { Ship = null });

        var delta = store.TakeDelta()!.Value.Delta;
        Assert.NotNull(delta.Ship);
        Assert.Null(delta.Ship.Value);
    }

    [Fact]
    public void FullSnapshotDiscardsPendingDelta()
    {
        var store = new StateStore();
        store.Update(s => s with { Game = new GameState(true, true, "12913470") });

        Assert.Equal("12913470", store.TakeFull().Snapshot.Game.Build);
        Assert.Null(store.TakeDelta());
    }

    [Fact]
    public void RaisesChangedOnlyForRealChangesWithUrgency()
    {
        var store = new StateStore();
        var events = new List<bool>();
        store.Changed += events.Add;

        store.Update(s => s);
        store.Update(s => s with { Game = new GameState(true, true, null) });
        store.Update(s => s with { HangarRequest = new HangarRequestState(HangarRequestStatus.Pending, Guid.NewGuid(), null, Now, Now) }, urgent: true);

        Assert.Equal([false, true], events);
    }
}

public sealed class EmoteAllowlistTests
{
    private readonly EmoteAllowlist _list = EmoteAllowlist.Parse("# emotes\nwave\ndance\n\ngasp\nasl_thanks\n");

    [Theory]
    [InlineData("/wave", true)]
    [InlineData("/dance3", true)]
    [InlineData("/gasp5", true)]
    [InlineData("/dance12", true)]
    [InlineData("/dance123", false)]
    [InlineData("/dance0", false)]
    [InlineData("/dance03", false)]
    [InlineData("/Wave", false)]
    [InlineData("wave", false)]
    [InlineData("/", false)]
    [InlineData("/3", false)]
    [InlineData("/salute", false)]
    [InlineData("/wave ", false)]
    [InlineData("/asl_thanks", true)]
    [InlineData("/asl_thanks2", true)]
    [InlineData("/asl_", false)]
    [InlineData("/asl", false)]
    public void ChecksBaseNameAndVariant(string text, bool allowed)
    {
        Assert.Equal(allowed, _list.IsAllowed(text));
    }

    [Fact]
    public void IgnoresCommentsAndInvalidNames()
    {
        Assert.Equal(4, _list.Count);
        Assert.Empty(_list.Skipped);

        var list = new EmoteAllowlist(["/wave", "asl_hello", "Bad", "two words", "", "dance2", "_x"]);
        Assert.Equal(2, list.Count);
        Assert.Equal(["Bad", "two words", "dance2", "_x"], list.Skipped);
    }
}
