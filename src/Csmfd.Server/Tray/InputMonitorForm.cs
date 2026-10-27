using System.Globalization;
using Csmfd.Core.Diagnostics;

namespace Csmfd.Server.Tray;

/// <summary>
/// Debug window: what the iPad sends, how the server answers and what SendInput hands to Windows.
/// The monitor records only while this window is open; rows are drained in batches every 100 ms.
/// </summary>
internal sealed class InputMonitorForm : Form
{
    private const int MaxRows = 1000;

    private static readonly Color ReceivedColor = Color.FromArgb(0, 200, 255);
    private static readonly Color SentColor = Color.FromArgb(80, 220, 120);
    private static readonly Color RejectedColor = Color.FromArgb(255, 110, 110);
    private static readonly Color CancelledColor = Color.FromArgb(255, 170, 60);
    private static readonly Color InjectedColor = Color.FromArgb(160, 160, 160);

    private readonly InputMonitor _monitor;
    private readonly ListView _list;
    private readonly CheckBox _showInjected;
    private readonly CheckBox _pause;
    private readonly Label _footer;
    private readonly System.Windows.Forms.Timer _drain;

    public InputMonitorForm(InputMonitor monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));

        Text = "CSMFD — Input monitor";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(820, 480);
        MinimumSize = new Size(520, 260);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = Color.FromArgb(18, 22, 28),
            ForeColor = Color.Gainsboro,
            Font = new Font(FontFamily.GenericMonospace, 9.5f),
        };
        _list.Columns.Add("Time", 100);
        _list.Columns.Add("Type", 80);
        _list.Columns.Add("Ref", 80);
        _list.Columns.Add("Details", 520);

        _showInjected = new CheckBox { Text = "Show SendInput", Checked = true, AutoSize = true, Margin = new Padding(8, 6, 8, 6) };
        _pause = new CheckBox { Text = "Pause", AutoSize = true, Margin = new Padding(8, 6, 8, 6) };
        var topMost = new CheckBox { Text = "Always on top", AutoSize = true, Margin = new Padding(8, 6, 8, 6) };
        topMost.CheckedChanged += (_, _) => TopMost = topMost.Checked;
        var clear = new Button { Text = "Clear", AutoSize = true, Margin = new Padding(8, 3, 8, 3) };
        clear.Click += (_, _) => _list.Items.Clear();
        _footer = new Label { AutoSize = true, Margin = new Padding(16, 7, 8, 6), ForeColor = SystemColors.GrayText };

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        toolbar.Controls.AddRange([clear, _pause, _showInjected, topMost, _footer]);

        Controls.Add(_list);
        Controls.Add(toolbar);

        _drain = new System.Windows.Forms.Timer { Interval = 100 };
        _drain.Tick += (_, _) => Drain();
    }

    protected override void OnShown(EventArgs e)
    {
        _monitor.Enabled = true;
        _drain.Start();
        UpdateFooter();
        base.OnShown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Stop recording as soon as nobody looks: nothing is kept in memory afterwards.
        _monitor.Enabled = false;
        _drain.Stop();
        _drain.Dispose();
        base.OnFormClosed(e);
    }

    private void Drain()
    {
        if (_pause.Checked)
        {
            UpdateFooter();
            return;
        }

        var rows = new List<ListViewItem>();
        while (rows.Count < 500 && _monitor.TryDequeue(out var entry))
        {
            if (entry.Kind == MonitorEntryKind.Injected && !_showInjected.Checked)
            {
                continue;
            }

            rows.Add(ToRow(entry));
        }

        if (rows.Count > 0)
        {
            _list.BeginUpdate();
            _list.Items.AddRange([.. rows]);
            while (_list.Items.Count > MaxRows)
            {
                _list.Items.RemoveAt(0);
            }

            _list.EnsureVisible(_list.Items.Count - 1);
            _list.EndUpdate();
        }

        UpdateFooter();
    }

    private static ListViewItem ToRow(MonitorEntry entry)
    {
        var (type, color) = entry.Kind switch
        {
            MonitorEntryKind.Received => ("iPad →", ReceivedColor),
            MonitorEntryKind.Result when entry.Text.StartsWith("rejected", StringComparison.Ordinal) => ("result", RejectedColor),
            MonitorEntryKind.Result => ("result", SentColor),
            MonitorEntryKind.Cancelled => ("released", CancelledColor),
            _ => ("SendInput", InjectedColor),
        };

        var item = new ListViewItem(entry.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)) { ForeColor = color };
        item.SubItems.Add(type);
        item.SubItems.Add(entry.Ref);
        item.SubItems.Add(entry.Text);
        return item;
    }

    private void UpdateFooter()
    {
        var dropped = _monitor.Dropped;
        _footer.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{_list.Items.Count} rows{(dropped > 0 ? $", {dropped} dropped" : string.Empty)}{(_pause.Checked ? ", paused" : string.Empty)}");
    }
}
