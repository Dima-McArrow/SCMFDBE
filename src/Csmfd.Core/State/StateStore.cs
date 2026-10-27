using Csmfd.Protocol;

namespace Csmfd.Core.State;

/// <summary>
/// Single owner of the published state (spec 41.8). Mutations are merged into one pending
/// delta (top-level keys replaced, never deep-merged) until the publisher takes it; seq is
/// allocated at take time, so it is strictly increasing in the order messages are sent.
/// </summary>
public sealed class StateStore
{
    private readonly Lock _gate = new();
    private StateSnapshot _current;
    private StateDelta _pending = new();
    private long _seq;

    public StateStore()
    {
        SessionId = Guid.NewGuid();
        _current = new StateSnapshot(
            new GameState(Running: false, Focused: false, Build: null),
            new LogState(LogStatus.Missing, Guid.NewGuid(), ObservedAt: null),
            Ship: null,
            new ShipContextState(0, ShipContextStatus.Unknown, ShipContextReason.Startup, null, null, null),
            QtTargetState.Unknown,
            HangarRequestState.Idle);
    }

    /// <summary>Raised outside the lock after a real change; true means "publish now, skip debounce".</summary>
    public event Action<bool>? Changed;

    /// <summary>New UUID per server start (spec 41.7); not a game session.</summary>
    public Guid SessionId { get; }

    public StateSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Applies a pure transformation; unchanged top-level objects produce no delta.</summary>
    public void Update(Func<StateSnapshot, StateSnapshot> mutate, bool urgent = false)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        bool changed;
        lock (_gate)
        {
            var before = _current;
            var after = mutate(before);
            var delta = _pending;

            if (!Equals(before.Game, after.Game))
            {
                delta = delta with { Game = after.Game };
            }

            if (!Equals(before.Log, after.Log))
            {
                delta = delta with { Log = after.Log };
            }

            if (!Equals(before.Ship, after.Ship))
            {
                delta = delta with { Ship = new ShipUpdate(after.Ship) };
            }

            if (!Equals(before.ShipContext, after.ShipContext))
            {
                delta = delta with { ShipContext = after.ShipContext };
            }

            if (!Equals(before.QtTarget, after.QtTarget))
            {
                delta = delta with { QtTarget = after.QtTarget };
            }

            if (!Equals(before.HangarRequest, after.HangarRequest))
            {
                delta = delta with { HangarRequest = after.HangarRequest };
            }

            changed = !ReferenceEquals(delta, _pending);
            _current = after;
            _pending = delta;
        }

        if (changed)
        {
            Changed?.Invoke(urgent);
        }
    }

    /// <summary>Full snapshot for a new connection; discards the pending delta it already contains.</summary>
    public (StateSnapshot Snapshot, long Seq) TakeFull()
    {
        lock (_gate)
        {
            _pending = new StateDelta();
            return (_current, NextSeq());
        }
    }

    /// <summary>Pending changes since the last take, or null when nothing changed.</summary>
    public (StateDelta Delta, long Seq)? TakeDelta()
    {
        lock (_gate)
        {
            if (_pending.IsEmpty)
            {
                return null;
            }

            var delta = _pending;
            _pending = new StateDelta();
            return (delta, NextSeq());
        }
    }

    private long NextSeq()
    {
        // 2^53 messages are unreachable in practice (~285,000 years at 1,000/s); fail loudly instead of wrapping.
        if (_seq >= ProtocolLimits.MaxSafeInteger)
        {
            throw new InvalidOperationException("State sequence exhausted; restart the server.");
        }

        return ++_seq;
    }
}
