using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Csmfd.Protocol;

public enum ActionKind
{
    /// <summary>Started with "down", finished with "up".</summary>
    Tap,

    /// <summary>Started with "hold" + ms; "up" cancels it early.</summary>
    Hold,
}

/// <summary>One allowed command. DefaultHoldMs is the spec estimate, not a calibrated game threshold.</summary>
public sealed record ActionSpec(string Action, string Key, Modifiers Mods, ActionKind Kind, int? DefaultHoldMs)
{
    public bool AllowsPhase(KeyPhase phase) => phase switch
    {
        KeyPhase.Up => true,
        KeyPhase.Down => Kind == ActionKind.Tap,
        KeyPhase.Hold => Kind == ActionKind.Hold,
        _ => false,
    };
}

/// <summary>
/// The only action/key/mods combinations the server will ever press (spec 41.10).
/// The server is not a generic network keyboard: anything outside this table is rejected.
/// </summary>
public static class ActionCatalog
{
    public static FrozenDictionary<string, ActionSpec> All { get; } = new[]
    {
        Tap("flightReady", "r", Modifiers.RAlt),
        Tap("landingGear", "n"),
        Tap("vtol", "k"),
        Tap("lights", "l"),
        Tap("requestLanding", "n", Modifiers.Alt),
        Hold("autoLand", "n", 1000),
        Tap("powerAll", "u"),
        Tap("powerWeapons", "p"),
        Tap("powerThrusters", "i"),
        Tap("powerShields", "o"),
        Tap("raiseWeapons", "f5"),
        Tap("raiseThrusters", "f6"),
        Tap("raiseShields", "f7"),
        Tap("resetPower", "f8"),
        Hold("masterMode", "b", 500),
        Tap("operatorMode", "mouse3"),
        Hold("quantumEngage", "mouse1", 1500),
        Hold("exitSeat", "y", 1000),
        Hold("selfDestruct", "backspace", 3000),
    }.ToFrozenDictionary(spec => spec.Action, StringComparer.Ordinal);

    public static bool TryGet(string action, [NotNullWhen(true)] out ActionSpec? spec) =>
        All.TryGetValue(action, out spec);

    private static ActionSpec Tap(string action, string key, Modifiers mods = Modifiers.None) =>
        new(action, key, mods, ActionKind.Tap, DefaultHoldMs: null);

    private static ActionSpec Hold(string action, string key, int defaultHoldMs) =>
        new(action, key, Modifiers.None, ActionKind.Hold, defaultHoldMs);
}
