using System.Text;
using Csmfd.Protocol;

namespace Csmfd.Tests.Protocol;

public sealed class ClientMessageParserTests
{
    [Fact]
    public void ParsesHelloFixture()
    {
        var hello = AssertSuccess<HelloMessage>(Fixture.Read("client-hello.json"));

        Assert.Equal("CSMFD-iPad", hello.Client);
        Assert.Equal("0.1.0", hello.AppVersion);
        Assert.Equal("Dima iPad", hello.DeviceName);
        Assert.Null(hello.Token);
    }

    [Fact]
    public void ParsesHelloWithToken()
    {
        var token = new string('A', TextRules.TokenLength);
        var hello = AssertSuccess<HelloMessage>(
            $$"""{"v":2,"type":"hello","client":"c","appVersion":"1","deviceName":"d","token":"{{token}}"}""");

        Assert.Equal(token, hello.Token);
    }

    [Fact]
    public void ParsesPairFixture()
    {
        var pair = AssertSuccess<PairMessage>(Fixture.Read("client-pair.json"));

        Assert.Equal("482913", pair.Code);
    }

    [Fact]
    public void ParsesTapKeyFixtureWithModifiers()
    {
        var key = AssertSuccess<KeyCommand>(Fixture.Read("client-key-request-landing-down.json"));

        Assert.Equal(Guid.Parse("4d88b2ad-d306-443a-8524-155575227708"), key.Id);
        Assert.Equal("requestLanding", key.Spec.Action);
        Assert.Equal(KeyPhase.Down, key.Phase);
        Assert.Equal(Modifiers.Alt, key.Spec.Mods);
        Assert.Null(key.Ms);
    }

    [Fact]
    public void ParsesHoldKeyFixture()
    {
        var key = AssertSuccess<KeyCommand>(Fixture.Read("client-key-quantum-engage-hold.json"));

        Assert.Equal("quantumEngage", key.Spec.Action);
        Assert.Equal(KeyPhase.Hold, key.Phase);
        Assert.Equal(1500, key.Ms);
    }

    [Fact]
    public void ParsesUpAsCancelForHoldAction()
    {
        var key = AssertSuccess<KeyCommand>(
            """{"v":2,"type":"key","id":"9b33e72e-4b86-455c-95c6-e3c2d782f172","action":"quantumEngage","phase":"up","key":"mouse1"}""");

        Assert.Equal(KeyPhase.Up, key.Phase);
    }

    [Fact]
    public void ParsesChatReleaseAllAndPingFixtures()
    {
        Assert.Equal("/dance3", AssertSuccess<ChatCommand>(Fixture.Read("client-chat.json")).Text);
        AssertSuccess<ReleaseAllCommand>(Fixture.Read("client-release-all.json"));

        var ping = AssertSuccess<PingMessage>(Fixture.Read("client-ping.json"));
        Assert.Equal("p-123", ping.Id);
        Assert.Equal(1_793_040_000_000, ping.T);
    }

