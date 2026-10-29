using Csmfd.Core.Input;
using Csmfd.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Csmfd.Tests.Core;

public sealed class WheelCommandGateTests
{
    [Fact]
    public void WheelSharesTheKeyAndChatTokenBudgetAndDoesNotLimitReleases()
    {
        var time = new FakeTimeProvider();
        var gate = new CommandGate(time, ratePerSecond: 30, burst: 3);
        Assert.Null(gate.Admit(Wheel()));
        Assert.Null(gate.Admit(new KeyCommand(Guid.NewGuid(), ActionCatalog.All["lights"], KeyPhase.Down, null)));
        Assert.Null(gate.Admit(new ChatCommand(Guid.NewGuid(), "/wave")));
        Assert.Equal(RejectReason.RateLimited, gate.Admit(Wheel()));
        Assert.Null(gate.Admit(new KeyCommand(Guid.NewGuid(), ActionCatalog.All["lights"], KeyPhase.Up, null)));
        Assert.Null(gate.Admit(new ReleaseAllCommand(Guid.NewGuid())));
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Null(gate.Admit(Wheel()));
    }

    [Fact]
    public void DuplicateIdCannotReplayOrChangeWheelAndRejectedIdsRemainConsumed()
    {
        var gate = new CommandGate(new FakeTimeProvider(), burst: 1);
        var first = Wheel();
        Assert.Null(gate.Admit(first));
        Assert.Equal(RejectReason.Duplicate, gate.Admit(first));
        Assert.Equal(RejectReason.Duplicate, gate.Admit(first with { Spec = WheelActionCatalog.All["accelerationLimiter"], Step = -1 }));
        Assert.Equal(RejectReason.Duplicate, gate.Admit(new ReleaseAllCommand(first.Id)));
        var limited = Wheel();
        Assert.Equal(RejectReason.RateLimited, gate.Admit(limited));
        Assert.Equal(RejectReason.Duplicate, gate.Admit(limited));
    }

    private static WheelCommand Wheel() => new(Guid.NewGuid(), WheelActionCatalog.All["speedLimiter"], 1);
}
