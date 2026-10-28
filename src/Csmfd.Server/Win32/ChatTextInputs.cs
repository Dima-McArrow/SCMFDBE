using static Csmfd.Server.Win32.NativeMethods;

namespace Csmfd.Server.Win32;

/// <summary>Pure layout mapping, validated in full before any text is injected.</summary>
internal static class ChatTextInputs
{
    public static Input[]? Build(string text, Func<ushort, short> characterKey, Func<uint, uint> scanCode)
    {
        var result = new List<Input>();
        foreach (var character in text)
        {
            // Emote syntax only. Ctrl/Alt mappings and unmappable characters must never become shortcuts.
            if (character != '/' && character != '_' && !char.IsAsciiLetterOrDigit(character)) { return null; }
            var mapping = characterKey(character);
            var modifiers = (mapping >> 8) & 0xff;
            if (mapping == -1 || modifiers > 1) { return null; }
            var scan = scanCode((uint)(mapping & 0xff));
            if (scan == 0 || scan > 0xff) { return null; }
            uint shift = modifiers == 1 ? scanCode(0xA0) : 0;
            if (modifiers == 1 && (shift == 0 || shift > 0xff)) { return null; }
            if (shift != 0) { result.Add(Key((ushort)shift, false)); }
            result.Add(Key((ushort)scan, false));
            result.Add(Key((ushort)scan, true));
            if (shift != 0) { result.Add(Key((ushort)shift, true)); }
        }

        return [.. result];
    }

    public static Input[] ReleasesAfterPartial(Input[] inputs, int inserted)
    {
        var held = new List<ushort>();
        foreach (var input in inputs.AsSpan(0, Math.Clamp(inserted, 0, inputs.Length)))
        {
            var key = input.Data.Keyboard;
            if ((key.Flags & KeyEventFKeyUp) != 0) { held.Remove(key.ScanCode); }
            else { held.Add(key.ScanCode); }
        }

        held.Reverse();
        return [.. held.Select(scan => Key(scan, true))];
    }

    private static Input Key(ushort scan, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                ScanCode = scan,
                Flags = KeyEventFScanCode | (up ? KeyEventFKeyUp : 0),
            }
        },
    };
}
