using Csmfd.Core.Input;
using Csmfd.Server.Hosting;

namespace Csmfd.Tests.Server;

public sealed class DefaultEmotesTests
{
    private readonly EmoteAllowlist _list = EmoteAllowlist.Parse(ServerApp.DefaultEmotes);

    [Fact]
    public void MatchesTheIPadCatalogSize()
    {
        // Emote.swift: 48 base names (132 commands with their numbered variants).
        Assert.Equal(48, _list.Count);
        Assert.Empty(_list.Skipped);
    }

    [Theory]
    [InlineData("/wave2")]
    [InlineData("/attention")]
    [InlineData("/come4")]
    [InlineData("/chicken")]
    [InlineData("/cheer8")]
    [InlineData("/whistle2")]
    [InlineData("/dance6")]
    [InlineData("/sleep")]
    [InlineData("/asl_hello")]
    [InlineData("/asl_sitdown2")]
    [InlineData("/asl_bullshit")]
    public void AllowsCommandsTheIPadSends(string text)
    {
        Assert.True(_list.IsAllowed(text));
    }
}
