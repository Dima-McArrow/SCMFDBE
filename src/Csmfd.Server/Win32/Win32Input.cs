using Csmfd.Core.Input;
using static Csmfd.Server.Win32.NativeMethods;

namespace Csmfd.Server.Win32;

/// <summary>
/// SendInput with scan codes (games read physical keys, not virtual keys), mouse buttons through
/// MOUSEINPUT and chat text as KEYEVENTF_UNICODE. Returns the inserted count so the executor can
/// detect UIPI or partial insertion; never retries on its own (spec 41.11 item 2).
/// </summary>
internal sealed class Win32InputSink : IInputSink
{
    public unsafe int Send(ReadOnlySpan<InputEvent> events)
    {
        if (events.IsEmpty)
        {
            return 0;
        }

        var inputs = new Input[events.Length];
        for (var i = 0; i < events.Length; i++)
        {
            inputs[i] = ToInput(events[i]);
        }

        fixed (Input* pointer = inputs)
        {
            return (int)SendInput((uint)inputs.Length, pointer, sizeof(Input));
        }
    }

    public unsafe bool SendText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return true;
        }

        // Each UTF-16 unit is a down/up pair; surrogate pairs are sent as two units, as Windows expects.
        var inputs = new Input[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            inputs[2 * i] = UnicodeInput(text[i], KeyEventFUnicode);
            inputs[(2 * i) + 1] = UnicodeInput(text[i], KeyEventFUnicode | KeyEventFKeyUp);
        }

        fixed (Input* pointer = inputs)
        {
            return SendInput((uint)inputs.Length, pointer, sizeof(Input)) == (uint)inputs.Length;
        }
    }

    private static Input ToInput(InputEvent inputEvent)
    {
        if (!KeyMap.All.TryGetValue(inputEvent.Key, out var info))
        {
            throw new ArgumentException("Key is not in the key map.", nameof(inputEvent));
        }

        if (info.Mouse != MouseButton.None)
        {
            var (flags, data) = MouseFlags(info.Mouse, inputEvent.Down);
            return new Input
            {
                Type = InputMouse,
                Data = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = data } },
            };
        }

        var keyFlags = KeyEventFScanCode;
        if (info.Extended)
        {
            keyFlags |= KeyEventFExtendedKey;
        }

        if (!inputEvent.Down)
        {
            keyFlags |= KeyEventFKeyUp;
        }

        return new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = info.ScanCode, Flags = keyFlags } },
        };
    }

    private static (uint Flags, uint Data) MouseFlags(MouseButton button, bool down) => button switch
    {
        MouseButton.Left => (down ? MouseEventFLeftDown : MouseEventFLeftUp, 0),
        MouseButton.Right => (down ? MouseEventFRightDown : MouseEventFRightUp, 0),
        MouseButton.Middle => (down ? MouseEventFMiddleDown : MouseEventFMiddleUp, 0),
        MouseButton.X1 => (down ? MouseEventFXDown : MouseEventFXUp, XButton1),
        MouseButton.X2 => (down ? MouseEventFXDown : MouseEventFXUp, XButton2),
        _ => throw new ArgumentOutOfRangeException(nameof(button)),
    };

    private static Input UnicodeInput(char character, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = flags } },
    };
}

/// <summary>Physical key state via GetAsyncKeyState, read only for the keys of a new gesture.</summary>
internal sealed class Win32PhysicalKeyboard : IPhysicalKeyboard
{
    public bool IsDown(string key) =>
        KeyMap.All.TryGetValue(key, out var info) && (GetAsyncKeyState(info.ConflictVirtualKey) & 0x8000) != 0;
}
