using System.Diagnostics;
using System.Globalization;
using Csmfd.Protocol;
using Csmfd.Server.Hosting;

namespace Csmfd.Server.Tray;

/// <summary>
/// The only UI (spec 41.16): status, network choice, pairing, revoke, pause, firewall help,
/// data folder and quit. No network administration surface exists.
/// </summary>
internal sealed partial class TrayContext : ApplicationContext
{
    private readonly ServerApp _app;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly SynchronizationContext _ui;
    private readonly Icon _iconImage;
    private PairingForm? _pairing;
    private bool _paused;
    private bool _quitting;

    public TrayContext(ServerApp app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _menu = new ContextMenuStrip();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _iconImage = CreateIcon();
        _icon = new NotifyIcon { Icon = _iconImage, ContextMenuStrip = _menu, Visible = true, Text = "CSMFD Server" };
        _menu.Opening += (_, _) => RebuildMenu();
        _icon.DoubleClick += (_, _) => OpenPairing();

        _app.ListenerChanged += () => Post(UpdateTooltip);
        _app.Registry.ControllerChanged += _ => Post(UpdateTooltip);
        _app.Registry.InputBlockedNotified += () => Post(() => _icon.ShowBalloonTip(
            5000,
            "CSMFD input blocked",
            "Releasing a key failed. Check that no key is stuck, then choose \"Re-enable input\".",
            ToolTipIcon.Warning));

        // A tray app must never block on the default .NET exception dialog: log it and notify instead.
        _unhandledLogger = _app.LoggerFactory.CreateLogger("Unhandled");
        Application.ThreadException += OnThreadException;

        _ui.Post(async _ => await StartAsync().ConfigureAwait(true), null);
    }

    private readonly Microsoft.Extensions.Logging.ILogger _unhandledLogger;

