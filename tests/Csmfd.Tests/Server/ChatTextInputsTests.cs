using Csmfd.Server.Win32;

namespace Csmfd.Tests.Server;

public sealed class ChatTextInputsTests
{
    [Fact]
    public void QwertySlashAndLettersUseScanCodesInsteadOfUnicodePackets()
    {
        var plan = ChatTextInputs.Build("/wave", c => (short)(c == '/' ? 0xbf : char.ToUpperInvariant((char)c)), vk => vk);
        Assert.NotNull(plan);
        Assert.Equal(10, plan.Length);
        Assert.All(plan, input => Assert.Equal(0u, input.Data.Keyboard.Flags & NativeMethods.KeyEventFUnicode));
        Assert.Equal(0xbf, plan[0].Data.Keyboard.ScanCode);
        Assert.Equal(NativeMethods.KeyEventFScanCode | NativeMethods.KeyEventFKeyUp, plan[1].Data.Keyboard.Flags);
    }

    [Fact]
    public void ShiftedSlashIsBalancedAndPartialInsertReleasesOnlyInsertedKeys()
    {
        var plan = ChatTextInputs.Build("/", _ => 0x01be, vk => vk == 0xA0 ? 0x2au : 0x35u)!;
        Assert.Equal(4, plan.Length);
        Assert.Equal(new ushort[] { 0x2a, 0x35, 0x35, 0x2a }, plan.Select(i => i.Data.Keyboard.ScanCode));
        Assert.Empty(ChatTextInputs.ReleasesAfterPartial(plan, 0));
        Assert.Equal(new ushort[] { 0x35, 0x2a }, ChatTextInputs.ReleasesAfterPartial(plan, 2).Select(i => i.Data.Keyboard.ScanCode));
        Assert.Empty(ChatTextInputs.ReleasesAfterPartial(plan, 4));
    }

    [Fact]
    public void UnderscoresInAllowlistedEmotesCanUseShift()
    {
        var plan = ChatTextInputs.Build("/asl_thanks", c => c == '_' ? (short)0x01bd : (short)0x41,
            vk => vk == 0xA0 ? 0x2au : 0x1eu);
        Assert.NotNull(plan);
        Assert.Equal(24, plan.Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x0641)]
    public void UnsupportedLayoutOrShortcutMappingRejectsTheWholeText(short mapping)
    {
        Assert.Null(ChatTextInputs.Build("/wave", c => c == 'e' ? mapping : (short)0x41, _ => 0x1e));
    }

    [Theory]
    [InlineData("/wave\n")]
    [InlineData("/wave ")]
    [InlineData("/Ð¶")]
    public void UnsupportedCharactersNeverReachInput(string text)
    {
        Assert.Null(ChatTextInputs.Build(text, _ => 0x41, _ => 0x1e));
    }
}
