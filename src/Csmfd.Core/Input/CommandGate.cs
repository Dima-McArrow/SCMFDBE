using Csmfd.Protocol;

namespace Csmfd.Core.Input;

/// <summary>
/// Per-connection admission control before the executor (spec 41.6, 41.10):
/// duplicate ids within the last 200 are never executed again, and new gestures
/// (down/hold/chat) are limited to 30/s with a burst of 30. Up and releaseAll are never rate limited.
/// Not thread-safe: owned by one connection's receive loop.
/// </summary>
public sealed class CommandGate
{
    private readonly TimeProvider _time;
    private readonly int _capacity;
    private readonly double _ratePerSecond;
    private readonly double _burst;
    private readonly HashSet<Guid> _seen = [];
    private readonly Queue<Guid> _order = new();

    private double _tokens;
    private long _lastRefill;

    public CommandGate(TimeProvider time, int dedupCapacity = 200, double ratePerSecond = 30, double burst = 30)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _capacity = dedupCapacity;
        _ratePerSecond = ratePerSecond;
        _burst = burst;
        _tokens = burst;
        _lastRefill = time.GetTimestamp();
    }

    /// <summary>Returns null when the command may proceed, otherwise the rejection reason.</summary>
    public RejectReason? Admit(CommandMessage command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_seen.Add(command.Id))
        {
            return RejectReason.Duplicate;
        }

        _order.Enqueue(command.Id);
        if (_order.Count > _capacity)
        {
            _seen.Remove(_order.Dequeue());
        }

        if (!StartsGesture(command))
        {
            return null;
        }

        Refill();
        if (_tokens < 1)
        {
            return RejectReason.RateLimited;
        }

        _tokens -= 1;
        return null;
    }

    private static bool StartsGesture(CommandMessage command) => command switch
    {
        KeyCommand key => key.Phase != KeyPhase.Up,
        ChatCommand => true,
        _ => false,
    };

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_lastRefill, now).TotalSeconds;
        _lastRefill = now;
        _tokens = Math.Min(_burst, _tokens + (elapsed * _ratePerSecond));
    }
}
