using System.Collections.Frozen;

namespace Csmfd.Server.Win32;

internal enum MouseButton
{
    None,
    Left,
    Right,
    Middle,
    X1,
    X2,
}

/// <summary>
/// How one protocol key name is injected and how its physical state is read.
/// ScanCode is Set 1 (spec 40.7); Extended marks E0-prefixed keys. ConflictVirtualKey is checked with
/// GetAsyncKeyState; for modifiers it is the side-agnostic key so a held right Ctrl also counts.
/// </summary>
internal readonly record struct KeyInfo(ushort ScanCode, bool Extended, int ConflictVirtualKey, MouseButton Mouse = MouseButton.None);

internal static class KeyMap
{
    public static FrozenDictionary<string, KeyInfo> All { get; } = Build();

    private static FrozenDictionary<string, KeyInfo> Build()
    {
        var map = new Dictionary<string, KeyInfo>(StringComparer.Ordinal);

        // Letters: Set 1 scan codes in a..z order; virtual keys are 'A'..'Z'.
        ushort[] letters =
        [
            0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
            0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C,
        ];
        for (var i = 0; i < letters.Length; i++)
        {
            map[new string((char)('a' + i), 1)] = new KeyInfo(letters[i], false, 'A' + i);
        }

        // Digits: 1..9 are 0x02..0x0A, 0 is 0x0B; virtual keys are '0'..'9'.
        for (var digit = 1; digit <= 9; digit++)
        {
            map[((char)('0' + digit)).ToString()] = new KeyInfo((ushort)(0x01 + digit), false, '0' + digit);
        }

        map["0"] = new KeyInfo(0x0B, false, '0');

        // F1..F10 are 0x3B..0x44, F11 0x57, F12 0x58; VK_F1 = 0x70.
        for (var function = 1; function <= 10; function++)
        {
            map["f" + function.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                new KeyInfo((ushort)(0x3A + function), false, 0x6F + function);
        }

        map["f11"] = new KeyInfo(0x57, false, 0x7A);
        map["f12"] = new KeyInfo(0x58, false, 0x7B);

        map["escape"] = new KeyInfo(0x01, false, 0x1B);
        map["backspace"] = new KeyInfo(0x0E, false, 0x08);
        map["tab"] = new KeyInfo(0x0F, false, 0x09);
        map["enter"] = new KeyInfo(0x1C, false, 0x0D);
        map["space"] = new KeyInfo(0x39, false, 0x20);

        map["up"] = new KeyInfo(0x48, true, 0x26);
        map["down"] = new KeyInfo(0x50, true, 0x28);
        map["left"] = new KeyInfo(0x4B, true, 0x25);
        map["right"] = new KeyInfo(0x4D, true, 0x27);
        map["insert"] = new KeyInfo(0x52, true, 0x2D);
        map["delete"] = new KeyInfo(0x53, true, 0x2E);
        map["home"] = new KeyInfo(0x47, true, 0x24);
        map["end"] = new KeyInfo(0x4F, true, 0x23);
        map["pageup"] = new KeyInfo(0x49, true, 0x21);
        map["pagedown"] = new KeyInfo(0x51, true, 0x22);

        // Modifiers: injected as the left key (right Alt for "ralt"), conflicts checked side-agnostic.
        map["ctrl"] = new KeyInfo(0x1D, false, 0x11);  // VK_CONTROL
        map["shift"] = new KeyInfo(0x2A, false, 0x10); // VK_SHIFT
        map["alt"] = new KeyInfo(0x38, false, 0x12);   // VK_MENU
        map["ralt"] = new KeyInfo(0x38, true, 0xA5);   // VK_RMENU; Star Citizen tells left and right Alt apart

        map["mouse1"] = new KeyInfo(0, false, 0x01, MouseButton.Left);
        map["mouse2"] = new KeyInfo(0, false, 0x02, MouseButton.Right);
        map["mouse3"] = new KeyInfo(0, false, 0x04, MouseButton.Middle);
        map["mouse4"] = new KeyInfo(0, false, 0x05, MouseButton.X1);
        map["mouse5"] = new KeyInfo(0, false, 0x06, MouseButton.X2);

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
