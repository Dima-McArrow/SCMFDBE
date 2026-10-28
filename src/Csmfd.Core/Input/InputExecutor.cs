using Csmfd.Protocol;

namespace Csmfd.Core.Input;

public sealed record InputExecutorOptions
{
    /// <summary>Minimum time a tap stays down so the game does not miss it (spec 41.11 item 4).</summary>
    public TimeSpan TapMinimum { get; init; } = TimeSpan.FromMilliseconds(60);

    /// <summary>Absolute ceiling for continuous synthetic input (spec 41.11 item 5).</summary>
    public TimeSpan MaxSyntheticHold { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A command older than this when execution starts is rejected as expired (spec 41.6).</summary>
    public TimeSpan MaxCommandAge { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan ChatOpenDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    public TimeSpan ChatSubmitDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public ChatKey ChatKey { get; init; } = ChatKey.Enter;
}

/// <summary>
/// Single owner of synthetic input (spec 41.11). Executes at most one gesture at a time:
/// a new start while anything is active gets "busy", while up/releaseAll/safety always pass.
/// All waits are TimeProvider timers guarded by a token, so a stale callback can never
/// release a newer gesture and the caller's receive loop is never blocked.
/// </summary>
public sealed class InputExecutor : IDisposable
{
    private static readonly string[] ModifierOrder = ["ctrl", "shift", "alt", "ralt"];

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly IInputSink _sink;
    private readonly IGameWindow _game;
    private readonly IPhysicalKeyboard _keyboard;
    private readonly IEmoteAllowlist _emotes;
    private readonly IExecutorOutput _output;
    private readonly InputExecutorOptions _options;

    private Gesture? _active;
    private long _timerToken;
    private bool _paused;
    private bool _sessionLocked;
    private bool _blocked;
    private bool _disposed;

    public InputExecutor(
        TimeProvider time,
        IInputSink sink,
        IGameWindow game,
        IPhysicalKeyboard keyboard,
        IEmoteAllowlist emotes,
        IExecutorOutput output,
        InputExecutorOptions? options = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        _emotes = emotes ?? throw new ArgumentNullException(nameof(emotes));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _options = options ?? new InputExecutorOptions();
    }

    private enum GestureKind
    {
        Tap,
        Hold,
        Chat,
    }

    private enum TimerPurpose
    {
        TapCeiling,
        HoldDeadline,
        DelayedRelease,
        ChatStep,
    }

    private enum ChatStep
    {
        ReleaseOpenKey,
        TypeText,
        PressEnter,
        ReleaseEnter,
    }

    public bool IsBlocked
    {
        get
        {
            lock (_gate)
            {
                return _blocked;
            }
        }
    }

    public bool HasActiveInput
    {
        get
        {
            lock (_gate)
            {
                return _active is not null;
            }
        }
    }

    /// <summary>Executes one admitted command. receivedTimestamp is the TimeProvider timestamp of full receipt.</summary>
    public void Submit(Guid connectionId, CommandMessage command, long receivedTimestamp)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (_gate)
        {
            if (_disposed)
            {
                _output.Ack(connectionId, command.Id, AckStatus.Rejected, RejectReason.Cancelled);
                return;
            }

            switch (command)
            {
                case KeyCommand { Phase: KeyPhase.Up } up:
                    HandleUp(connectionId, up);
                    break;
                case KeyCommand start:
                    HandleStart(connectionId, start, receivedTimestamp);
                    break;
                case ChatCommand chat:
                    HandleChat(connectionId, chat, receivedTimestamp);
                    break;
                case ReleaseAllCommand releaseAll:
                    HandleReleaseAll(connectionId, releaseAll);
                    break;
                default:
                    throw new ArgumentException("Unsupported command type.", nameof(command));
            }
        }
    }

    /// <summary>Safety release of whatever is active (focus lost, session lock, ClearDriver...).</summary>
    public void CancelAll(CancelReason reason)
    {
        lock (_gate)
        {
            if (_active is { } gesture)
            {
                Cancel(gesture, reason);
            }
        }
    }

    /// <summary>Releases input owned by one connection only (replacement, revoke, disconnect).</summary>
    public void CancelConnection(Guid connectionId, CancelReason reason)
    {
        lock (_gate)
        {
            if (_active is { } gesture && gesture.ConnectionId == connectionId)
            {
                Cancel(gesture, reason);
            }
        }
    }

    /// <summary>Local tray "Pause input": releases immediately and rejects new gestures.</summary>
    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            _paused = paused;
            if (paused && _active is { } gesture)
            {
                Cancel(gesture, CancelReason.Paused);
            }
        }
    }

    /// <summary>
    /// Windows session locked or detached from the console (spec 41.11 item 7): releases immediately and
    /// rejects new gestures as game_not_focused until unlocked. The foreground window is not a reliable
    /// signal here: while the secure desktop is shown it can still report the game.
    /// </summary>
    public void SetSessionLocked(bool locked)
    {
        lock (_gate)
        {
            _sessionLocked = locked;
            if (locked && _active is { } gesture)
            {
                Cancel(gesture, CancelReason.SessionLocked);
            }
        }
    }

    /// <summary>Local re-enable after the user checked that no key is stuck.</summary>
    public void ResetBlock()
    {
        lock (_gate)
        {
            _blocked = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_active is { } gesture)
            {
                Cancel(gesture, CancelReason.Shutdown);
            }

            _disposed = true;
        }
    }

    private void HandleStart(Guid connectionId, KeyCommand command, long receivedTimestamp)
    {
        var keys = KeysOf(command.Spec);
        if (CheckCanStart(receivedTimestamp, keys) is { } rejection)
        {
            _output.Ack(connectionId, command.Id, AckStatus.Rejected, rejection);
            return;
        }

        if (!Press(keys))
        {
            _output.Ack(connectionId, command.Id, AckStatus.Rejected, RejectReason.InputFailed);
            return;
        }

        var isHold = command.Phase == KeyPhase.Hold;
        var gesture = new Gesture(connectionId, command.Id, isHold ? GestureKind.Hold : GestureKind.Tap, command.Spec)
        {
            DownTimestamp = _time.GetTimestamp(),
        };
        gesture.Held.AddRange(keys);
        _active = gesture;

        if (isHold)
        {
            Schedule(gesture, TimeSpan.FromMilliseconds(command.Ms!.Value), TimerPurpose.HoldDeadline);
        }
        else
        {
            Schedule(gesture, _options.MaxSyntheticHold, TimerPurpose.TapCeiling);
        }

        _output.Ack(connectionId, command.Id, AckStatus.Sent);
        _output.InputStarted(connectionId, command.Id, command.Spec);
    }

    private void HandleUp(Guid connectionId, KeyCommand up)
    {
        var gesture = _active;
        if (gesture is null
            || gesture.ConnectionId != connectionId
            || gesture.Kind == GestureKind.Chat
            || !string.Equals(gesture.Spec?.Action, up.Spec.Action, StringComparison.Ordinal)
            || gesture.PendingUpId is not null)
        {
            _output.Ack(connectionId, up.Id, AckStatus.Ignored);
            return;
        }

        if (gesture.Kind == GestureKind.Tap)
        {
            var elapsed = _time.GetElapsedTime(gesture.DownTimestamp);
            if (elapsed < _options.TapMinimum)
            {
                // The ack for this up is sent only after the key is actually released.
                gesture.PendingUpId = up.Id;
                Schedule(gesture, _options.TapMinimum - elapsed, TimerPurpose.DelayedRelease);
                return;
            }
        }

        // Hold cancelled by the user, or a tap already past its minimum: release now.
        var released = ReleaseHeld(gesture);
        Clear(gesture);
        AckRelease(connectionId, up.Id, released);
    }

    private void HandleChat(Guid connectionId, ChatCommand chat, long receivedTimestamp)
    {
        if (!_emotes.IsAllowed(chat.Text))
        {
            _output.Ack(connectionId, chat.Id, AckStatus.Rejected, RejectReason.Invalid);
            return;
        }

        var openKey = _options.ChatKey.ToWire();
        if (CheckCanStart(receivedTimestamp, [openKey, "enter"]) is { } rejection)
        {
            _output.Ack(connectionId, chat.Id, AckStatus.Rejected, rejection);
            return;
        }

        if (!Press([openKey]))
        {
            _output.Ack(connectionId, chat.Id, AckStatus.Rejected, RejectReason.InputFailed);
            return;
        }

        var gesture = new Gesture(connectionId, chat.Id, GestureKind.Chat, spec: null)
        {
            DownTimestamp = _time.GetTimestamp(),
            ChatText = chat.Text,
            ChatStep = ChatStep.ReleaseOpenKey,
        };
        gesture.Held.Add(openKey);
        _active = gesture;
        Schedule(gesture, _options.TapMinimum, TimerPurpose.ChatStep);
    }

    private void HandleReleaseAll(Guid connectionId, ReleaseAllCommand command)
    {
        if (_active is { } gesture && gesture.ConnectionId == connectionId)
        {
            var released = Cancel(gesture, CancelReason.ReleaseAll);
            AckRelease(connectionId, command.Id, released);
            return;
        }

        _output.Ack(connectionId, command.Id, AckStatus.Ignored);
    }

    private RejectReason? CheckCanStart(long receivedTimestamp, IReadOnlyList<string> keys)
    {
        if (_time.GetElapsedTime(receivedTimestamp) > _options.MaxCommandAge)
        {
            return RejectReason.Expired;
        }

        if (_paused)
        {
            return RejectReason.InputPaused;
        }

        if (_blocked)
        {
            return RejectReason.InputFailed;
        }

        if (_active is not null)
        {
            return RejectReason.Busy;
        }

        if (_sessionLocked)
        {
            return RejectReason.GameNotFocused;
        }

        switch (_game.Check())
        {
            case GameFocus.NotRunning:
                return RejectReason.GameNotRunning;
            case GameFocus.Background:
                return RejectReason.GameNotFocused;
        }

        // A physically held modifier or the same key would merge with our input (e.g. N -> Alt+N).
        foreach (var modifier in ModifierOrder)
        {
            if (_keyboard.IsDown(modifier))
            {
                return RejectReason.Busy;
            }
        }

        foreach (var key in keys)
        {
            if (_keyboard.IsDown(key))
            {
                return RejectReason.Busy;
            }
        }

        return null;
    }

    private void OnTimer(long token)
    {
        lock (_gate)
        {
            var gesture = _active;
            if (gesture is null || gesture.TimerToken != token)
            {
                return;
            }

            switch (gesture.TimerPurpose)
            {
                case TimerPurpose.TapCeiling:
                    ReleaseHeld(gesture);
                    Clear(gesture);
                    _output.InputError(gesture.ConnectionId, ErrorCode.AutoReleased, gesture.CommandId, CancelReason.Watchdog);
                    break;
                case TimerPurpose.HoldDeadline:
                    // Normal end of a hold: no extra message (spec 41.10).
                    ReleaseHeld(gesture);
                    Clear(gesture);
                    break;
                case TimerPurpose.DelayedRelease:
                    var released = ReleaseHeld(gesture);
                    Clear(gesture);
                    AckRelease(gesture.ConnectionId, gesture.PendingUpId!.Value, released);
                    break;
                case TimerPurpose.ChatStep:
                    AdvanceChat(gesture);
                    break;
            }
        }
    }

    private void AdvanceChat(Gesture gesture)
    {
        switch (gesture.ChatStep)
        {
            case ChatStep.ReleaseOpenKey:
                if (!ReleaseHeld(gesture))
                {
                    AbortChat(gesture, RejectReason.InputFailed);
                    return;
                }

                gesture.ChatStep = ChatStep.TypeText;
                Schedule(gesture, _options.ChatOpenDelay, TimerPurpose.ChatStep);
                break;

            case ChatStep.TypeText:
                if (_game.Check() != GameFocus.Focused)
                {
                    AbortChat(gesture, RejectReason.GameNotFocused);
                    return;
                }

                if (!_sink.SendText(gesture.ChatText!))
                {
                    if (_sink.TextReleaseFailed) { Block(); }
                    AbortChat(gesture, RejectReason.InputFailed);
                    return;
                }

                gesture.ChatStep = ChatStep.PressEnter;
                Schedule(gesture, _options.ChatSubmitDelay, TimerPurpose.ChatStep);
                break;

            case ChatStep.PressEnter:
                // Never press Enter into another window (spec 41.12).
                if (_game.Check() != GameFocus.Focused)
                {
                    AbortChat(gesture, RejectReason.GameNotFocused);
                    return;
                }

                if (!Press(["enter"]))
                {
                    AbortChat(gesture, RejectReason.InputFailed);
                    return;
                }

                gesture.Held.Add("enter");
                gesture.ChatStep = ChatStep.ReleaseEnter;
                Schedule(gesture, _options.TapMinimum, TimerPurpose.ChatStep);
                break;

            case ChatStep.ReleaseEnter:
                var released = ReleaseHeld(gesture);
                Clear(gesture);
                AckRelease(gesture.ConnectionId, gesture.CommandId, released);
                break;
        }
    }

    private void AbortChat(Gesture gesture, RejectReason reason)
    {
        ReleaseHeld(gesture);
        Clear(gesture);
        _output.Ack(gesture.ConnectionId, gesture.CommandId, AckStatus.Rejected, reason);
    }

    /// <summary>Releases the gesture immediately (bypassing the tap minimum) and reports it.</summary>
    private bool Cancel(Gesture gesture, CancelReason reason)
    {
        var released = ReleaseHeld(gesture);
        Clear(gesture);

        if (gesture.Kind == GestureKind.Chat)
        {
            // The chat ack has not been sent yet; it becomes the terminal rejection.
            _output.Ack(gesture.ConnectionId, gesture.CommandId, AckStatus.Rejected, RejectReason.Cancelled);
        }
        else if (gesture.PendingUpId is { } upId)
        {
            // The user already asked to release; that request is now fulfilled.
            AckRelease(gesture.ConnectionId, upId, released);
        }
        else
        {
            _output.InputError(gesture.ConnectionId, ErrorCode.InputCancelled, gesture.CommandId, reason);
        }

        return released;
    }

    private void AckRelease(Guid connectionId, Guid commandId, bool released)
    {
        if (released)
        {
            _output.Ack(connectionId, commandId, AckStatus.Sent);
        }
        else
        {
            _output.Ack(connectionId, commandId, AckStatus.Rejected, RejectReason.InputFailed);
        }
    }

    /// <summary>Presses keys in order; on partial insertion releases what was pressed.</summary>
    private bool Press(string[] keys)
    {
        var downs = new InputEvent[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            downs[i] = new InputEvent(keys[i], Down: true);
        }

        var inserted = _sink.Send(downs);
        if (inserted == downs.Length)
        {
            return true;
        }

        var pressed = Math.Clamp(inserted, 0, keys.Length);
        if (pressed > 0)
        {
            var ups = new InputEvent[pressed];
            for (var i = 0; i < pressed; i++)
            {
                ups[i] = new InputEvent(keys[pressed - 1 - i], Down: false);
            }

            if (_sink.Send(ups) != ups.Length)
            {
                Block();
            }
        }

        return false;
    }

    /// <summary>Releases held keys in reverse press order (main key first, then modifiers).</summary>
    private bool ReleaseHeld(Gesture gesture)
    {
        if (gesture.Held.Count == 0)
        {
            return true;
        }

        var ups = new InputEvent[gesture.Held.Count];
        for (var i = 0; i < ups.Length; i++)
        {
            ups[i] = new InputEvent(gesture.Held[ups.Length - 1 - i], Down: false);
        }

        gesture.Held.Clear();
        if (_sink.Send(ups) == ups.Length)
        {
            return true;
        }

        Block();
        return false;
    }

    private void Block()
    {
        if (!_blocked)
        {
            _blocked = true;
            _output.InputBlocked();
        }
    }

    private void Schedule(Gesture gesture, TimeSpan due, TimerPurpose purpose)
    {
        gesture.Timer?.Dispose();
        var token = ++_timerToken;
        gesture.TimerToken = token;
        gesture.TimerPurpose = purpose;
        gesture.Timer = _time.CreateTimer(
            static state =>
            {
                var (executor, timerToken) = ((InputExecutor, long))state!;
                executor.OnTimer(timerToken);
            },
            (this, token),
            due,
            Timeout.InfiniteTimeSpan);
    }

    private void Clear(Gesture gesture)
    {
        gesture.Timer?.Dispose();
        gesture.Timer = null;
        gesture.TimerToken = 0;
        if (ReferenceEquals(_active, gesture))
        {
            _active = null;
        }
    }

    private static string[] KeysOf(ActionSpec spec)
    {
        var keys = new List<string>(5);
        if (spec.Mods.HasFlag(Modifiers.Ctrl))
        {
            keys.Add("ctrl");
        }

        if (spec.Mods.HasFlag(Modifiers.Shift))
        {
            keys.Add("shift");
        }

        if (spec.Mods.HasFlag(Modifiers.Alt))
        {
            keys.Add("alt");
        }

        if (spec.Mods.HasFlag(Modifiers.RAlt))
        {
            keys.Add("ralt");
        }

        keys.Add(spec.Key);
        return [.. keys];
    }

    private sealed class Gesture(Guid connectionId, Guid commandId, GestureKind kind, ActionSpec? spec)
    {
        public Guid ConnectionId { get; } = connectionId;

        public Guid CommandId { get; } = commandId;

        public GestureKind Kind { get; } = kind;

        public ActionSpec? Spec { get; } = spec;

        public List<string> Held { get; } = [];

        public long DownTimestamp { get; init; }

        public Guid? PendingUpId { get; set; }

        public ITimer? Timer { get; set; }

        public long TimerToken { get; set; }

        public TimerPurpose TimerPurpose { get; set; }

        public string? ChatText { get; init; }

        public ChatStep ChatStep { get; set; }
    }
}
