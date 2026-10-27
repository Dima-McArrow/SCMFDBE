using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Csmfd.Core.Input;
using Csmfd.Core.Security;
using Csmfd.Core.State;
using Csmfd.Core.Transport;
using Csmfd.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Csmfd.Server.Hosting;

/// <summary>Everything a connection needs; shared by all connections of one server run.</summary>
internal sealed record ConnectionServices(
    TimeProvider Time,
    PairingService Pairing,
    ControllerRegistry Registry,
    InputExecutor Executor,
    StateStore Store,
    ServerIdentity Identity,
    ILoggerFactory LoggerFactory,
    SessionLimits? Limits = null,
    Csmfd.Core.Diagnostics.InputMonitor? Monitor = null,
    Csmfd.Core.GameLog.ActivityJournal? Activity = null);

/// <summary>
/// Minimal Kestrel host (spec 41.3, 41.4): one explicit IPv4 address, TLS 1.2+ only, the single
/// endpoint /csmfd, no compression, no browser origins, no static files, no admin surface.
/// </summary>
internal sealed partial class WssHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ILogger _logger;

    private WssHost(WebApplication app, ILogger logger)
    {
        _app = app;
        _logger = logger;
    }

    public static async Task<WssHost> StartAsync(
        IPAddress address,
        int port,
        X509Certificate2 certificate,
        ConnectionServices services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(services);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(services.LoggerFactory);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = 8;
            kestrel.Limits.MaxConcurrentUpgradedConnections = 3; // 1 controller + 2 unauthenticated
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.MaxRequestBodySize = 0;
            kestrel.Listen(address, port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = certificate;
                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    https.HandshakeTimeout = TimeSpan.FromSeconds(10);
                });
            });
        });

        var app = builder.Build();
        var logger = services.LoggerFactory.CreateLogger<WssHost>();
        var sessionLogger = services.LoggerFactory.CreateLogger<ConnectionSession>();

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Run(context => HandleAsync(context, services, sessionLogger));

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        LogListening(logger, address, port);
        return new WssHost(app, logger);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
            LogStopped(_logger);
        }
    }

    private static async Task HandleAsync(HttpContext context, ConnectionServices services, ILogger sessionLogger)
    {
        var request = context.Request;
        if (!string.Equals(request.Path.Value, ProtocolLimits.EndpointPath, StringComparison.Ordinal)
            || request.QueryString.HasValue
            || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // The native iPad client sends no Origin; any browser origin is refused (spec 41.4).
        if (request.Headers.Origin.Count > 0)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!services.Registry.TryEnterUnauthenticated())
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        System.Net.WebSockets.WebSocket socket;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                DangerousEnableCompression = false,
            }).ConfigureAwait(false);
        }
        catch
        {
            services.Registry.LeaveUnauthenticated();
            throw;
        }

        using (socket)
        {
            using var session = new ConnectionSession(
                socket,
                services.Time,
                services.Pairing,
                services.Registry,
                services.Executor,
                services.Store,
                services.Identity,
                sessionLogger,
                services.Limits,
                services.Monitor,
                services.Activity);
            await session.RunAsync(context.RequestAborted).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening on wss://{Address}:{Port}/csmfd")]
    private static partial void LogListening(ILogger logger, IPAddress address, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Listener stopped")]
    private static partial void LogStopped(ILogger logger);
}
