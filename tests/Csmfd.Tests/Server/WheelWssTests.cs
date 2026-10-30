using Csmfd.Core.Input;
using Csmfd.Protocol;

namespace Csmfd.Tests.Server;

/// <summary>Real loopback WSS; all input goes to the fake sink.</summary>
public sealed class WheelWssTests : IAsyncLifetime
{
    private WssTestServer _server = null!;

    public async Task InitializeAsync() => _server = await WssTestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task WheelBeforeAuthenticationCannotInjectInput()
    {
        using var client = await _server.ConnectAsync();
        await client.SendAsync(Message(Guid.NewGuid(), "speedLimiter", 1));
        Assert.Equal("not_authenticated", (await client.ReceiveAsync())["code"]!.GetValue<string>());
        Assert.Equal(CloseCodes.Unauthenticated, await client.ReceiveCloseAsync());
        Assert.Empty(_server.Sink.Batches);
    }

    [Theory]
    [InlineData("speedLimiter", "alt", 1)]
    [InlineData("speedLimiter", "alt", -1)]
    [InlineData("accelerationLimiter", "ralt", 1)]
    [InlineData("accelerationLimiter", "ralt", -1)]
    public async Task PairedStepGetsOneAckAndDuplicateCannotRepeatIt(string action, string modifier, int step)
    {
        var (client, _) = await _server.PairAsync();
        using (client)
        {
            var id = Guid.NewGuid();
            await client.SendAsync(Message(id, action, step));
            var ack = await client.ReceiveTypeAsync("ack");
            Assert.Equal(id.ToString("D"), ack["id"]!.GetValue<string>());
            Assert.Equal("sent", ack["status"]!.GetValue<string>());
            // The ack follows the modifier release, so all three timed batches are already in.
            Assert.Equal(
                [[new InputEvent(modifier, true)], [InputEvent.Wheel(step)], [new InputEvent(modifier, false)]],
                _server.Sink.Batches);

            await client.SendAsync(Message(id, action, -step));
            var duplicate = await client.ReceiveTypeAsync("ack");
            Assert.Equal("rejected", duplicate["status"]!.GetValue<string>());
            Assert.Equal("duplicate", duplicate["reason"]!.GetValue<string>());
            Assert.Equal(3, _server.Sink.Batches.Count);
        }
    }

    [Fact]
    public async Task WheelReportsFocusAndPartialInsertionFailuresWithoutReplay()
    {
        var (client, _) = await _server.PairAsync();
        using (client)
        {
            _server.Game.Focus = GameFocus.Background;
            await client.SendAsync(Message(Guid.NewGuid(), "speedLimiter", 1));
            var unfocused = await client.ReceiveTypeAsync("ack");
            Assert.Equal("rejected", unfocused["status"]!.GetValue<string>());
            Assert.Equal("game_not_focused", unfocused["reason"]!.GetValue<string>());
            Assert.Empty(_server.Sink.Batches);

            _server.Game.Focus = GameFocus.Focused;
            // Modifier down inserted, wheel insertion fails: only the modifier is released.
            _server.Sink.InsertResults.Enqueue(1);
            _server.Sink.InsertResults.Enqueue(0);
            var failedId = Guid.NewGuid();
            await client.SendAsync(Message(failedId, "accelerationLimiter", -1));
            var failed = await client.ReceiveTypeAsync("ack");
            Assert.Equal(failedId.ToString("D"), failed["id"]!.GetValue<string>());
            Assert.Equal("rejected", failed["status"]!.GetValue<string>());
            Assert.Equal("input_failed", failed["reason"]!.GetValue<string>());
            Assert.Equal([new InputEvent("ralt", false)], _server.Sink.Batches[2]);
            Assert.DoesNotContain(_server.Sink.Timeline, e => e.Contains("wheel(", StringComparison.Ordinal));

            await client.SendAsync(Message(failedId, "accelerationLimiter", -1));
            Assert.Equal("duplicate", (await client.ReceiveTypeAsync("ack"))["reason"]!.GetValue<string>());
            Assert.Equal(3, _server.Sink.Batches.Count);
        }
    }

    [Fact]
    public async Task ClientCannotOverrideTheServerBinding()
    {
        var (client, _) = await _server.PairAsync();
        using (client)
        {
            await client.SendAsync("""{"v":2,"type":"wheel","id":"4d88b2ad-d306-443a-8524-155575227708","action":"speedLimiter","step":1,"mods":["ralt"]}""");
            var ack = await client.ReceiveTypeAsync("ack");
            Assert.Equal("4d88b2ad-d306-443a-8524-155575227708", ack["id"]!.GetValue<string>());
            Assert.Equal("rejected", ack["status"]!.GetValue<string>());
            Assert.Equal("invalid", ack["reason"]!.GetValue<string>());
            Assert.Empty(_server.Sink.Batches);
        }
    }

    private static string Message(Guid id, string action, int step) =>
        $$"""{"v":2,"type":"wheel","id":"{{id:D}}","action":"{{action}}","step":{{step}}}""";
}
