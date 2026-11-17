using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DeviceBattery;

public sealed class TrayApplicationContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "DeviceBattery";
    private const int LowThreshold = 15;
    private const int RearmThreshold = 20;

    private const string SummaryKey = "\u0001summary"; // tray icon used in taskbar-readout mode (fallback / balloons)
    private const string IdleKey = "\u0001idle";       // tray icon used in tray mode when there is nothing to show

    private readonly Dictionary<string, TrayEntry> _entries = new();
    private TaskbarWidget? _widget;
    private readonly ContextMenuStrip _menu;
    private readonly IBatteryProvider[] _providers = { new PnpBatteryProvider(), new HidBatteryProvider() };
    private readonly Dictionary<string, bool> _alerted = new();
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _intervalCts;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly System.Windows.Forms.Timer _debounce;
    private readonly MessageWindow _msgWindow;
    private IReadOnlyList<DeviceBatteryInfo> _devices = Array.Empty<DeviceBatteryInfo>();

    public TrayApplicationContext()
    {
        _menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true, // room for the check marks of the toggles
            BackColor = DarkMenuRenderer.Back,
            ForeColor = DarkMenuRenderer.Fore,
            Font = SystemFonts.MenuFont
        };
        // ContextMenuStrip starts "Opening" already cancelled while it has no items, so fill it once up front
        // and rebuild on every open so it always shows the current state (devices, autostart, ...).
        RebuildMenu();
        _menu.Opening += (_, e) =>
        {
            RebuildMenu();
            e.Cancel = false;
        };

        _widget = new TaskbarWidget(_menu);
        SyncDisplay(); // neutral icon until the first scan finishes

        // Immediate refresh on device connect/disconnect (debounced: PnP fires bursts of events).
        _debounce = new System.Windows.Forms.Timer { Interval = 2000 };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RefreshAsync(); };
        _msgWindow = new MessageWindow(
            onDeviceChange: () => { _debounce.Stop(); _debounce.Start(); },
            onQuit: ExitThread);

        _ = RunLoopAsync();
    }

    /// <summary>Thread-safe: asks the running instance to exit (used by "--quit" and the uninstaller).</summary>
    public void PostQuit() => NativeMethods.PostMessageW(_msgWindow.Handle, NativeMethods.WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);

    // ------------------------------------------------------------------ scanning

    private async Task RunLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await RefreshAsync();
            _intervalCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Settings.IntervalMinutes), _intervalCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (_cts.IsCancellationRequested) return; // exiting; otherwise the interval changed: rescan now
            }
        }
    }

    private async Task RefreshAsync()
    {
        if (!await _scanGate.WaitAsync(0)) return; // scan already running
        try
        {
            var all = await Task.Run(() =>
            {
                var list = new List<DeviceBatteryInfo>();
                foreach (var p in _providers)
                {
                    try { list.AddRange(p.GetBatteries()); } catch { /* isolate provider failures */ }
                }
                return list;
            });

            if (_cts.IsCancellationRequested) return;
            _devices = all;
            SyncDisplay();
            CheckLowBattery();
        }
        catch (Exception ex)
        {
            Log.Error("RefreshAsync", ex); // an unexpected failure must not silently end the refresh loop
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>Devices shown on the taskbar / in the tray: connected and not hidden by the user.</summary>
    private IReadOnlyList<DeviceBatteryInfo> VisibleDevices =>
        _devices.Where(d => d.IsConnected && !Settings.IsHidden(d.Id)).ToList();

    private static string ConnectionLabel(DeviceBatteryInfo d) => d.ConnectionType switch
    {
        ConnectionType.Bluetooth => "Bluetooth",
        ConnectionType.Usb => "2.4 GHz / USB",
        _ => "Other"
    };

    // ------------------------------------------------------------------ display (taskbar readout / tray icons)

    /// <summary>
    /// Taskbar mode: the readout shows every visible device; one small tray icon is only a fallback
    /// (shown if the readout can't be, and briefly for notifications).
    /// Tray mode: one tray icon per visible device.
    /// </summary>
    private void SyncDisplay()
    {
        var visible = VisibleDevices;

        if (Settings.TaskbarWidget)
        {
            _widget?.Update(visible);
            Prune(keep: new[] { SummaryKey });
            GetEntry(SummaryKey).Update(visible, hidden: _widget?.IsShown == true);
        }
        else
        {
            _widget?.Update(Array.Empty<DeviceBatteryInfo>());
            var keys = visible.Select(d => d.Id).ToList();
            if (keys.Count == 0) keys.Add(IdleKey);
            Prune(keep: keys);

            if (visible.Count == 0)
                GetEntry(IdleKey).Update(visible, hidden: false);
            foreach (var d in visible)
                GetEntry(d.Id).Update(new[] { d }, hidden: false);
        }
    }

    private TrayEntry GetEntry(string key)
    {
        if (!_entries.TryGetValue(key, out var e))
            _entries[key] = e = new TrayEntry(_menu);
        return e;
    }

    private void Prune(IEnumerable<string> keep)
    {
        var set = keep.ToHashSet();
        foreach (var gone in _entries.Keys.Where(k => !set.Contains(k)).ToList())
        {
            _entries[gone].Dispose();
            _entries.Remove(gone);
        }
    }

    private sealed class TrayEntry : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly IconGenerator _gen = new();
        private readonly System.Windows.Forms.Timer _balloonTimer = new() { Interval = 12000 };
        private bool _hidden;
        private bool _balloonActive;

        public TrayEntry(ContextMenuStrip menu)
        {
            _icon = new NotifyIcon { ContextMenuStrip = menu };
            // Left click opens the same menu as right click.
            _icon.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_icon, null);
            };
            _balloonTimer.Tick += (_, _) =>
            {
                _balloonTimer.Stop();
                _balloonActive = false;
                if (_hidden) _icon.Visible = false;
            };
        }

        public void Update(IReadOnlyList<DeviceBatteryInfo> devices, bool hidden)
        {
            _hidden = hidden;
            var lowest = devices.Count == 0 ? null : devices.MinBy(x => x.BatteryPercentage);
            _icon.Icon = _gen.Create(lowest?.Kind ?? DeviceKind.Other, lowest?.BatteryPercentage);
            _gen.ReleasePrevious(); // DestroyIcon on the previous HICON

            string tip = devices.Count == 0
                ? "No battery devices found"
                : string.Join(" | ", devices.Select(d => $"{Shorten(d.DeviceName, 22)}: {d.BatteryPercentage}%"));
            _icon.Text = tip.Length <= 127 ? tip : tip[..124] + "...";
            _icon.Visible = !_hidden || _balloonActive;
        }

        /// <summary>Balloons need a visible NotifyIcon: show it briefly, then hide it again if it is only a fallback.</summary>
        public void Notify(string title, string text)
        {
            _balloonActive = true;
            _icon.Visible = true;
            _icon.ShowBalloonTip(8000, title, text, ToolTipIcon.Warning);
            _balloonTimer.Stop();
            _balloonTimer.Start();
        }

        public void Hide() => _icon.Visible = false;

        public void Dispose()
        {
            _balloonTimer.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _gen.Dispose();
        }
    }

    // ------------------------------------------------------------------ low battery notification

    private void CheckLowBattery()
    {
        foreach (var d in VisibleDevices)
        {
            bool alerted = _alerted.GetValueOrDefault(d.Id);

            if (!alerted && d.BatteryPercentage < LowThreshold)
            {
                _alerted[d.Id] = true;
                if (!Settings.NotifyLow) continue;
                var entry = Settings.TaskbarWidget
                    ? _entries.GetValueOrDefault(SummaryKey)
                    : _entries.GetValueOrDefault(d.Id);
                entry?.Notify("Low battery", $"{d.DeviceName} is at {d.BatteryPercentage}%");
            }
            else if (alerted && d.BatteryPercentage > RearmThreshold)
            {
                _alerted[d.Id] = false; // re-arm only after it has charged above 20%
            }
        }
        // Alert state of devices that are temporarily gone is kept, so they don't re-notify on reconnect.
    }

    // ------------------------------------------------------------------ menu

    private void RebuildMenu()
    {
        var old = _menu.Items.Cast<ToolStripItem>().ToList();
        _menu.Items.Clear();
        foreach (var i in old) i.Dispose();

        // Devices: click to show / hide on the taskbar and in the tray.
        if (_devices.Count == 0)
        {
            _menu.Items.Add(Header("No devices with battery info found"));
        }
        else
        {
            _menu.Items.Add(Header("Click a device to show or hide it"));
            foreach (var d in _devices)
            {
                var device = d;
                bool hidden = Settings.IsHidden(device.Id);
                string status = device.IsConnected ? $"{device.BatteryPercentage}%" : "off / not connected";
                var item = new ToolStripMenuItem($"{device.DeviceName}  —  {status}   [{ConnectionLabel(device)}]")
                {
                    Checked = !hidden,
                    Tag = hidden || !device.IsConnected
                        ? (object)Color.FromArgb(140, 140, 150)
                        : IconGenerator.ColorFor(device.BatteryPercentage)
                };
                item.Click += (_, _) =>
                {
                    Settings.SetHidden(device.Id, !Settings.IsHidden(device.Id));
                    SyncDisplay();
                };
                _menu.Items.Add(item);
            }
        }

        _menu.Items.Add(new ToolStripSeparator());

        // Where the readout appears.
        var onTaskbar = new ToolStripMenuItem("Show on the taskbar (icon + percentage)") { Checked = Settings.TaskbarWidget };
        onTaskbar.Click += (_, _) => { Settings.TaskbarWidget = true; SyncDisplay(); };
        _menu.Items.Add(onTaskbar);

        var asTray = new ToolStripMenuItem("Show as tray icons (one per device)") { Checked = !Settings.TaskbarWidget };
        asTray.Click += (_, _) => { Settings.TaskbarWidget = false; SyncDisplay(); };
        _menu.Items.Add(asTray);

        _menu.Items.Add(new ToolStripSeparator());

        // Notifications and refresh.
        var notify = new ToolStripMenuItem($"Notify me when a battery is low ({LowThreshold}%)") { Checked = Settings.NotifyLow };
        notify.Click += (_, _) => Settings.NotifyLow = !Settings.NotifyLow;
        _menu.Items.Add(notify);

        var interval = new ToolStripMenuItem("Check batteries every");
        foreach (int minutes in Settings.IntervalChoices)
        {
            int m = minutes;
            var choice = new ToolStripMenuItem(m == 1 ? "1 minute" : $"{m} minutes") { Checked = Settings.IntervalMinutes == m };
            choice.Click += (_, _) => { Settings.IntervalMinutes = m; _intervalCts?.Cancel(); };
            interval.DropDownItems.Add(choice);
        }
        _menu.Items.Add(interval);

        var refresh = new ToolStripMenuItem("Refresh now");
        refresh.Click += (_, _) => _ = RefreshAsync();
        _menu.Items.Add(refresh);

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled() };
        startup.Click += (_, _) => SetStartup(!IsStartupEnabled());
        _menu.Items.Add(startup);

        _menu.Items.Add(new ToolStripSeparator());

        var about = new ToolStripMenuItem("About Device Battery");
        about.Click += (_, _) => MessageBox.Show(
            $"Device Battery {AppVersion}\n\nShows the battery level of your wireless keyboards, mice and headsets " +
            "on the taskbar.\n\nSupports Bluetooth devices that report a battery level to Windows and 2.4 GHz " +
            "dongle headsets listed in the README.", "About Device Battery", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _menu.Items.Add(about);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();
        _menu.Items.Add(exit);
    }

    private static string AppVersion =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0")
            .Split('+')[0];

    private static ToolStripMenuItem Header(string text) =>
        new(text) { Enabled = false, Tag = Color.FromArgb(150, 150, 160) };

    internal static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ------------------------------------------------------------------ Start with Windows (HKCU Run)

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) is string v &&
                   v.Contains(Environment.ProcessPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enable && Environment.ProcessPath is { } path)
                key.SetValue(RunValueName, $"\"{path}\"", RegistryValueKind.String);
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { /* registry blocked by policy: leave unchanged */ }
    }

    // ------------------------------------------------------------------ lifetime

    protected override void ExitThreadCore()
    {
        _cts.Cancel();
        _debounce.Stop();
        foreach (var e in _entries.Values) e.Hide();
        _widget?.Dispose();
        _widget = null;
        base.ExitThreadCore();
    }

    private bool _disposed;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true; // Application.Run disposes the context on exit and "using" does it again
            _cts.Cancel();
            _debounce.Dispose();
            _msgWindow.DestroyHandle();
            _widget?.Dispose();
            foreach (var e in _entries.Values) e.Dispose();
            _entries.Clear();
            _menu.Dispose();
            _intervalCts?.Dispose();
            _cts.Dispose();
            _scanGate.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- hidden window: WM_DEVICECHANGE listener + quit request (no Form) ----

    private sealed class MessageWindow : NativeWindow
    {
        private readonly Action _onDeviceChange;
        private readonly Action _onQuit;

        public MessageWindow(Action onDeviceChange, Action onQuit)
        {
            _onDeviceChange = onDeviceChange;
            _onQuit = onQuit;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_DEVICECHANGE) _onDeviceChange();
            else if (m.Msg == NativeMethods.WM_APP_QUIT) _onQuit();
            base.WndProc(ref m);
        }
    }

    // ---- dark theme ----

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public static readonly Color Back = Color.FromArgb(32, 32, 36);
        public static readonly Color Fore = Color.FromArgb(230, 230, 235);

        public DarkMenuRenderer() : base(new DarkColors()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Items can carry their own text color in Tag (battery level colors, dimmed headers).
            e.TextColor = e.Item.Tag is Color c ? c : Fore;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // Draw a simple check mark that is visible on the dark background.
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using var pen = new Pen(Fore, 2f);
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + 3, r.Top + r.Height / 2f),
                new PointF(r.Left + r.Width / 2.5f, r.Bottom - 4),
                new PointF(r.Right - 2, r.Top + 3)
            });
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Fore; // submenu arrow, visible on dark
            base.OnRenderArrow(e);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => DarkMenuRenderer.Back;
        public override Color MenuBorder => Color.FromArgb(70, 70, 78);
        public override Color MenuItemBorder => Color.FromArgb(70, 70, 78);
        public override Color MenuItemSelected => Color.FromArgb(58, 58, 66);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(58, 58, 66);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(58, 58, 66);
        public override Color ImageMarginGradientBegin => DarkMenuRenderer.Back;
        public override Color ImageMarginGradientMiddle => DarkMenuRenderer.Back;
        public override Color ImageMarginGradientEnd => DarkMenuRenderer.Back;
        public override Color SeparatorDark => Color.FromArgb(70, 70, 78);
        public override Color SeparatorLight => Color.FromArgb(70, 70, 78);
        public override Color CheckBackground => Color.FromArgb(58, 58, 66);
        public override Color CheckSelectedBackground => Color.FromArgb(58, 58, 66);
        public override Color CheckPressedBackground => Color.FromArgb(58, 58, 66);
    }
}
