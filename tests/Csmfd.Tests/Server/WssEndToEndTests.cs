using System.Net.WebSockets;
using System.Security.Authentication;
using Csmfd.Core.Input;
using Csmfd.Core.Transport;
using Csmfd.Protocol;

namespace Csmfd.Tests.Server;

/// <summary>Spec 41.18 network rows against a real TLS/WebSocket listener on loopback.</summary>
public sealed class WssEndToEndTests : IAsyncLifetime
{
    private WssTestServer _server = null!;

    public async Task InitializeAsync() => _server = await WssTestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task WrongCertificatePinRefusesTheConnection()
    {
        var wrongPin = new string('0', 64);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => _server.ConnectAsync(wrongPin));

        Assert.True(error is WebSocketException or AuthenticationException, error.GetType().Name);
    }

    [Fact]
    public async Task BrowserOriginIsRefused()
    {
        await Assert.ThrowsAsync<WebSocketException>(() => _server.ConnectAsync(origin: "https://evil.example"));
    }

    [Fact]
    public async Task UnpairedHelloWithClosedWindowGetsPairUnavailable()
    {
        using var client = await _server.ConnectAsync();
        await client.SendAsync("""{"v":2,"type":"hello","client":"c","appVersion":"1","deviceName":"d","token":null}""");

        var error = await client.ReceiveAsync();
        Assert.Equal("pair_unavailable", error["code"]!.GetValue<string>());
        Assert.Equal(CloseCodes.Unauthenticated, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task CommandBeforeAuthenticationNeverReachesInput()
    {
        using var client = await _server.ConnectAsync();
        await client.SendAsync("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"down","key":"l"}""");

        Assert.Equal("not_authenticated", (await client.ReceiveAsync())["code"]!.GetValue<string>());
        Assert.Equal(CloseCodes.Unauthenticated, await client.ReceiveCloseAsync());
        Assert.Empty(_server.Sink.Timeline);
    }

    [Fact]
    public async Task InvalidTokenIsRejectedWithoutState()
    {
        using var client = await _server.ConnectAsync();
        await client.SendAsync($$"""{"v":2,"type":"hello","client":"c","appVersion":"1","deviceName":"d","token":"{{new string('A', 43)}}"}""");

        Assert.Equal("not_authenticated", (await client.ReceiveAsync())["code"]!.GetValue<string>());
        Assert.Equal(CloseCodes.Unauthenticated, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task WrongPairingCodeGetsPairInvalidAndCanRetry()
    {
        var code = _server.Services.Pairing.OpenWindow();
        using var client = await _server.ConnectAsync();
        await client.SendAsync("""{"v":2,"type":"hello","client":"c","appVersion":"1","deviceName":"d","token":null}""");
        await client.ReceiveTypeAsync("pairRequired");

        var wrong = code == "000000" ? "111111" : "000000";
        await client.SendAsync($$"""{"v":2,"type":"pair","code":"{{wrong}}"}""");
        Assert.Equal("pair_invalid", (await client.ReceiveAsync())["code"]!.GetValue<string>());

        await client.SendAsync($$"""{"v":2,"type":"pair","code":"{{code}}"}""");
        Assert.Equal("paired", (await client.ReceiveAsync())["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task PairedClientGetsWelcomeStatePongAndAcks()
    {
        var (client, _) = await _server.PairAsync();
        using (client)
        {
            await client.SendAsync("""{"v":2,"type":"ping","id":"p-1","t":42}""");
            var pong = await client.ReceiveTypeAsync("pong");
            Assert.Equal("p-1", pong["id"]!.GetValue<string>());
            Assert.Equal(42, pong["t"]!.GetValue<long>());

            await client.SendAsync("""{"v":2,"type":"key","id":"4d88b2ad-d306-443a-8524-155575227708","action":"requestLanding","phase":"down","key":"n","mods":["alt"]}""");
            var ack = await client.ReceiveTypeAsync("ack");
            Assert.Equal("sent", ack["status"]!.GetValue<string>());

            await client.SendAsync("""{"v":2,"type":"key","id":"5d88b2ad-d306-443a-8524-155575227708","action":"requestLanding","phase":"up","key":"n","mods":["alt"]}""");
            var upAck = await client.ReceiveTypeAsync("ack");
            Assert.Equal("5d88b2ad-d306-443a-8524-155575227708", upAck["id"]!.GetValue<string>());
            Assert.Equal("sent", upAck["status"]!.GetValue<string>());

            Assert.Equal(["altv", "nv", "n^", "alt^"], _server.Sink.Timeline.Select(e => e[(e.IndexOf(':', StringComparison.Ordinal) + 1)..]));
        }
    }

    [Fact]
    public async Task InputMonitorShowsCommandInjectionAndResultInOrder()
    {
        var monitor = _server.Services.Monitor!;
        monitor.Enabled = true;
        var (client, _) = await _server.PairAsync("Dima iPad");
        using (client)
        {
            await client.SendAsync("""{"v":2,"type":"key","id":"ad88b2ad-d306-443a-8524-155575227708","action":"requestLanding","phase":"down","key":"n","mods":["alt"]}""");
            await client.ReceiveTypeAsync("ack");
            await client.SendAsync("""{"v":2,"type":"key","id":"bd88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"down","key":"l"}""");
            await client.ReceiveTypeAsync("ack");
        }

        var entries = new List<Csmfd.Core.Diagnostics.MonitorEntry>();
        while (monitor.TryDequeue(out var entry))
        {
            entries.Add(entry);
        }

        Assert.Equal(
            [
                "ad88b2ad Dima iPad: requestLanding  Alt+N  down",
                " Alt↓ N↓",
                "ad88b2ad sent",
                "bd88b2ad Dima iPad: lights  L  down",
                "bd88b2ad rejected (busy)",
            ],
            entries.Take(5).Select(e => $"{e.Ref} {e.Text}"));
    }

    [Fact]
    public async Task DuplicateCommandIdIsNotExecutedTwice()
    {
        var (client, _) = await _server.PairAsync();
        using (client)
        {
            const string hold = """{"v":2,"type":"key","id":"6d88b2ad-d306-443a-8524-155575227708","action":"masterMode","phase":"hold","key":"b","ms":50}""";
            await client.SendAsync(hold);
            Assert.Equal("sent", (await client.ReceiveTypeAsync("ack"))["status"]!.GetValue<string>());
            await Task.Delay(200);

            await client.SendAsync(hold);
            var duplicate = await client.ReceiveTypeAsync("ack");
            Assert.Equal("duplicate", duplicate["reason"]!.GetValue<string>());
            Assert.Equal(2, _server.Sink.Timeline.Count);
        }
    }

    [Fact]
    public async Task UnfocusedGameIsReportedOnTheCommand()
    {
        _server.Game.Focus = GameFocus.Background;
        var (client, _) = await _server.PairAsync();
        using (client)
        {
            await client.SendAsync("""{"v":2,"type":"key","id":"7d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"down","key":"l"}""");

            var ack = await client.ReceiveTypeAsync("ack");
            Assert.Equal("rejected", ack["status"]!.GetValue<string>());
            Assert.Equal("game_not_focused", ack["reason"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task ReconnectWithTokenReplacesOldClientAndReleasesItsInput()
    {
        var (first, token) = await _server.PairAsync();
        using (first)
        {
            await first.SendAsync("""{"v":2,"type":"key","id":"8d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"down","key":"l"}""");
            await first.ReceiveTypeAsync("ack");

            using var second = await _server.ConnectAsync();
            await second.SendAsync($$"""{"v":2,"type":"hello","client":"c","appVersion":"1","deviceName":"d","token":"{{token}}"}""");
            Assert.Equal("welcome", (await second.ReceiveAsync())["type"]!.GetValue<string>());
            var state = await second.ReceiveAsync();
            Assert.False(state["partial"]!.GetValue<bool>());

            Assert.Equal(CloseCodes.Replaced, await first.ReceiveCloseAsync());
            Assert.EndsWith("l^", _server.Sink.Timeline[^1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RevokedDeviceIsDisconnected()
    {
        var (client, token) = await _server.PairAsync();
        using (client)
        {
            var device = _server.Services.Pairing.Authenticate(token)!;
            _server.Services.Pairing.Revoke(device.Id);

            Assert.Equal(CloseCodes.Revoked, await client.ReceiveCloseAsync());
        }
    }

    [Fact]
    public async Task OversizedFragmentedMessageClosesWith1009()
    {
        var (client, _) = await _server.PairAsync();
        using (client)
        {
            var chunk = new byte[9 * 1024];
            Array.Fill(chunk, (byte)' ');
            await client.SendRawAsync(chunk, WebSocketMessageType.Text, endOfMessage: false);
            await client.SendRawAsync(chunk, WebSocketMessageType.Text, endOfMessage: true);

            Assert.Equal((int)WebSocketCloseStatus.MessageTooBig, await client.ReceiveCloseAsync());
        }
    }

    [Fact]
    public async Task BinaryFrameClosesWith1003()
    {
        var (client, _) = await _server.PairAsync();
        using (client)
        {
            await client.SendRawAsync([1, 2, 3], WebSocketMessageType.Binary, endOfMessage: true);

            Assert.Equal((int)WebSocketCloseStatus.InvalidMessageType, await client.ReceiveCloseAsync());
        }
    }

    [Fact]
    public async Task StateChangesArriveAsPartialDeltas()
    {
        var (client, _) = await _server.PairAsync();
        using (client)
        {
            _server.Services.Store.Update(s => s with { Game = new GameState(true, true, "12913470") });

            var delta = await client.ReceiveTypeAsync("state");
            Assert.True(delta["partial"]!.GetValue<bool>());
            Assert.Equal("12913470", delta["game"]!["build"]!.GetValue<string>());
            Assert.Null(delta["ship"]);
        }
    }
}

/// <summary>Heartbeat timing uses a short limit so the test stays fast.</summary>
public sealed class WssHeartbeatTests
{
    [Fact]
    public async Task MissingPingClosesWith4004AndReleasesInput()
    {
        await using var server = await WssTestServer.StartAsync(new SessionLimits
        {
            HeartbeatTimeout = TimeSpan.FromMilliseconds(600),
            WatchdogInterval = TimeSpan.FromMilliseconds(100),
        });
        var (client, _) = await server.PairAsync();
        using (client)
        {
            await client.SendAsync("""{"v":2,"type":"key","id":"9d88b2ad-d306-443a-8524-155575227708","action":"lights","phase":"down","key":"l"}""");
            await client.ReceiveTypeAsync("ack");

            Assert.Equal(CloseCodes.HeartbeatTimeout, await client.ReceiveCloseAsync());
            Assert.EndsWith("l^", server.Sink.Timeline[^1], StringComparison.Ordinal);
        }
    }
}