    [Fact]
    public void IgnoresUnknownFields()
    {
        AssertSuccess<PingMessage>("""{"v":2,"type":"ping","id":"p","t":1,"future":{"x":[1,2]}}""");
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    [InlineData("""{"v":2,"type":"ping","id":"p","t":1,}""")]
    [InlineData("""{"v":2,"type":"ping","id":"p","t":1 /* c */}""")]
    [InlineData("""{"v":2,"v":2,"type":"ping","id":"p","t":1}""")]
    [InlineData("""{"v":2,"type":"ping","id":"p","id":"q","t":1}""")]
    [InlineData("""{"type":"ping","id":"p","t":1}""")]
    [InlineData("""{"v":"2","type":"ping","id":"p","t":1}""")]
    [InlineData("""{"v":2.5,"type":"ping","id":"p","t":1}""")]
    [InlineData("""{"v":2,"id":"p","t":1}""")]
    [InlineData("""{"v":2,"type":null,"id":"p","t":1}""")]
    [InlineData("""{"v":2,"type":"ping","t":1}""")]
    [InlineData("""{"v":2,"type":"ping","id":"","t":1}""")]
    [InlineData("""{"v":2,"type":"ping","id":"p","t":"1"}""")]
    [InlineData("""{"v":2,"type":"pair","code":"12345"}""")]
    [InlineData("""{"v":2,"type":"pair","code":"12a456"}""")]
    [InlineData("""{"v":2,"type":"pair","code":123456}""")]
    [InlineData("""{"v":2,"type":"hello","client":"c","appVersion":"1","deviceName":"d"}""")]
    [InlineData("""{"v":2,"type":"hello","client":"c","appVersion":"1","deviceName":null,"token":null}""")]
    [InlineData("""{"v":2,"type":"hello","client":"c","appVersion":"1","deviceName":"","token":null}""")]
    [InlineData("""{"v":2,"type":"hello","client":"c","appVersion":"1","deviceName":"d","token":"short"}""")]
    [InlineData("""{"v":2,"type":"hello","client":"c","appVersion":"1","deviceName":"d","token":5}""")]
    [InlineData("""{"v":2,"type":"hello","client":"c\u0007","appVersion":"1","deviceName":"d","token":null}""")]
    [InlineData("""{"v":2,"type":"key","action":"lights","phase":"down","key":"l"}""")]
    [InlineData("""{"v":2,"type":"key","id":"not-a-uuid","action":"lights","phase":"down","key":"l"}""")]
    [InlineData("""{"v":2,"type":"releaseAll"}""")]
    public void RejectsAsMalformed(string json)
    {
        AssertFailure(json, ParseFailureKind.Malformed);
    }

    [Theory]
    [InlineData("""{"v":1,"type":"hello"}""")]
    [InlineData("""{"v":3,"type":"hello"}""")]
    public void RejectsOtherProtocolVersions(string json)
    {
        AssertFailure(json, ParseFailureKind.UnsupportedVersion);
    }

    [Theory]
    [InlineData("""{"v":2,"type":"foo"}""")]
    [InlineData("""{"v":2,"type":"ack","id":"x","status":"sent"}""")]
    [InlineData("""{"v":2,"type":"state"}""")]
    public void RejectsUnknownOrServerOnlyTypes(string json)
    {
        AssertFailure(json, ParseFailureKind.UnknownType);
    }

    [Theory]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"press","key":"l"}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"down"}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"warp","phase":"down","key":"l"}""", RejectReason.UnknownAction)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"down","key":"f13"}""", RejectReason.UnknownKey)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"down","key":"k"}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"requestLanding","phase":"down","key":"n"}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"requestLanding","phase":"down","key":"n","mods":["ralt"]}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"requestLanding","phase":"down","key":"n","mods":["alt","alt"]}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"requestLanding","phase":"down","key":"n","mods":["alt","ctrl"]}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"down","key":"l","mods":["meta"]}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"down","key":"l","mods":null}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"down","key":"l","mods":"alt"}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"autoLand","phase":"down","key":"n"}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"hold","key":"l","ms":100}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"quantumEngage","phase":"hold","key":"mouse1"}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"quantumEngage","phase":"hold","key":"mouse1","ms":49}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"quantumEngage","phase":"hold","key":"mouse1","ms":10001}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"quantumEngage","phase":"hold","key":"mouse1","ms":1.5}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"quantumEngage","phase":"hold","key":"mouse1","ms":"1500"}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"down","key":"l","ms":100}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"quantumEngage","phase":"up","key":"mouse1","ms":100}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"chat","id":"4d88b2ad-d306-443a-8524-155575227708","text":"dance"}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"chat","id":"4d88b2ad-d306-443a-8524-155575227708","text":"/"}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"chat","id":"4d88b2ad-d306-443a-8524-155575227708","text":"/wave\n"}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"chat","id":"4d88b2ad-d306-443a-8524-155575227708","text":"/wa\tve"}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"chat","id":"4d88b2ad-d306-443a-8524-155575227708","text":42}""", RejectReason.Invalid)]
    [InlineData("""{"v":2,"type":"chat","id":"4d88b2ad-d306-443a-8524-155575227708"}""", RejectReason.Invalid)]
    public void RejectsCommandWithReason(string json, RejectReason expectedReason)
    {
        var failure = AssertFailure(json, ParseFailureKind.CommandRejected);

        Assert.Equal(Guid.Parse("4d88b2ad-d306-443a-8524-155575227708"), failure.CommandId);
        Assert.Equal(expectedReason, failure.Reason);
    }

    [Fact]
    public void RejectsChatLongerThanLimit()
    {
        var text = "/" + new string('a', ProtocolLimits.MaxChatScalars);
        var failure = AssertFailure(
            $$"""{"v":2,"type":"chat","id":"4d88b2ad-d306-443a-8524-155575227708","text":"{{text}}"}""",
            ParseFailureKind.CommandRejected);

        Assert.Equal(RejectReason.Invalid, failure.Reason);
    }

    [Fact]
    public void CountsUnicodeScalarsNotUtf16Units()
    {
        // 63 emoji = 126 UTF-16 code units but 63 scalars + "/" = 64 scalars: within the limit.
        var text = "/" + string.Concat(Enumerable.Repeat("\U0001F680", ProtocolLimits.MaxChatScalars - 1));
        var json = $$"""{"v":2,"type":"chat","id":"4d88b2ad-d306-443a-8524-155575227708","text":"{{text}}"}""";

        Assert.Equal(text, AssertSuccess<ChatCommand>(json).Text);
    }

    [Fact]
    public void RejectsLoneSurrogate()
    {
        var result = Parse("""{"v":2,"type":"chat","id":"4d88b2ad-d306-443a-8524-155575227708","text":"/a\ud800b"}""");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void AcceptsMaximumDepthAndRejectsDeeper()
    {
        // Root object is depth 1, so 7 nested objects reach the limit of 8.
        AssertSuccess<PingMessage>(WithNesting(ProtocolLimits.MaxJsonDepth - 1));
        AssertFailure(WithNesting(ProtocolLimits.MaxJsonDepth), ParseFailureKind.Malformed);
    }

    [Fact]
    public void AcceptsMaximumSizeAndRejectsLarger()
    {
        AssertSuccess<PingMessage>(WithTotalSize(ProtocolLimits.MaxMessageBytes));
        AssertFailure(WithTotalSize(ProtocolLimits.MaxMessageBytes + 1), ParseFailureKind.Malformed);
    }

    private static ParseResult Parse(string json) => ClientMessageParser.Parse(Encoding.UTF8.GetBytes(json));

    private static T AssertSuccess<T>(string json)
        where T : ClientMessage
    {
        var result = Parse(json);
        Assert.True(result.IsSuccess, result.Failure?.Detail);
        return Assert.IsType<T>(result.Message);
    }

    private static ParseFailure AssertFailure(string json, ParseFailureKind expectedKind)
    {
        var result = Parse(json);
        Assert.False(result.IsSuccess);
        Assert.Equal(expectedKind, result.Failure.Kind);
        return result.Failure;
    }

    private static string WithNesting(int nestedObjects)
    {
        var open = string.Concat(Enumerable.Repeat("""{"n":""", nestedObjects));
        var close = new string('}', nestedObjects);
        return $$"""{"v":2,"type":"ping","id":"p","t":1,"x":{{open}}0{{close}}}""";
    }

    private static string WithTotalSize(int totalBytes)
    {
        const string prefix = """{"v":2,"type":"ping","id":"p","t":1,"pad":" """;
        const string suffix = "\"}";
        var padding = totalBytes - Encoding.UTF8.GetByteCount(prefix) - suffix.Length;
        var json = prefix + new string('x', padding) + suffix;
        Assert.Equal(totalBytes, Encoding.UTF8.GetByteCount(json));
        return json;
    }
}
