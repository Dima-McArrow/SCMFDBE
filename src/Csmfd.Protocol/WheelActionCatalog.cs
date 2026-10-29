using System.Collections.Frozen;

namespace Csmfd.Protocol;

/// <summary>A vertical wheel binding owned by the server, never supplied by the client.</summary>
public sealed record WheelActionSpec(string Action, Modifiers Mods);

/// <summary>Dedicated single-step limiter actions (spec 41.26). Separate from key down/up actions.</summary>
public static class WheelActionCatalog
{
    public static FrozenDictionary<string, WheelActionSpec> All { get; } = new[]
    {
        new WheelActionSpec("speedLimiter", Modifiers.Alt),
        new WheelActionSpec("accelerationLimiter", Modifiers.RAlt),
    }.ToFrozenDictionary(spec => spec.Action, StringComparer.Ordinal);
}
