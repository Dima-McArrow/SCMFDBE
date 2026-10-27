using Csmfd.Protocol;

namespace Csmfd.Core.Input;

/// <summary>
/// One synthetic key or mouse button transition. Key is a protocol key name (spec 40.7)
/// or a modifier name: "ctrl", "shift", "alt", "ralt".
/// </summary>
public readonly record struct InputEvent(string Key, bool Down);

/// <summary>Injects input into the OS. Implemented with SendInput on Windows.</summary>
public interface IInputSink
{
    /// <summary>Sends events in one batch and returns how many were actually inserted.</summary>
    int Send(ReadOnlySpan<InputEvent> events);

    /// <summary>Types text as Unicode key events; false if not every event was inserted.</summary>
    bool SendText(string text);
}

public enum GameFocus
{
    NotRunning,
    Background,
    Focused,
}

/// <summary>Whether the configured StarCitizen.exe owns the foreground window right now.</summary>
public interface IGameWindow
{
    GameFocus Check();
}

/// <summary>Current state of a physical key, used only to detect conflicts before a new gesture.</summary>
public interface IPhysicalKeyboard
{
    bool IsDown(string key);
}

/// <summary>Chat commands allowed to be typed into the game (spec 41.12).</summary>
public interface IEmoteAllowlist
{
    bool IsAllowed(string text);
}

/// <summary>
/// Receives executor results. Called while the executor holds its lock, so implementations
/// must only enqueue work and never block or call back into the executor.
/// </summary>
public interface IExecutorOutput
{
    void Ack(Guid connectionId, Guid commandId, AckStatus status, RejectReason? reason = null);

    void InputError(Guid connectionId, ErrorCode code, Guid commandId, CancelReason reason);

    /// <summary>A start gesture (down/hold) reached Windows successfully.</summary>
    void InputStarted(Guid connectionId, Guid commandId, ActionSpec spec);

    /// <summary>Releasing synthetic input failed; new input stays blocked until a local reset.</summary>
    void InputBlocked();
}
