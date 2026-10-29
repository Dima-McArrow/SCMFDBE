using System.Text;
using Csmfd.Protocol;

namespace Csmfd.Tests.Protocol;

public sealed class ActionCatalogTests
{
    /// <summary>Pins the allowlist to spec table 41.10 so any change is a deliberate contract change.</summary>
    [Theory]
    [InlineData("flightReady", "r", Modifiers.RAlt, ActionKind.Tap, null)]
    [InlineData("landingGear", "n", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("vtol", "k", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("lights", "l", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("requestLanding", "n", Modifiers.Alt, ActionKind.Tap, null)]
    [InlineData("autoLand", "n", Modifiers.None, ActionKind.Hold, 1000)]
    [InlineData("powerAll", "u", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("powerWeapons", "p", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("powerThrusters", "i", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("powerShields", "o", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("raiseWeapons", "f5", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("raiseThrusters", "f6", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("raiseShields", "f7", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("resetPower", "f8", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("masterMode", "b", Modifiers.None, ActionKind.Hold, 500)]
    [InlineData("operatorMode", "mouse3", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("quantumEngage", "mouse1", Modifiers.None, ActionKind.Hold, 1500)]
    [InlineData("coupledMode", "c", Modifiers.None, ActionKind.Tap, null)]
    [InlineData("cruiseMode", "c", Modifiers.Alt, ActionKind.Tap, null)]
    [InlineData("resetFlightAccelerometer", "c", Modifiers.RAlt, ActionKind.Tap, null)]
    [InlineData("cycleConfiguration", "k", Modifiers.Alt, ActionKind.Tap, null)]
    [InlineData("exitSeat", "y", Modifiers.None, ActionKind.Hold, 1000)]
    [InlineData("selfDestruct", "backspace", Modifiers.None, ActionKind.Hold, 3000)]
    public void MatchesSpecTable(string action, string key, Modifiers mods, ActionKind kind, int? defaultHoldMs)
    {
        Assert.True(ActionCatalog.TryGet(action, out var spec));
        Assert.Equal(new ActionSpec(action, key, mods, kind, defaultHoldMs), spec);
        Assert.True(KeyNames.IsKnown(key));
    }

    [Fact]
    public void ContainsOnlySpecActions()
    {
        Assert.Equal(23, ActionCatalog.All.Count);
    }

    [Fact]
    public void KeyDictionaryMatchesSpec()
    {
        // a-z, 0-9, f1-f12, 15 named keys, mouse1-mouse5.
        Assert.Equal(26 + 10 + 12 + 15 + 5, KeyNames.All.Count);
    }

    [Fact]
    public void EveryCatalogActionParsesInEveryAllowedPhase()
    {
        foreach (var spec in ActionCatalog.All.Values)
        {
            var startPhase = spec.Kind == ActionKind.Tap ? KeyPhase.Down : KeyPhase.Hold;
            foreach (var phase in new[] { startPhase, KeyPhase.Up })
            {
                var result = ClientMessageParser.Parse(Encoding.UTF8.GetBytes(BuildKeyJson(spec, phase)));

                Assert.True(result.IsSuccess, $"{spec.Action}/{phase}: {result.Failure?.Detail}");
                var command = Assert.IsType<KeyCommand>(result.Message);
                Assert.Same(spec, command.Spec);
            }
        }
    }

    private static string BuildKeyJson(ActionSpec spec, KeyPhase phase)
    {
        var mods = new List<string>();
        if (spec.Mods.HasFlag(Modifiers.Ctrl))
        {
            mods.Add("\"ctrl\"");
        }

        if (spec.Mods.HasFlag(Modifiers.Shift))
        {
            mods.Add("\"shift\"");
        }

        if (spec.Mods.HasFlag(Modifiers.Alt))
        {
            mods.Add("\"alt\"");
        }

        if (spec.Mods.HasFlag(Modifiers.RAlt))
        {
            mods.Add("\"ralt\"");
        }

        var ms = phase == KeyPhase.Hold ? $",\"ms\":{spec.DefaultHoldMs}" : string.Empty;
        return $$"""{"v":2,"type":"key","id":"{{Guid.NewGuid()}}","action":"{{spec.Action}}","phase":"{{phase.ToWire()}}","key":"{{spec.Key}}","mods":[{{string.Join(',', mods)}}]{{ms}}}""";
    }
}
