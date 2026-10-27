using System.Collections.Frozen;
using System.Globalization;

namespace Csmfd.Protocol;

/// <summary>
/// Key names accepted on the wire (spec 40.7). Scan codes are an input-layer concern
/// and intentionally live outside the protocol assembly.
/// </summary>
public static class KeyNames
{
    public static FrozenSet<string> All { get; } = Build();

    public static bool IsKnown(string name) => All.Contains(name);

    private static FrozenSet<string> Build()
    {
        var names = new List<string>();

        for (var letter = 'a'; letter <= 'z'; letter++)
        {
            names.Add(new string(letter, 1));
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            names.Add(digit.ToString(CultureInfo.InvariantCulture));
        }

        for (var function = 1; function <= 12; function++)
        {
            names.Add("f" + function.ToString(CultureInfo.InvariantCulture));
        }

        names.AddRange(
        [
            "escape", "backspace", "tab", "enter", "space",
            "up", "down", "left", "right",
            "insert", "delete", "home", "end", "pageup", "pagedown",
            "mouse1", "mouse2", "mouse3", "mouse4", "mouse5",
        ]);

        return names.ToFrozenSet(StringComparer.Ordinal);
    }
}