    private void OnThreadException(object? sender, ThreadExceptionEventArgs e)
    {
        LogUnhandled(_unhandledLogger, e.Exception);
        _icon.ShowBalloonTip(8000, "CSMFD Server", "Unexpected error: " + e.Exception.Message + " (see logs)", ToolTipIcon.Error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _menu.Dispose();
            _iconImage.Dispose();
            _pairing?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var frame = new Pen(Color.FromArgb(0, 200, 255), 3);
            graphics.DrawRectangle(frame, 3, 3, 26, 26);
            using var font = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(graphics, "MFD", font, new Rectangle(0, 0, 32, 32), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        var handle = bitmap.GetHicon();
        using var temporary = Icon.FromHandle(handle);
        var icon = (Icon)temporary.Clone();
        Csmfd.Server.Win32.NativeMethods.DestroyIcon(handle);
        return icon;
    }

    private async Task StartAsync()
    {
        try
        {
            await _app.StartAsync().ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            _icon.ShowBalloonTip(8000, "CSMFD Server", "Start failed: " + ex.Message, ToolTipIcon.Error);
        }

        UpdateTooltip();
        if (_app.Config.BindAddress is null)
        {
            _icon.ShowBalloonTip(
                8000,
                "CSMFD Server",
                "Right-click the tray icon → Network to choose the Wi-Fi/Ethernet address the iPad should use.",
                ToolTipIcon.Info);
        }
    }

    private string StatusText()
    {
        var controller = _app.Registry.Controller;
        return _app.ListenerStatus switch
        {
            ListenerStatus.Listening when controller is not null => "Connected: " + controller.DeviceName,
            ListenerStatus.Listening => string.Create(CultureInfo.InvariantCulture, $"Listening on {_app.BoundAddress?.Address}:{ProtocolLimits.Port}"),
            ListenerStatus.Starting => "Starting…",
            ListenerStatus.AddressUnavailable => "Chosen address is not available",
            ListenerStatus.Failed => "Listener failed (port in use?)",
            _ => "Listener off — choose a network",
        };
    }

    private void UpdateTooltip()
    {
        var text = "CSMFD " + _app.Version + " — " + StatusText();
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem(StatusText()) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        var pair = new ToolStripMenuItem("Pair iPad…", null, (_, _) => OpenPairing())
        {
            Enabled = _app.ListenerStatus == ListenerStatus.Listening,
        };
        _menu.Items.Add(pair);
        _menu.Items.Add(BuildNetworkMenu());
        _menu.Items.Add(BuildDevicesMenu());
        _menu.Items.Add(new ToolStripSeparator());

        var pause = new ToolStripMenuItem("Pause input", null, (_, _) => TogglePause()) { Checked = _paused };
        _menu.Items.Add(pause);
        if (_app.Executor.IsBlocked)
        {
            _menu.Items.Add(new ToolStripMenuItem("Re-enable input", null, (_, _) => _app.Executor.ResetBlock()));
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Copy firewall command", null, (_, _) => CopyFirewallCommand()));
        _menu.Items.Add(new ToolStripMenuItem("Open data folder", null, (_, _) => OpenFolder(_app.Storage.Directory)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Quit", null, async (_, _) => await QuitAsync().ConfigureAwait(true)));
    }

    private ToolStripMenuItem BuildNetworkMenu()
    {
        var menu = new ToolStripMenuItem("Network");
        foreach (var address in NetworkAddresses.GetPrivateIPv4())
        {
            var label = $"{address.Address}  ({address.InterfaceName}){(address.LooksLikeVpn ? "  — VPN?" : string.Empty)}";
            var selected = string.Equals(_app.Config.BindAddress, address.Address.ToString(), StringComparison.Ordinal);
            var value = address.Address.ToString();
            menu.DropDownItems.Add(new ToolStripMenuItem(label, null, async (_, _) => await _app.SetBindAddressAsync(value).ConfigureAwait(true))
            {
                Checked = selected,
            });
        }

        if (menu.DropDownItems.Count == 0)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem("No private IPv4 network found") { Enabled = false });
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("Turn listener off", null, async (_, _) => await _app.SetBindAddressAsync(null).ConfigureAwait(true))
        {
            Checked = _app.Config.BindAddress is null,
        });
        return menu;
    }

    private ToolStripMenuItem BuildDevicesMenu()
    {
        var menu = new ToolStripMenuItem("Trusted devices");
        var devices = _app.Pairing.Devices;
        foreach (var device in devices)
        {
            var id = device.Id;
            var label = string.Create(CultureInfo.InvariantCulture, $"Revoke \"{device.Name}\" (paired {device.PairedAt.LocalDateTime:yyyy-MM-dd})");
            menu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => _app.Pairing.Revoke(id)));
        }

        if (devices.Count == 0)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem("No paired devices") { Enabled = false });
        }
        else
        {
            menu.DropDownItems.Add(new ToolStripSeparator());
            menu.DropDownItems.Add(new ToolStripMenuItem("Revoke all", null, (_, _) => _app.Pairing.RevokeAll()));
        }

        return menu;
    }

    private void OpenPairing()
    {
        if (_app.ListenerStatus != ListenerStatus.Listening || _app.BoundAddress is not { } address)
        {
            _icon.ShowBalloonTip(5000, "CSMFD Server", "Choose a network first (tray → Network).", ToolTipIcon.Info);
            return;
        }

        if (_pairing is { IsDisposed: false })
        {
            _pairing.Activate();
            return;
        }

        _pairing = new PairingForm(_app, address);
        _pairing.FormClosed += (_, _) =>
        {
            _pairing?.Dispose();
            _pairing = null;
        };
        _pairing.Show();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _app.Executor.SetPaused(_paused);
    }

    private void CopyFirewallCommand()
    {
        var program = Environment.ProcessPath ?? "CSMFD.Server.exe";
        var localIp = _app.BoundAddress?.Address.ToString() ?? "any";
        var command = string.Create(
            CultureInfo.InvariantCulture,
            $"netsh advfirewall firewall add rule name=\"CSMFD Server\" dir=in action=allow program=\"{program}\" protocol=TCP localport={ProtocolLimits.Port} localip={localIp} remoteip=localsubnet profile=private");
        Clipboard.SetText(command);
        _icon.ShowBalloonTip(6000, "CSMFD Server", "Firewall command copied. Run it once in an administrator terminal.", ToolTipIcon.Info);
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    private async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _icon.Visible = false;
        _pairing?.Close();
        await _app.DisposeAsync().ConfigureAwait(true);
        ExitThread();
    }

    private void Post(Action action) => _ui.Post(_ => action(), null);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Unhandled UI exception")]
    private static partial void LogUnhandled(Microsoft.Extensions.Logging.ILogger logger, Exception exception);
}
