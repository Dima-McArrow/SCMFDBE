using Csmfd.Server.Hosting;

namespace Csmfd.Tests.Server;

public sealed class GamePathsTests
{
    private const string Root = @"C:\Program Files\Roberts Space Industries\StarCitizen";

    [Theory]
    [InlineData("LIVE")]
    [InlineData("PTU")]
    [InlineData("EPTU")]
    public void DerivesTheLogNextToTheChannelFolder(string channel)
    {
        var exe = Path.Combine(Root, channel, "Bin64", "StarCitizen.exe");

        Assert.True(GamePaths.TryFromExe(exe, out var paths, out var error));
        Assert.Null(error);
        Assert.Equal(exe, paths.ExePath);
        Assert.Equal(Path.Combine(Root, channel, "Game.log"), paths.LogPath);
        Assert.Equal(channel, paths.Channel);
    }

    [Fact]
    public void AcceptsAnyCasingAndNormalizesThePath()
    {
        Assert.True(GamePaths.TryFromExe(@"D:\Games\SC\LIVE\bin64\..\BIN64\starcitizen.EXE", out var paths, out _));
        Assert.Equal(@"D:\Games\SC\LIVE\BIN64\starcitizen.EXE", paths.ExePath);
        Assert.Equal(@"D:\Games\SC\LIVE\Game.log", paths.LogPath);
        Assert.Equal("LIVE", paths.Channel);
    }

    [Theory]
    [InlineData(@"C:\Games\SC\LIVE\Bin64\RSI Launcher.exe")]
    [InlineData(@"C:\Games\SC\LIVE\StarCitizen.exe")]
    [InlineData(@"C:\Bin64\StarCitizen.exe")]
    [InlineData(@"\\nas\games\SC\LIVE\Bin64\StarCitizen.exe")]
    [InlineData(@"LIVE\Bin64\StarCitizen.exe")]
    [InlineData("")]
    public void RejectsOtherLayouts(string exe)
    {
        Assert.False(GamePaths.TryFromExe(exe, out var paths, out var error));
        Assert.Null(paths);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
