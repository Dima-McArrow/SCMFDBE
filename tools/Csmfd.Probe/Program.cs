using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Csmfd.Protocol;

// csmfd-probe: plays the iPad against a running CSMFD server (development only).
//   --host <ip> [--port 47823] --fp <64 hex>            pinned TLS (required for anything but --print-fp)
//   --print-fp                                         connect once, print the server fingerprint, abort
//   --code <6 digits> | --token <token>                pair, or authenticate with a stored token
//   --action <name> [--hold-ms <n>]                    send one allowlisted command (down+up or hold)
var options = ParseArgs(args);
var host = Require(options, "host");
var port = options.TryGetValue("port", out var portText) ? int.Parse(portText, CultureInfo.InvariantCulture) : ProtocolLimits.Port;
var endpoint = new Uri($"wss://{host}:{port}{ProtocolLimits.EndpointPath}");

if (options.ContainsKey("print-fp"))
{
    string? seen = null;
    using var probe = new ClientWebSocket();
    probe.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
    {
        seen = certificate is null ? null : Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        return false; // never proceed without a pin
    };

    try
    {
        await probe.ConnectAsync(endpoint, CancellationToken.None);
    }
    catch (WebSocketException)
    {
        // Expected: the callback refuses the connection.
    }

    Console.WriteLine(seen ?? "no certificate received");
    return seen is null ? 1 : 0;
}

var pin = Require(options, "fp");
using var socket = new ClientWebSocket();
socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
    certificate is not null
    && string.Equals(Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())), pin, StringComparison.OrdinalIgnoreCase);

var clock = Stopwatch.StartNew();
await socket.ConnectAsync(endpoint, CancellationToken.None);
Log($"connected, TLS pinned ({clock.ElapsedMilliseconds} ms)");

var token = options.GetValueOrDefault("token");
await Send(socket, $$"""{"v":2,"type":"hello","client":"csmfd-probe","appVersion":"0.1.0","deviceName":"Probe","token":{{(token is null ? "null" : $"\"{token}\"")}}}""");

if (token is null)
{
    var reply = await Receive(socket);
    if (reply?["type"]?.GetValue<string>() != "pairRequired")
    {
        return 2;
    }

    await Send(socket, $$"""{"v":2,"type":"pair","code":"{{Require(options, "code")}}"}""");
    var paired = await Receive(socket);
    if (paired?["type"]?.GetValue<string>() != "paired")
    {
        return 3;
    }

    Log("token (store it like a password): " + paired["token"]!.GetValue<string>());
}

var welcome = await Receive(socket);
if (welcome?["type"]?.GetValue<string>() != "welcome")
{
    return 4;
}

var state = await Receive(socket);
if (state?["partial"]?.GetValue<bool>() != false)
{
    return 5;
}

clock.Restart();
await Send(socket, $$"""{"v":2,"type":"ping","id":"probe-1","t":{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}""");
await ReceiveType(socket, "pong");
Log($"ping RTT {clock.Elapsed.TotalMilliseconds:F1} ms");

if (options.TryGetValue("action", out var action))
{
    if (!ActionCatalog.TryGet(action, out var spec))
    {
        Log("unknown action");
        return 6;
    }

    var mods = string.Join(',', ModNames(spec.Mods).Select(m => $"\"{m}\""));
    if (spec.Kind == ActionKind.Hold)
    {
        var ms = options.TryGetValue("hold-ms", out var holdText) ? int.Parse(holdText, CultureInfo.InvariantCulture) : spec.DefaultHoldMs!.Value;
        await Send(socket, $$"""{"v":2,"type":"key","id":"{{Guid.NewGuid()}}","action":"{{spec.Action}}","phase":"hold","key":"{{spec.Key}}","mods":[{{mods}}],"ms":{{ms}}}""");
        await ReceiveType(socket, "ack");
    }
    else
    {
        clock.Restart();
        await Send(socket, $$"""{"v":2,"type":"key","id":"{{Guid.NewGuid()}}","action":"{{spec.Action}}","phase":"down","key":"{{spec.Key}}","mods":[{{mods}}]}""");
        await ReceiveType(socket, "ack");
        Log($"down ack after {clock.Elapsed.TotalMilliseconds:F1} ms");
        await Send(socket, $$"""{"v":2,"type":"key","id":"{{Guid.NewGuid()}}","action":"{{spec.Action}}","phase":"up","key":"{{spec.Key}}","mods":[{{mods}}]}""");
        await ReceiveType(socket, "ack");
    }
}

if (options.TryGetValue("stay", out var stayText))
{
    // Behave like an idle iPad: one ping every 5 s, no commands.
    var until = DateTime.UtcNow.AddSeconds(int.Parse(stayText, CultureInfo.InvariantCulture));
    var pings = 0;
    while (DateTime.UtcNow < until)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        await socket.SendAsync(Encoding.UTF8.GetBytes($$"""{"v":2,"type":"ping","id":"stay-{{pings}}","t":{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        if (await ReceiveType(socket, "pong") is null)
        {
            return 7;
        }

        pings++;
    }

    Log($"stayed connected, {pings} pings answered");
}

await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe done", CancellationToken.None);
Log("closed");
return 0;

static IEnumerable<string> ModNames(Modifiers mods)
{
    if (mods.HasFlag(Modifiers.Ctrl))
    {
        yield return "ctrl";
    }

    if (mods.HasFlag(Modifiers.Shift))
    {
        yield return "shift";
    }

    if (mods.HasFlag(Modifiers.Alt))
    {
        yield return "alt";
    }

    if (mods.HasFlag(Modifiers.RAlt))
    {
        yield return "ralt";
    }
}

static async Task Send(ClientWebSocket socket, string json)
{
    Log("→ " + json);
    await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
}

static async Task<JsonNode?> Receive(ClientWebSocket socket)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var buffer = new byte[ProtocolLimits.MaxMessageBytes];
    var count = 0;
    ValueWebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(buffer.AsMemory(count), timeout.Token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            Log($"← close {(int?)socket.CloseStatus} {socket.CloseStatusDescription}");
            return null;
        }

        count += result.Count;
    }
    while (!result.EndOfMessage);

    var text = Encoding.UTF8.GetString(buffer, 0, count);
    Log("← " + text);
    return JsonNode.Parse(text);
}

static async Task<JsonNode?> ReceiveType(ClientWebSocket socket, string type)
{
    while (true)
    {
        var message = await Receive(socket);
        if (message is null || message["type"]?.GetValue<string>() == type)
        {
            return message;
        }
    }
}

static void Log(string line) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");

static string Require(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required");

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var name = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        result[name] = hasValue ? args[++i] : "true";
    }

    return result;
}
