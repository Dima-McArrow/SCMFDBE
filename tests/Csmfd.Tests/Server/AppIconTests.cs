using System.Buffers.Binary;
using System.Drawing;
using Csmfd.Server.Tray;

namespace Csmfd.Tests.Server;

public sealed class AppIconTests
{
    /// <summary>Tray sizes for 100%-250% scaling and window caption sizes.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(64)]
    public void EmbeddedIconHasAFrameForEveryTraySize(int size)
    {
        using var icon = AppIcon.Load(new Size(size, size));

        Assert.Equal(new Size(size, size), icon.Size);
    }

    /// <summary>
    /// Explorer uses the 256 px frame (PNG-compressed, which System.Drawing.Icon skips), so it is checked
    /// in the ICONDIR header directly: width/height byte 0 means 256.
    /// </summary>
    [Fact]
    public void IconFileContainsAll8Frames()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(AppIcon.ResourceName)!;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);

        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        var sizes = Enumerable.Range(0, count)
            .Select(i => bytes[6 + (16 * i)] == 0 ? 256 : bytes[6 + (16 * i)])
            .Order()
            .ToArray();

        Assert.Equal([16, 20, 24, 32, 40, 48, 64, 256], sizes);
    }
}
