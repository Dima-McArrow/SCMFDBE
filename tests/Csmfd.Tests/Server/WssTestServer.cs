using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Csmfd.Core.Input;
using Csmfd.Core.Security;
using Csmfd.Core.State;
using Csmfd.Core.Transport;
using Csmfd.Protocol;
using Csmfd.Server.Hosting;
using Csmfd.Server.Security;
using Csmfd.Tests.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csmfd.Tests.Server;

/// <summary>Real Kestrel/WSS on loopback with a real certificate; input goes to a fake sink.</summary>
internal sealed class WssTestServer : IAsyncDisposable
{
    private readonly string _directory;
    private readonly WssHost _host;

    private WssTestServer(string directory, WssHost host, X509Certificate2 certificate, int port, ConnectionServices services, FakeInputSink sink, FakeGameWindow game)
    {
        _directory = directory;
        _host = host;
        Certificate = certificate;
        Port = port;
        Services = services;
        Sink = sink;
        Game = game;
    }

    public X509Certificate2 Certificate { get; }

    public string Fingerprint => ServerCertificateStore.Fingerprint(Certificate);

    public int Port { get; }

    public ConnectionServices Services { get; }

    public FakeInputSink Sink { get; }

    public FakeGameWindow Game { get; }

    public Uri Endpoint => new($"wss://127.0.0.1:{Port}{ProtocolLimits.EndpointPath}");

    public static async Task<WssTestServer> StartAsync(SessionLimits? limits = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "csmfd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var time = TimeProvider.System;
        var certificate = ServerCertificateStore.LoadOrCreate(Path.Combine(directory, "cert.bin"), time);

        var sink = new FakeInputSink(time);
        var game = new FakeGameWindow();
        var registry = new ControllerRegistry();
        var executor = new InputExecutor(time, sink, game, new FakeKeyboard(), new EmoteAllowlist(["wave"]), registry);
        registry.Attach(executor);
        var pairing = new PairingService(time, new MemoryTrustStore());
        pairing.DeviceRevoked += registry.OnDeviceRevoked;

        var services = new ConnectionServices(
            time,
            pairing,
            registry,
            executor,
            new StateStore(),
            new ServerIdentity(Guid.NewGuid(), "CSMFD-Server", "0.1.0", ChatKey.Enter),
            NullLoggerFactory.Instance,
            limits);

        var port = FreePort();
        var host = await WssHost.StartAsync(IPAddress.Loopback, port, certificate, services, CancellationToken.None);
        return new WssTestServer(directory, host, certificate, port, services, sink, game);
    }

    public Task<TestClient> ConnectAsync(string? pinnedFingerprint = null, string? origin = null) =>
        TestClient.ConnectAsync(Endpoint, pinnedFingerprint ?? Fingerprint, origin);

    /// <summary>Full pairing handshake; returns the client after welcome + full state.</summary>
    public async Task<(TestClient Client, string Token)> PairAsync(string deviceName = "Test iPad")
    {
        var code = Services.Pairing.OpenWindow();
        var client = await ConnectAsync();
        await client.SendAsync($$"""{"v":2,"type":"hello","client":"test","appVersion":"1","deviceName":"{{deviceName}}","token":null}""");
        Assert.Equal("pairRequired", (await client.ReceiveAsync())["type"]!.GetValue<string>());
        await client.SendAsync($$"""{"v":2,"type":"pair","code":"{{code}}"}""");

        var paired = await client.ReceiveAsync();
        Assert.Equal("paired", paired["type"]!.GetValue<string>());
        Assert.Equal("welcome", (await client.ReceiveAsync())["type"]!.GetValue<string>());
        Assert.False((await client.ReceiveAsync())["partial"]!.GetValue<bool>());
        return (client, paired["token"]!.GetValue<string>());
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Services.Executor.Dispose();
        Certificate.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp directory.
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>Plays the iPad: pins the certificate fingerprint and exchanges JSON text frames.</summary>
internal sealed class TestClient : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ClientWebSocket _socket;

    private TestClient(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public WebSocketState State => _socket.State;

    public static async Task<TestClient> ConnectAsync(Uri endpoint, string pinnedFingerprint, string? origin)
    {
        var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null
            && string.Equals(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.GetRawCertData())),
                pinnedFingerprint,
                StringComparison.OrdinalIgnoreCase);
        if (origin is not null)
        {
            socket.Options.SetRequestHeader("Origin", origin);
        }

        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await socket.ConnectAsync(endpoint, timeout.Token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new TestClient(socket);
    }

    public Task SendAsync(string json) => SendRawAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true);

    public async Task SendRawAsync(byte[] payload, WebSocketMessageType type, bool endOfMessage)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        await _socket.SendAsync(payload, type, endOfMessage, timeout.Token);
    }

    public async Task<JsonNode> ReceiveAsync()
    {
        var (node, close) = await ReceiveAnyAsync();
        Assert.True(node is not null, $"Expected a message but the server closed with {close}");
        return node;
    }

    /// <summary>Skips messages until the server closes; returns the close code.</summary>
    public async Task<int> ReceiveCloseAsync()
    {
        while (true)
        {
            var (node, close) = await ReceiveAnyAsync();
            if (node is null)
            {
                return close!.Value;
            }
        }
    }

    /// <summary>Returns the next message of the given type, skipping others (e.g. state deltas).</summary>
    public async Task<JsonNode> ReceiveTypeAsync(string type)
    {
        while (true)
        {
            var message = await ReceiveAsync();
            if (message["type"]!.GetValue<string>() == type)
            {
                return message;
            }
        }
    }

    public void Dispose() => _socket.Dispose();

    private async Task<(JsonNode? Node, int? Close)> ReceiveAnyAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var buffer = new byte[64 * 1024];
        var count = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (null, (int?)result.CloseStatus ?? 0);
            }

            count += result.Count;
        }
        while (!result.EndOfMessage);

        return (JsonNode.Parse(buffer.AsSpan(0, count)), null);
    }
}
