using Csmfd.Core.Diagnostics;
using Csmfd.Core.Input;
using Csmfd.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Csmfd.Tests.Core;

public sealed class InputMonitorTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void RecordsNothingWhileDisabled()
    {
        var monitor = new InputMonitor(_time);

        monitor.Received("iPad", Key("lights", KeyPhase.Down));
        monitor.Result(Guid.NewGuid(), AckStatus.Sent, null);
        monitor.Injected([new InputEvent("l", true)], 1);

        Assert.False(monitor.TryDequeue(out _));
    }

    [Fact]
    public void DescribesCommandsResultsAndInjectedInput()
    {
        var monitor = new InputMonitor(_time) { Enabled = true };
        var down = Key("requestLanding", KeyPhase.Down);
        var hold = new KeyCommand(Guid.NewGuid(), ActionCatalog.All["quantumEngage"], KeyPhase.Hold, 1500);

        monitor.Received("Dima iPad", down);
        monitor.Received("Dima iPad", hold);
        monitor.Received("Dima iPad", new ChatCommand(Guid.NewGuid(), "/wave"));
        monitor.Result(down.Id, AckStatus.Rejected, RejectReason.GameNotFocused);
        monitor.Cancelled(hold.Id, ErrorCode.InputCancelled, CancelReason.FocusLost);
        monitor.Injected([new InputEvent("alt", true), new InputEvent("n", true)], 1);

        Assert.Equal(
            [
                (MonitorEntryKind.Received, "Dima iPad: requestLanding  Alt+N  down"),
                (MonitorEntryKind.Received, "Dima iPad: quantumEngage  LMB  hold 1500 ms"),
                (MonitorEntryKind.Received, "Dima iPad: chat /wave"),
                (MonitorEntryKind.Result, "rejected (game_not_focused)"),
                (MonitorEntryKind.Cancelled, "input_cancelled (focus_lost)"),
                (MonitorEntryKind.Injected, "Alt↓ N↓  (only 1 inserted)"),
            ],
            Drain(monitor).Select(e => (e.Kind, e.Text)));
    }

    [Fact]
    public void ResultReferencesTheCommandId()
    {
        var monitor = new InputMonitor(_time) { Enabled = true };
        var command = Key("lights", KeyPhase.Down);

        monitor.Received("iPad", command);
        monitor.Result(command.Id, AckStatus.Sent, null);

        var entries = Drain(monitor);
        Assert.Equal(command.Id.ToString("N")[..8], entries[0].Ref);
        Assert.Equal(entries[0].Ref, entries[1].Ref);
    }

    [Theory]
    [InlineData("speedLimiter", "alt", 1, "Alt", "increase", "Wheel+120")]
    [InlineData("accelerationLimiter", "ralt", -1, "RAlt", "decrease", "Wheel-120")]
    public void WheelIsVisibleThroughTheExistingObservedSink(string action, string modifier, int step, string label, string direction, string wheelLabel)
    {
        var monitor = new InputMonitor(_time) { Enabled = true };
        var inner = new FakeInputSink(_time);
        var sink = new ObservedInputSink(inner, monitor);
        var command = new WheelCommand(Guid.NewGuid(), WheelActionCatalog.All[action], step);

        monitor.Received("iPad", command);
        Assert.Equal(3, sink.Send([new InputEvent(modifier, true), InputEvent.Wheel(step), new InputEvent(modifier, false)]));
        monitor.Result(command.Id, AckStatus.Sent, null);

        Assert.Equal(
            [$"iPad: {action}  {label}+Wheel  {direction}", $"{label}↓ {wheelLabel} {label}↑", "sent"],
            Drain(monitor).Select(e => e.Text));
    }

    [Fact]
    public void IsBoundedAndClearsWhenDisabled()
    {
        var monitor = new InputMonitor(_time) { Enabled = true };
        for (var i = 0; i < InputMonitor.Capacity + 5; i++)
        {
            monitor.Result(Guid.NewGuid(), AckStatus.Sent, null);
        }

        Assert.Equal(5, monitor.Dropped);
        monitor.Enabled = false;
        Assert.False(monitor.TryDequeue(out _));
    }

    [Fact]
    public void ObservedSinkPassesThroughAndRecords()
    {
        var monitor = new InputMonitor(_time) { Enabled = true };
        var inner = new FakeInputSink(_time) { InsertLimit = 1 };
        var sink = new ObservedInputSink(inner, monitor);

        Assert.Equal(1, sink.Send([new InputEvent("mouse1", true), new InputEvent("mouse1", false)]));
        Assert.True(sink.SendText("/wave"));

        Assert.Equal(["0:mouse1v", "0:text(/wave)"], inner.Timeline);
        Assert.Equal(["LMB↓ LMB↑  (only 1 inserted)", "text \"/wave\""], Drain(monitor).Select(e => e.Text));
    }

    [Theory]
    [InlineData("ctrl", "Ctrl")]
    [InlineData("ralt", "RAlt")]
    [InlineData("mouse3", "MMB")]
    [InlineData("f12", "F12")]
    [InlineData("backspace", "Backspace")]
    [InlineData("pagedown", "Pagedown")]
    [InlineData("b", "B")]
    public void LabelsKeysForHumans(string key, string label)
    {
        Assert.Equal(label, InputMonitor.KeyLabel(key));
    }

    private static KeyCommand Key(string action, KeyPhase phase) => new(Guid.NewGuid(), ActionCatalog.All[action], phase, null);

    private static List<MonitorEntry> Drain(InputMonitor monitor)
    {
        var entries = new List<MonitorEntry>();
        while (monitor.TryDequeue(out var entry))
        {
            entries.Add(entry);
        }

        return entries;
    }
}
