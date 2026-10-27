using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Csmfd.Core.GameLog;
using Csmfd.Core.Input;
using Csmfd.Core.Security;
using Csmfd.Core.State;
using Csmfd.Core.Transport;
using Csmfd.Protocol;
using Csmfd.Server.Security;
using Csmfd.Server.Win32;
using Microsoft.Extensions.Logging;

namespace Csmfd.Server.Hosting;

/// <summary>Listener status shown in the tray.</summary>
internal enum ListenerStatus
{
    NotConfigured,
    AddressUnavailable,
    Starting,
    Listening,
    Failed,
}

/// <summary>
/// Composition root of the Windows BE (spec 41.3): one process in the user's session, one controller,
/// input executor, Game.log pipeline, game monitor and a WSS listener bound to the chosen private IPv4.
/// </summary>
internal sealed partial class ServerApp : IAsyncDisposable
{
    public const string ServerName = "CSMFD-Server";

    private readonly ILogger _logger;
    private readonly Lock _listenerGate = new();
    private readonly SemaphoreSlim _listenerSwitch = new(1, 1);
    private WssHost? _host;
    private MdnsAdvertiser? _mdns;
    private LocalAddress? _boundAddress;

    public ServerApp(ServerStorage storage)
    {
        Storage = storage ?? throw new ArgumentNullException(nameof(storage));
        Config = storage.LoadConfig();
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Information)
                .AddFilter("Microsoft", LogLevel.Warning) // hosting chatter includes full local paths (spec 41.16)
                .AddProvider(new FileLoggerProvider(storage.LogDirectory)));
        _logger = LoggerFactory.CreateLogger<ServerApp>();

        var time = TimeProvider.System;
        Store = new StateStore();
        Monitor = new Csmfd.Core.Diagnostics.InputMonitor(time);
        Registry = new ControllerRegistry { Monitor = Monitor };
        GameWindow = new Win32GameWindow(Config.GameExePath, LoggerFactory.CreateLogger<Win32GameWindow>());
        Executor = new InputExecutor(
            time,
            new Csmfd.Core.Diagnostics.ObservedInputSink(new Win32InputSink(), Monitor),
            GameWindow,
            new Win32PhysicalKeyboard(),
            LoadEmotes(storage, _logger),
            Registry,
            new InputExecutorOptions { ChatKey = Config.ChatKey });
        Registry.Attach(Executor);

        Pairing = new PairingService(time, new FileTrustStore(storage));
        Pairing.DeviceRevoked += Registry.OnDeviceRevoked;

        Tailer = new LogTailer(Config.GameLogPath);
        Processor = new GameLogProcessor(Store, Executor, time, Tailer.Generation, Tailer.ProbeLength);
        Registry.InputStartedNotified += Processor.OnInputStarted;

        GameMonitor = new GameMonitor(GameWindow, Store, Executor, time, GameMonitor.ReadBuild(Config.GameExePath));
        GameMonitor.GameExited += Processor.OnGameProcessExited;
        LogService = new GameLogService(Tailer, Processor, time, () => Store.Current.Game.Running);

        Certificate = ServerCertificateStore.LoadOrCreate(storage.CertificatePath, time);
        Fingerprint = ServerCertificateStore.Fingerprint(Certificate);
        Version = typeof(ServerApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        Identity = new ServerIdentity(Config.ServerId, ServerName, Version, Config.ChatKey);
        Services = new ConnectionServices(time, Pairing, Registry, Executor, Store, Identity, LoggerFactory, Monitor: Monitor);
    }

    public event Action? ListenerChanged;

    public ServerStorage Storage { get; }

    public ServerConfig Config { get; private set; }

    public ILoggerFactory LoggerFactory { get; }

    public StateStore Store { get; }

    public ControllerRegistry Registry { get; }

    public InputExecutor Executor { get; }

    /// <summary>Local debug view of received commands and injected input (tray \"Input monitor\").</summary>
    public Csmfd.Core.Diagnostics.InputMonitor Monitor { get; }

    public PairingService Pairing { get; }

    public X509Certificate2 Certificate { get; }

    public string Fingerprint { get; }

    public string Version { get; }

    public ListenerStatus ListenerStatus { get; private set; } = ListenerStatus.NotConfigured;

    public LocalAddress? BoundAddress
    {
        get
        {
            lock (_listenerGate)
            {
                return _boundAddress;
            }
        }
    }

    private Win32GameWindow GameWindow { get; }

    private ServerIdentity Identity { get; }

    private ConnectionServices Services { get; }

    private LogTailer Tailer { get; }

    private GameLogProcessor Processor { get; }

    private GameMonitor GameMonitor { get; }

    private GameLogService LogService { get; }

    /// <summary>Must run on the UI thread (the foreground hook needs its message loop).</summary>
    public async Task StartAsync()
    {
        LogStarting(_logger, Version);
        GameMonitor.Start();
        LogService.Start(Config.GameLogPath);
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        await ApplyListenerAsync().ConfigureAwait(true);
    }

    /// <summary>Saves the user's choice and restarts the listener on it.</summary>
    public async Task SetBindAddressAsync(string? address)
    {
        Config = Config with { BindAddress = address };
        Storage.SaveConfig(Config);
        await ApplyListenerAsync().ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        Executor.CancelAll(CancelReason.Shutdown);
        await StopListenerAsync().ConfigureAwait(false);
        LogService.Dispose();
        GameMonitor.Dispose();
        GameWindow.Dispose();
        Processor.Dispose();
        Executor.Dispose();
        Certificate.Dispose();
        _listenerSwitch.Dispose();
        LogStopped(_logger);
        LoggerFactory.Dispose();
    }

    private static EmoteAllowlist LoadEmotes(ServerStorage storage, ILogger logger)
    {
        if (!File.Exists(storage.EmotesPath))
        {
            storage.WriteAtomically(storage.EmotesPath, DefaultEmotes);
        }

        var emotes = EmoteAllowlist.Parse(File.ReadAllText(storage.EmotesPath));
        if (emotes.Skipped.Count > 0)
        {
            // A skipped line means the iPad's emote is rejected as "invalid"; make that visible.
            LogEmotesSkipped(logger, emotes.Skipped.Count, string.Join(", ", emotes.Skipped));
        }

        return emotes;
    }

    private async void OnNetworkChanged(object? sender, EventArgs e)
    {
        try
        {
            await ApplyListenerAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            LogListenerFailed(_logger, ex.Message);
        }
    }

    /// <summary>Starts, keeps or stops the listener so it matches the configured, currently present address.</summary>
    private async Task ApplyListenerAsync()
    {
        await _listenerSwitch.WaitAsync().ConfigureAwait(false);
        try
        {
            var wanted = NetworkAddresses.Find(Config.BindAddress);
            var current = BoundAddress;
            if (wanted is not null && current is not null && wanted.Address.Equals(current.Address) && _host is not null)
            {
                return;
            }

            await StopListenerAsync().ConfigureAwait(false);

            if (Config.BindAddress is null)
            {
                SetStatus(ListenerStatus.NotConfigured);
                return;
            }

            if (wanted is null)
            {
                // Interface/profile changed: stay closed until the chosen address is back (spec 41.4).
                SetStatus(ListenerStatus.AddressUnavailable);
                return;
            }

            SetStatus(ListenerStatus.Starting);
            try
            {
                var host = await WssHost.StartAsync(wanted.Address, ProtocolLimits.Port, Certificate, Services, CancellationToken.None).ConfigureAwait(false);
                lock (_listenerGate)
                {
                    _host = host;
                    _boundAddress = wanted;
                }

                _mdns = MdnsAdvertiser.TryStart(Config.DisplayName, Config.ServerId, wanted.Address, wanted.InterfaceIndex, ProtocolLimits.Port, LoggerFactory.CreateLogger<MdnsAdvertiser>());
                SetStatus(ListenerStatus.Listening);
            }
            catch (IOException ex)
            {
                LogListenerFailed(_logger, ex.Message);
                SetStatus(ListenerStatus.Failed);
            }
        }
        finally
        {
            _listenerSwitch.Release();
        }
    }

    private async Task StopListenerAsync()
    {
        WssHost? host;
        lock (_listenerGate)
        {
            host = _host;
            _host = null;
            _boundAddress = null;
        }

        _mdns?.Dispose();
        _mdns = null;
        if (host is not null)
        {
            // Closing connections releases their input through the sessions; this is a second guard.
            Executor.CancelAll(CancelReason.Disconnected);
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SetStatus(ListenerStatus status)
    {
        ListenerStatus = status;
        ListenerChanged?.Invoke();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "CSMFD server {Version} starting")]
    private static partial void LogStarting(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "CSMFD server stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listener failed: {Reason}")]
    private static partial void LogListenerFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "emotes.txt: {Count} invalid entries skipped: {Names}")]
    private static partial void LogEmotesSkipped(ILogger logger, int count, string names);

    private const string DefaultEmotes = """
        # Chat emotes the iPad may send, one base name per line ("/name" and "/name<N>" are accepted).
        # Keep this list in sync with Emote.swift on the iPad; anything else is rejected as "invalid".
        wave
        dance
        gasp
        """;
}
