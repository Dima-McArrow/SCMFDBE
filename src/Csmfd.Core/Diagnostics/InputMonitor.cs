using System.Collections.Concurrent;
using System.Globalization;
using Csmfd.Core.Input;
using Csmfd.Protocol;

namespace Csmfd.Core.Diagnostics;

public enum MonitorEntryKind
{
    /// <summary>A command received from the iPad.</summary>
    Received,

    /// <summary>The terminal ack of a command.</summary>
    Result,

    /// <summary>Input released by the server on its own (focus lost, watchdog...).</summary>
    Cancelled,

    /// <summary>Events actually handed to Windows (SendInput).</summary>
    Injected,
}

/// <summary>One line of the local input monitor. Ref is the first 8 hex digits of the command id.</summary>
public sealed record MonitorEntry(DateTimeOffset At, MonitorEntryKind Kind, string Ref, string Text);

/// <summary>
/// Local, in-memory view of what the iPad sends and what reaches Windows (tray "Input monitor").
/// Records only while enabled (the window is open); otherwise every call is a single flag check.
/// Bounded, never written to disk, never exposed over the network. Producers only enqueue, so it is
/// safe to call from the executor lock and the receive loop.
/// </summary>
public sealed class InputMonitor(TimeProvider time)
{
    public const int Capacity = 2000;

    private readonly ConcurrentQueue<MonitorEntry> _queue = new();
    private int _count;
    private long _dropped;
    private volatile bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                Clear();
            }
        }
    }

    /// <summary>Entries discarded because the window did not drain fast enough.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public bool TryDequeue(out MonitorEntry entry)
    {
        if (_queue.TryDequeue(out entry!))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }

        return false;
    }

    public void Clear()
    {
        while (TryDequeue(out _))
        {
        }
    }

    public void Received(string device, CommandMessage command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_enabled)
        {
            return;
        }

        var text = command switch
        {
            KeyCommand key => Describe(key),
            WheelCommand wheel => $"{wheel.Spec.Action}  {string.Join('+', ModifierLabels(wheel.Spec.Mods))}+Wheel  {(wheel.Step > 0 ? "increase" : "decrease")}",
            ChatCommand chat => "chat " + chat.Text,
            ReleaseAllCommand => "releaseAll",
            _ => command.GetType().Name,
        };
        Add(MonitorEntryKind.Received, Ref(command.Id), $"{device}: {text}");
    }

    public void Result(Guid commandId, AckStatus status, RejectReason? reason)
    {
        if (_enabled)
        {
            Add(MonitorEntryKind.Result, Ref(commandId), reason is { } r ? $"{status.ToWire()} ({r.ToWire()})" : status.ToWire());
        }
    }

    public void Cancelled(Guid commandId, ErrorCode code, CancelReason reason)
    {
        if (_enabled)
        {
            Add(MonitorEntryKind.Cancelled, Ref(commandId), $"{code.ToWire()} ({reason.ToWire()})");
        }
    }

    public void Injected(ReadOnlySpan<InputEvent> events, int inserted)
    {
        if (!_enabled || events.IsEmpty)
        {
            return;
        }

        var parts = new string[events.Length];
        for (var i = 0; i < events.Length; i++)
        {
            parts[i] = events[i].WheelDelta != 0
                ? (events[i].WheelDelta > 0 ? "Wheel+120" : "Wheel-120")
                : KeyLabel(events[i].Key) + (events[i].Down ? "↓" : "↑");
        }

        var text = string.Join(' ', parts);
        Add(MonitorEntryKind.Injected, string.Empty, inserted == events.Length ? text : $"{text}  (only {inserted} inserted)");
    }

    public void InjectedText(string text, bool success)
    {
        if (_enabled)
        {
            Add(MonitorEntryKind.Injected, string.Empty, success ? $"text \"{text}\"" : $"text \"{text}\" (failed)");
        }
    }

    /// <summary>"requestLanding  Alt+N  down" / "quantumEngage  LMB  hold 1500 ms".</summary>
    public static string Describe(KeyCommand key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var chord = string.Join('+', ModifierLabels(key.Spec.Mods).Append(KeyLabel(key.Spec.Key)));
        var phase = key.Phase == KeyPhase.Hold
            ? string.Create(CultureInfo.InvariantCulture, $"hold {key.Ms} ms")
            : key.Phase.ToWire();
        return $"{key.Spec.Action}  {chord}  {phase}";
    }

    public static string KeyLabel(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key switch
        {
            "ctrl" => "Ctrl",
            "shift" => "Shift",
            "alt" => "Alt",
            "ralt" => "RAlt",
            "mouse1" => "LMB",
            "mouse2" => "RMB",
            "mouse3" => "MMB",
            "mouse4" => "Mouse4",
            "mouse5" => "Mouse5",
            _ when key.Length == 1 => key.ToUpperInvariant(),
            _ when key[0] == 'f' && key.Length <= 3 => key.ToUpperInvariant(),
            _ => char.ToUpperInvariant(key[0]) + key[1..],
        };
    }

    private static IEnumerable<string> ModifierLabels(Modifiers mods)
    {
        if (mods.HasFlag(Modifiers.Ctrl))
        {
            yield return "Ctrl";
        }

        if (mods.HasFlag(Modifiers.Shift))
        {
            yield return "Shift";
        }

        if (mods.HasFlag(Modifiers.Alt))
        {
            yield return "Alt";
        }

        if (mods.HasFlag(Modifiers.RAlt))
        {
            yield return "RAlt";
        }
    }

    private static string Ref(Guid id) => id.ToString("N")[..8];

    private void Add(MonitorEntryKind kind, string reference, string text)
    {
        if (Interlocked.Increment(ref _count) > Capacity)
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _dropped);
            return;
        }

        _queue.Enqueue(new MonitorEntry(time.GetLocalNow(), kind, reference, text));
    }
}

/// <summary>Decorator that reports every injected batch to the monitor (exactly what Windows received).</summary>
public sealed class ObservedInputSink(IInputSink inner, InputMonitor monitor) : IInputSink
{
    public int Send(ReadOnlySpan<InputEvent> events)
    {
        var inserted = inner.Send(events);
        monitor.Injected(events, inserted);
        return inserted;
    }

    public bool SendText(string text)
    {
        var success = inner.SendText(text);
        monitor.InjectedText(text, success);
        return success;
    }
}
