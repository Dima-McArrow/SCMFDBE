using System.Globalization;
using Csmfd.Core.Security;
using Csmfd.Protocol;
using Csmfd.Server.Hosting;
using Csmfd.Server.Security;
using QRCoder;

namespace Csmfd.Server.Tray;

/// <summary>
/// Local pairing window (spec 41.5): opening it starts the 120 s pairing window; closing it ends it.
/// Shows address, server id, certificate fingerprint and the one-time code, plus a QR code with
/// csmfd://pair?v=2&amp;host=&lt;ip&gt;&amp;port=47823&amp;id=&lt;serverId&gt;&amp;fp=&lt;sha256 hex&gt; (no code inside).
/// </summary>
internal sealed class PairingForm : Form
{
    private readonly ServerApp _app;
    private readonly int _devicesAtStart;
    private readonly long _openedAt = Environment.TickCount64;
    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _ticker;
    private bool _finished;

    public PairingForm(ServerApp app, LocalAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _devicesAtStart = app.Pairing.Devices.Count;
        var code = app.Pairing.OpenWindow();

        Text = "CSMFD — Pair iPad";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"csmfd://pair?v=2&host={address.Address}&port={ProtocolLimits.Port}&id={app.Config.ServerId:D}&fp={app.Fingerprint}");
        var qr = new PictureBox { Image = RenderQr(payload), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, 16, 0) };
        layout.Controls.Add(qr, 0, 0);
        layout.SetRowSpan(qr, 2);

        var details = new Label
        {
            AutoSize = true,
            Font = new Font(FontFamily.GenericMonospace, 10f),
            Text = string.Join(
                Environment.NewLine,
                (string[])
                [
                $"Address      wss://{address.Address}:{ProtocolLimits.Port}{ProtocolLimits.EndpointPath}",
                $"Server ID    {app.Config.ServerId:D}",
                "Fingerprint  (SHA-256 of the TLS certificate)",
                .. WrapFingerprint(ServerCertificateStore.FormatFingerprint(app.Fingerprint)),
                string.Empty,
                "Scan the QR code on the iPad (or compare the fingerprint),",
                "then enter this one-time code:",
                ]),
        };
        layout.Controls.Add(details, 1, 0);

        var codeLabel = new Label
        {
            AutoSize = true,
            Font = new Font(FontFamily.GenericMonospace, 32f, FontStyle.Bold),
            Text = code[..3] + " " + code[3..],
        };
        layout.Controls.Add(codeLabel, 1, 1);

        _status = new Label { AutoSize = true, Font = new Font(Font.FontFamily, 10f) };
        layout.Controls.Add(_status, 1, 2);
        Controls.Add(layout);

        _ticker = new System.Windows.Forms.Timer { Interval = 500 };
        _ticker.Tick += (_, _) => UpdateStatus();
        _ticker.Start();
        UpdateStatus();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ticker.Stop();
        _ticker.Dispose();
        _app.Pairing.CloseWindow();
        base.OnFormClosed(e);
    }

    private static IEnumerable<string> WrapFingerprint(string grouped)
    {
        // 16 groups of 4: two lines of 8 groups.
        var groups = grouped.Split(' ');
        yield return "  " + string.Join(' ', groups.Take(8));
        yield return "  " + string.Join(' ', groups.Skip(8));
    }

    private static Bitmap RenderQr(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        const int moduleSize = 5;
        var modules = data.ModuleMatrix; // includes the quiet zone
        var size = modules.Count * moduleSize;
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        for (var y = 0; y < modules.Count; y++)
        {
            for (var x = 0; x < modules[y].Count; x++)
            {
                if (modules[y][x])
                {
                    graphics.FillRectangle(Brushes.Black, x * moduleSize, y * moduleSize, moduleSize, moduleSize);
                }
            }
        }

        return bitmap;
    }

    private void UpdateStatus()
    {
        if (_finished)
        {
            return;
        }

        if (_app.Pairing.Devices.Count > _devicesAtStart)
        {
            _finished = true;
            _status.Text = "Paired: " + _app.Pairing.Devices[^1].Name + ". This window closes in 2 s.";
            var close = new System.Windows.Forms.Timer { Interval = 2000 };
            close.Tick += (_, _) =>
            {
                close.Dispose();
                Close();
            };
            close.Start();
            return;
        }

        if (!_app.Pairing.IsWindowOpen)
        {
            _finished = true;
            _status.Text = "Pairing closed (timeout or too many wrong codes). Open it again from the tray.";
            return;
        }

        var remaining = PairingService.WindowDuration - TimeSpan.FromMilliseconds(Environment.TickCount64 - _openedAt);
        _status.Text = string.Create(CultureInfo.InvariantCulture, $"Pairing open for {Math.Max(0, (int)remaining.TotalSeconds)} s");
    }
}
