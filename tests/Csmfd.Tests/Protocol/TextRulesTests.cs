using Csmfd.Protocol;

namespace Csmfd.Tests.Protocol;

public sealed class TextRulesTests
{
    // The iPad rejects control (Cc) and format (Cf) characters alike (spec 41.21).
    [Theory]
    [InlineData("Deliver­cargo")]
    [InlineData("Deliver​cargo")]
    [InlineData("Deliver‍cargo")]
    [InlineData("﻿Deliver cargo")]
    [InlineData("Deliver\tcargo")]
    [InlineData("Deliver\ncargo")]
    public void RejectsControlAndFormatCharacters(string text)
    {
        Assert.False(TextRules.IsValidText(text, 1, 160));
    }

    [Theory]
    [InlineData("Deliver cargo to Lorville")]
    [InlineData("Anvil C8R Pisces Rescue")]
    [InlineData("Livraison à Orison, café")]
    [InlineData("Доставка груза")]
    [InlineData("Ship's cargo – 500 aUEC")]
    public void AcceptsOrdinaryTextIncludingNonAsciiLetters(string text)
    {
        Assert.True(TextRules.IsValidText(text, 1, 160));
    }
}
