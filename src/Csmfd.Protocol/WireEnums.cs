namespace Csmfd.Protocol;

public enum KeyPhase
{
    Down,
    Up,
    Hold,
}

[Flags]
public enum Modifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    RAlt = 8,
}

public enum AckStatus
{
    Sent,
    Ignored,
    Rejected,
}

public enum RejectReason
{
    GameNotFocused,
    GameNotRunning,
    UnknownAction,
    UnknownKey,
    Invalid,
    RateLimited,
    Duplicate,
    Busy,
    Expired,
    InputFailed,
    InputPaused,
    Cancelled,
}

public enum ErrorCode
{
    UnsupportedVersion,
    NotAuthenticated,
    PairInvalid,
    PairLocked,
    PairUnavailable,
    UnknownType,
    Malformed,
    AutoReleased,
    InputCancelled,
    Busy,
    StateUnavailable,
}

public enum CancelReason
{
    FocusLost,
    SessionLocked,
    GameEnded,
    ShipContextChanged,
    ClientReplaced,
    Revoked,
    Disconnected,
    Paused,
    Watchdog,
    Shutdown,
    ReleaseAll,
}

public enum ChatKey
{
    Enter,
    F12,
}

/// <summary>
/// Explicit enum-to-wire mapping. Kept hand-written (no reflection, no naming policy)
/// so a C# rename can never silently change the contract.
/// </summary>
public static class WireNames
{
    public static string ToWire(this KeyPhase value) => value switch
    {
        KeyPhase.Down => "down",
        KeyPhase.Up => "up",
        KeyPhase.Hold => "hold",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static bool TryParseKeyPhase(string wire, out KeyPhase value)
    {
        switch (wire)
        {
            case "down": value = KeyPhase.Down; return true;
            case "up": value = KeyPhase.Up; return true;
            case "hold": value = KeyPhase.Hold; return true;
            default: value = default; return false;
        }
    }

    public static bool TryParseModifier(string wire, out Modifiers value)
    {
        switch (wire)
        {
            case "ctrl": value = Modifiers.Ctrl; return true;
            case "shift": value = Modifiers.Shift; return true;
            case "alt": value = Modifiers.Alt; return true;
            case "ralt": value = Modifiers.RAlt; return true;
            default: value = Modifiers.None; return false;
        }
    }

    public static string ToWire(this AckStatus value) => value switch
    {
        AckStatus.Sent => "sent",
        AckStatus.Ignored => "ignored",
        AckStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToWire(this RejectReason value) => value switch
    {
        RejectReason.GameNotFocused => "game_not_focused",
        RejectReason.GameNotRunning => "game_not_running",
        RejectReason.UnknownAction => "unknown_action",
        RejectReason.UnknownKey => "unknown_key",
        RejectReason.Invalid => "invalid",
        RejectReason.RateLimited => "rate_limited",
        RejectReason.Duplicate => "duplicate",
        RejectReason.Busy => "busy",
        RejectReason.Expired => "expired",
        RejectReason.InputFailed => "input_failed",
        RejectReason.InputPaused => "input_paused",
        RejectReason.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToWire(this ErrorCode value) => value switch
    {
        ErrorCode.UnsupportedVersion => "unsupported_version",
        ErrorCode.NotAuthenticated => "not_authenticated",
        ErrorCode.PairInvalid => "pair_invalid",
        ErrorCode.PairLocked => "pair_locked",
        ErrorCode.PairUnavailable => "pair_unavailable",
        ErrorCode.UnknownType => "unknown_type",
        ErrorCode.Malformed => "malformed",
        ErrorCode.AutoReleased => "auto_released",
        ErrorCode.InputCancelled => "input_cancelled",
        ErrorCode.Busy => "busy",
        ErrorCode.StateUnavailable => "state_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToWire(this CancelReason value) => value switch
    {
        CancelReason.FocusLost => "focus_lost",
        CancelReason.SessionLocked => "session_locked",
        CancelReason.GameEnded => "game_ended",
        CancelReason.ShipContextChanged => "ship_context_changed",
        CancelReason.ClientReplaced => "client_replaced",
        CancelReason.Revoked => "revoked",
        CancelReason.Disconnected => "disconnected",
        CancelReason.Paused => "paused",
        CancelReason.Watchdog => "watchdog",
        CancelReason.Shutdown => "shutdown",
        CancelReason.ReleaseAll => "release_all",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToWire(this ChatKey value) => value switch
    {
        ChatKey.Enter => "enter",
        ChatKey.F12 => "f12",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
