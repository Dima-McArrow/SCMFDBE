using Csmfd.Core.Input;
using static Csmfd.Server.Win32.NativeMethods;

namespace Csmfd.Server.Win32;

/// <summary>
/// SendInput with scan codes (games read physical keys, not virtual keys), mouse buttons through
/// MOUSEINPUT and layout-mapped chat keystrokes. Returns the inserted count so the executor can
/// detect UIPI or partial insertion; never retries on its own (spec 41.11 item 2).
/// </summary>
internal sealed class Win32InputSink : IInputSink
{
    private static readonly int[] TextModifiers = [0x10, 0x11, 0x12, 0xA5];

    public bool TextReleaseFailed { get; private set; }

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
        TextReleaseFailed = false;
        if (text.Length == 0)
        {
            return true;
        }

        var thread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        if (thread == 0) { return false; }
        var layout = GetKeyboardLayout(thread);
        var inputs = ChatTextInputs.Build(text, c => VkKeyScanEx(c, layout), vk => MapVirtualKeyEx(vk, 4, layout));
        if (inputs is null) { return false; }
        // The player may have pressed a modifier/key during the chat-open delay.
        if (TextModifiers.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0)
            || inputs.Any(input => (GetAsyncKeyState((int)MapVirtualKeyEx(input.Data.Keyboard.ScanCode, 3, layout)) & 0x8000) != 0))
        {
            return false;
        }
        fixed (Input* pointer = inputs)
        {
            var inserted = SendInput((uint)inputs.Length, pointer, sizeof(Input));
            if (inserted == (uint)inputs.Length) { return true; }
            // Release only keys whose down was inserted, never replay the text or submit Enter.
            var releases = ChatTextInputs.ReleasesAfterPartial(inputs, (int)inserted);
            fixed (Input* releasePointer = releases)
            {
                if (releases.Length > 0)
                {
                    TextReleaseFailed = SendInput((uint)releases.Length, releasePointer, sizeof(Input)) != (uint)releases.Length;
                }
            }
            return false;
        }
    }

    internal static Input ToInput(InputEvent inputEvent)
    {
        if (inputEvent.WheelDelta != 0)
        {
            return new Input
            {
                Type = InputMouse,
                Data = new InputUnion
                {
                    Mouse = new MouseInput
                    {
                        Flags = MouseEventFWheel,
                        MouseData = unchecked((uint)inputEvent.WheelDelta),
                    },
                },
            };
        }

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


}

/// <summary>Physical key state via GetAsyncKeyState, read only for the keys of a new gesture.</summary>
internal sealed class Win32PhysicalKeyboard : IPhysicalKeyboard
{
    public bool IsDown(string key) =>
        KeyMap.All.TryGetValue(key, out var info) && (GetAsyncKeyState(info.ConflictVirtualKey) & 0x8000) != 0;
}
