using Csmfd.Core.Input;
using Csmfd.Server.Win32;

namespace Csmfd.Tests.Server;

/// <summary>Pure input conversion only; never invokes SendInput.</summary>
public sealed class WheelInputTests
{
    [Theory]
    [InlineData(1, 120)]
    [InlineData(-1, -120)]
    public void OneStepIsSignedVerticalWheelDeltaWithoutMovementOrButtons(int step, int delta)
    {
        var input = Win32InputSink.ToInput(InputEvent.Wheel(step));
        Assert.Equal(NativeMethods.InputMouse, input.Type);
        Assert.Equal(0x0800u, input.Data.Mouse.Flags);
        Assert.Equal(delta, unchecked((int)input.Data.Mouse.MouseData));
        Assert.Equal(0, input.Data.Mouse.Dx);
        Assert.Equal(0, input.Data.Mouse.Dy);
    }

    [Theory]
    [InlineData("alt", false)]
    [InlineData("ralt", true)]
    public void ModifierUsesTheCorrectAltSideAndBalancedScanCodes(string modifier, bool extended)
    {
        var down = Win32InputSink.ToInput(new InputEvent(modifier, true));
        var up = Win32InputSink.ToInput(new InputEvent(modifier, false));
        var flags = NativeMethods.KeyEventFScanCode | (extended ? NativeMethods.KeyEventFExtendedKey : 0u);
        Assert.Equal(NativeMethods.InputKeyboard, down.Type);
        Assert.Equal((ushort)0x38, down.Data.Keyboard.ScanCode);
        Assert.Equal(down.Data.Keyboard.ScanCode, up.Data.Keyboard.ScanCode);
        Assert.Equal(flags, down.Data.Keyboard.Flags);
        Assert.Equal(flags | NativeMethods.KeyEventFKeyUp, up.Data.Keyboard.Flags);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-120)]
    public void InternalEventFactoryAlsoRejectsNonSingleSteps(int step)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InputEvent.Wheel(step));
    }
}
