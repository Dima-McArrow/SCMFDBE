using System.Runtime.CompilerServices;
using Csmfd.Protocol;
using Csmfd.Server.Win32;

namespace Csmfd.Tests.Server;

/// <summary>Pure checks only: nothing here injects real input.</summary>
public sealed class Win32Tests
{
    [Fact]
    public void EveryProtocolKeyAndModifierIsMapped()
    {
        foreach (var key in KeyNames.All.Concat(["ctrl", "shift", "alt", "ralt"]))
        {
            Assert.True(KeyMap.All.ContainsKey(key), key);
        }

        Assert.Equal(KeyNames.All.Count + 4, KeyMap.All.Count);
    }

    [Theory]
    [InlineData("n", 0x31, false)]
    [InlineData("r", 0x13, false)]
    [InlineData("y", 0x15, false)]
    [InlineData("b", 0x30, false)]
    [InlineData("1", 0x02, false)]
    [InlineData("0", 0x0B, false)]
    [InlineData("f5", 0x3F, false)]
    [InlineData("f8", 0x42, false)]
    [InlineData("f10", 0x44, false)]
    [InlineData("f11", 0x57, false)]
    [InlineData("backspace", 0x0E, false)]
    [InlineData("alt", 0x38, false)]
    [InlineData("ralt", 0x38, true)]
    [InlineData("up", 0x48, true)]
    [InlineData("pagedown", 0x51, true)]
    public void ScanCodesMatchSpecTable(string key, int scanCode, bool extended)
    {
        var info = KeyMap.All[key];

        Assert.Equal(scanCode, info.ScanCode);
        Assert.Equal(extended, info.Extended);
        Assert.Equal(MouseButton.None, info.Mouse);
    }

    [Fact]
    public void LettersHaveDistinctScanCodes()
    {
        var codes = Enumerable.Range('a', 26).Select(c => KeyMap.All[new string((char)c, 1)].ScanCode).ToList();

        Assert.Equal(26, codes.Distinct().Count());
    }

    [Fact]
    public void MouseButtonsUseMouseInput()
    {
        Assert.Equal(MouseButton.Left, KeyMap.All["mouse1"].Mouse);
        Assert.Equal(MouseButton.Right, KeyMap.All["mouse2"].Mouse);
        Assert.Equal(MouseButton.Middle, KeyMap.All["mouse3"].Mouse);
        Assert.Equal(MouseButton.X1, KeyMap.All["mouse4"].Mouse);
        Assert.Equal(MouseButton.X2, KeyMap.All["mouse5"].Mouse);
    }

    [Fact]
    public void InputStructMatchesWin64Layout()
    {
        // SendInput rejects every event when cbSize is wrong; INPUT is 40 bytes on x64.
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Unsafe.SizeOf<NativeMethods.Input>());
    }

    [Fact]
    public void EmptyBatchInjectsNothing()
    {
        Assert.Equal(0, new Win32InputSink().Send([]));
        Assert.True(new Win32InputSink().SendText(string.Empty));
    }

    [Fact]
    public void ReadsBuildFromManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "csmfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Bin64"));
        try
        {
            File.WriteAllText(
                Path.Combine(root, "build_manifest.id"),
                """{"Data":{"Branch":"sc-alpha-4.10.2","RequestedP4ChangeNum":"12913470"}}""");

            Assert.Equal("12913470", GameMonitor.ReadBuild(Path.Combine(root, "Bin64", "StarCitizen.exe")));
            Assert.Null(GameMonitor.ReadBuild(Path.Combine(root, "missing", "Bin64", "StarCitizen.exe")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
