using System.Text;
using Csmfd.Protocol;

namespace Csmfd.Tests.Protocol;

public sealed class WheelCommandTests
{
    [Theory]
    [InlineData("speedLimiter", Modifiers.Alt)]
    [InlineData("accelerationLimiter", Modifiers.RAlt)]
    public void CatalogOwnsExactlyTheTwoLimiterBindings(string action, Modifiers mods)
    {
        Assert.Equal(2, WheelActionCatalog.All.Count);
        Assert.Equal(new WheelActionSpec(action, mods), WheelActionCatalog.All[action]);
        Assert.False(ActionCatalog.TryGet(action, out _));
    }

    [Theory]
    [InlineData("client-wheel-speed-limiter-increase.json", "speedLimiter", 1, Modifiers.Alt)]
    [InlineData("client-wheel-speed-limiter-decrease.json", "speedLimiter", -1, Modifiers.Alt)]
    [InlineData("client-wheel-acceleration-limiter-increase.json", "accelerationLimiter", 1, Modifiers.RAlt)]
    [InlineData("client-wheel-acceleration-limiter-decrease.json", "accelerationLimiter", -1, Modifiers.RAlt)]
    public void ParsesReferenceFixtures(string file, string action, int step, Modifiers mods)
    {
        var result = Parse(Fixture.Read(file));

        Assert.True(result.IsSuccess, result.Failure?.Detail);
        var command = Assert.IsType<WheelCommand>(result.Message);
        Assert.NotEqual(Guid.Empty, command.Id);
        Assert.Same(WheelActionCatalog.All[action], command.Spec);
        Assert.Equal(mods, command.Spec.Mods);
        Assert.Equal(step, command.Step);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("-2")]
    [InlineData("120")]
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("2147483648")]
    [InlineData("\"1\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    public void RejectsAnythingOtherThanAnIntegerSingleStep(string step)
    {
        AssertRejected(Message($$""" "action":"speedLimiter","step":{{step}} """), RejectReason.Invalid);
    }

    [Theory]
    [InlineData("\"action\":\"speedLimiter\"", RejectReason.Invalid)]
    [InlineData("\"step\":1", RejectReason.Invalid)]
    [InlineData("\"action\":null,\"step\":1", RejectReason.Invalid)]
    [InlineData("\"action\":\"SpeedLimiter\",\"step\":1", RejectReason.UnknownAction)]
    [InlineData("\"action\":\"lights\",\"step\":1", RejectReason.UnknownAction)]
    [InlineData("\"action\":\"coupledMode\",\"step\":1", RejectReason.UnknownAction)]
    public void RejectsMissingFieldsOrActionsOutsideTheWheelAllowlist(string fields, RejectReason reason)
    {
        AssertRejected(Message(fields), reason);
    }

    [Theory]
    [InlineData("\"key\":\"mouse3\"")]
    [InlineData("\"mods\":[\"ralt\"]")]
    [InlineData("\"mods\":[]")]
    [InlineData("\"phase\":\"up\"")]
    [InlineData("\"phase\":\"down\"")]
    [InlineData("\"phase\":\"hold\"")]
    [InlineData("\"ms\":100")]
    [InlineData("\"delta\":120")]
    [InlineData("\"axis\":\"horizontal\"")]
    [InlineData("\"steps\":2")]
    [InlineData("\"future\":{}")]
    public void RejectsAllExtraFieldsIncludingClientBindingsAndBatching(string extra)
    {
        AssertRejected(Message($$""" "action":"speedLimiter","step":1,{{extra}} """), RejectReason.Invalid);
    }

    [Theory]
    [InlineData("""{"v":2,"type":"wheel","action":"speedLimiter","step":1}""")]
    [InlineData("""{"v":2,"type":"wheel","id":"invalid","action":"speedLimiter","step":1}""")]
    [InlineData("""{"v":2,"type":"wheel","id":"4d88b2ad-d306-443a-8524-155575227708","action":"speedLimiter","step":1,"step":-1}""")]
    public void InvalidEnvelopeIsMalformed(string json)
    {
        var result = Parse(json);
        Assert.False(result.IsSuccess);
        Assert.Equal(ParseFailureKind.Malformed, result.Failure.Kind);
    }

    [Fact]
    public void WheelActionCannotBeSentAsAKeyCommand()
    {
        AssertRejected("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"speedLimiter","phase":"down","key":"mouse3","mods":["alt"]}""", RejectReason.UnknownAction);
    }

    private static string Message(string fields) =>
        $$"""{"v":2,"type":"wheel","id":"4d88b2ad-d306-443a-8524-155575227708",{{fields}}}""";

    private static ParseResult Parse(string json) => ClientMessageParser.Parse(Encoding.UTF8.GetBytes(json));

    private static void AssertRejected(string json, RejectReason reason)
    {
        var result = Parse(json);
        Assert.False(result.IsSuccess);
        Assert.Equal(ParseFailureKind.CommandRejected, result.Failure.Kind);
        Assert.Equal(Guid.Parse("4d88b2ad-d306-443a-8524-155575227708"), result.Failure.CommandId);
        Assert.Equal(reason, result.Failure.Reason);
    }
}
