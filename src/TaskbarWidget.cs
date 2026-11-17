using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using static DeviceBattery.NativeMethods;

namespace DeviceBattery;

/// <summary>
/// A layered CHILD window of the taskbar (Shell_TrayWnd), docked just left of the notification area,
/// drawing "[glyph] 93%" for every device. Being a real child of the taskbar it shares the taskbar's
/// z-order, so flyouts (volume, clock, Start, hidden icons) can't hide it the way they hid a topmost popup.
/// No Form: a bare NativeWindow + UpdateLayeredWindow, redrawn only when data/theme/position changes.
/// Layered child windows require the exe manifest to declare Windows 8/10 support (app.manifest);
/// without it CreateWindowEx fails.
/// </summary>
public sealed class TaskbarWidget : NativeWindow, IDisposable
{
    private readonly ContextMenuStrip _menu;
    private IReadOnlyList<DeviceBatteryInfo> _devices = Array.Empty<DeviceBatteryInfo>();
    private int _width, _height;
    private bool _disposed;
    private readonly ToolTip _tip = new() { InitialDelay = 0, ReshowDelay = 0, AutoPopDelay = 15000 };
    // Explorer sometimes re-stacks the taskbar's own XAML window above us; check cheaply once a second
    // (only while the readout is shown) and re-raise within the taskbar's child windows if needed.
    private readonly System.Windows.Forms.Timer _keepOnTop = new() { Interval = 1000 };
    private bool _hoverArmed;
    private IntPtr _taskbar;
    private readonly System.Windows.Forms.Timer _menuTimer = new() { Interval = 80 };

    private void ShowMenu()
    {
        SetForegroundWindow(_owner.Handle); // needed so the menu closes when clicking elsewhere
        _menu.Show(Cursor.Position);
    }


    /// <summary>True while the readout is actually visible on the taskbar.</summary>
    public bool IsShown => _width > 0;

    private sealed class WinOwner(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private readonly OwnerWindow _owner;

    /// <summary>Hidden top-level window: foreground owner for the context menu and receiver of the
    /// "TaskbarCreated" broadcast (explorer.exe restarted => our child window was destroyed).</summary>
    private sealed class OwnerWindow : NativeWindow
    {
        private static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        private readonly Action _onTaskbarCreated;

        public OwnerWindow(Action onTaskbarCreated)
        {
            _onTaskbarCreated = onTaskbarCreated;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == TaskbarCreated) _onTaskbarCreated();
            base.WndProc(ref m);
        }
    }

    /// <summary>Creates the layered, no-activate child window inside the taskbar the readout is drawn into
    /// (re-created if explorer restarted and the old taskbar window is gone).</summary>
    private bool EnsureWindow(IntPtr taskbar)
    {
        if (Handle != IntPtr.Zero && IsWindow(Handle) && GetParent(Handle) == taskbar)
            return true;
        if (Handle != IntPtr.Zero)
            DestroyHandle();

        try
        {
            CreateHandle(new CreateParams
            {
                ClassName = "STATIC",
                Parent = taskbar,
                Style = WS_CHILD | WS_CLIPSIBLINGS,
                ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Error("create taskbar child window", ex); // taskbar refused it: caller falls back to the tray icon
            return false;
        }
    }

    public TaskbarWidget(ContextMenuStrip menu)
    {
        _menu = menu;
        _keepOnTop.Tick += (_, _) => KeepOnTop();
        _menuTimer.Tick += (_, _) => { _menuTimer.Stop(); ShowMenu(); };
        _owner = new OwnerWindow(() => Render()); // top-level helper: menu foreground + "TaskbarCreated"
    }

    public void Update(IReadOnlyList<DeviceBatteryInfo> devices)
    {
        _devices = devices;
        Render();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case 0x84: // WM_NCHITTEST: STATIC reports HTTRANSPARENT by default, so claim the hit ourselves
                m.Result = (IntPtr)1; // HTCLIENT
                return;
            case WM_MOUSEACTIVATE:
                m.Result = (IntPtr)3; // MA_NOACTIVATE
                return;
            case WM_MOUSEMOVE:
                if (!_hoverArmed)
                {
                    _hoverArmed = true;
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = TME_HOVER | TME_LEAVE,
                        hwndTrack = Handle,
                        dwHoverTime = 350
                    };
                    TrackMouseEvent(ref tme);
                }
                break;
            case WM_MOUSEHOVER:
                ShowDeviceTip();
                break;
            case WM_MOUSELEAVE:
                _hoverArmed = false;
                _tip.Hide(new WinOwner(Handle));
                break;
            case WM_LBUTTONDOWN:
                _tip.Hide(new WinOwner(Handle));
                break;
            case 0x202: // WM_LBUTTONUP: left click opens the same menu as right click, but slightly
                        // delayed: the taskbar activates itself on a left click, which would close a menu
                        // shown in the same instant.
                _tip.Hide(new WinOwner(Handle));
                _menuTimer.Stop();
                _menuTimer.Start();
                return;
            case WM_RBUTTONUP:
                _tip.Hide(new WinOwner(Handle));
                ShowMenu();
                return;
            case WM_SETTINGCHANGE: // theme / taskbar settings
            case WM_DISPLAYCHANGE: // resolution / DPI / taskbar moved
                Render();
                break;
        }
        base.WndProc(ref m);
    }

    /// <summary>If any sibling window (e.g. the taskbar's XAML bridge) is stacked above us, move to the top.</summary>
    private void KeepOnTop()
    {
        if (_disposed || _width <= 0 || Handle == IntPtr.Zero) return;
        if (GetWindow(Handle, 3 /* GW_HWNDPREV */) != IntPtr.Zero)
            SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, 0x1 | 0x2 | SWP_NOACTIVATE | SWP_SHOWWINDOW); // NOSIZE|NOMOVE
    }

    private void ShowDeviceTip()
    {
        if (_devices.Count == 0 || _menu.Visible) return; // a tooltip popping up would close the open menu
        string text = string.Join("\n", _devices.Select(d =>
            $"{d.DeviceName}: {d.BatteryPercentage}%  ({(d.ConnectionType == ConnectionType.Bluetooth ? "Bluetooth" : d.ConnectionType == ConnectionType.Usb ? "USB / 2.4 GHz" : "Other")})"));

        // Show above the taskbar: tooltip position is relative to our client area.
        var size = TextRenderer.MeasureText(text, SystemFonts.StatusFont ?? SystemFonts.DefaultFont);
        _tip.Show(text, new WinOwner(Handle), 0, -(size.Height + 22), 15000);
    }

    // ---------------------------------------------------------------- rendering

    private static bool TaskbarIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    private void Render()
    {
        if (_disposed) return;

        IntPtr taskbar = FindWindowW("Shell_TrayWnd", null);
        if (_devices.Count == 0 || taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out RECT tb))
        {
            _width = 0;
            _keepOnTop.Stop();
            if (Handle != IntPtr.Zero) ShowWindow(Handle, SW_HIDE);
            return;
        }

        float s = Math.Max(1f, GetDpiForWindow(taskbar) / 96f);
        int tbH = tb.Bottom - tb.Top;
        int tbW = tb.Right - tb.Left;
        if (tbH > tbW) // vertical taskbar: not supported
        {
            _width = 0;
            _keepOnTop.Stop();
            if (Handle != IntPtr.Zero) ShowWindow(Handle, SW_HIDE);
            return;
        }

        _taskbar = taskbar;
        if (!EnsureWindow(taskbar))
        {
            _width = 0;
            _keepOnTop.Stop();
            return;
        }

        Color fg = TaskbarIsLight() ? Color.FromArgb(28, 28, 28) : Color.White;
        Color low = Color.FromArgb(255, 99, 99);

        float glyph = 16f * s, gap = 5f * s, between = 16f * s, pad = 6f * s;
        using var font = new Font("Segoe UI", 13f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;

        // Measure
        using var measure = Graphics.FromHwnd(IntPtr.Zero);
        var texts = _devices.Select(d => $"{d.BatteryPercentage}%").ToList();
        var widths = texts.Select(t => measure.MeasureString(t, font, int.MaxValue, fmt).Width).ToList();
        float total = pad * 2 + widths.Sum() + _devices.Count * (glyph + gap) + (_devices.Count - 1) * between;

        int w = (int)Math.Ceiling(total), h = tbH;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            // Alpha 1 (not 0) keeps the whole strip hit-testable so right-click works anywhere on it.
            g.Clear(Color.FromArgb(1, 0, 0, 0));

            float x = pad;
            for (int i = 0; i < _devices.Count; i++)
            {
                var d = _devices[i];
                Color c = d.BatteryPercentage < 20 ? low : fg;

                DrawGlyph(g, d.Kind, new RectangleF(x, (h - glyph) / 2f, glyph, glyph), c, s);
                x += glyph + gap;

                using var brush = new SolidBrush(c);
                float th = font.GetHeight(g);
                g.DrawString(texts[i], font, brush, x, (h - th) / 2f, fmt);
                x += widths[i] + between;
            }
        }

        // Position: left of the notification area.
        int right;
        IntPtr notify = FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && GetWindowRect(notify, out RECT nr) && nr.Right > nr.Left && nr.Left > tb.Left)
            right = nr.Left;
        else
            right = tb.Right - (int)(280 * s);
        int left = Math.Max(tb.Left, right - w - (int)(4 * s));

        // Child window: position is relative to the taskbar's client area (left edge / top of the bar).
        Commit(bmp, left - tb.Left, 0, w, h);
        _width = w;
        _height = h;
        if (!_keepOnTop.Enabled) _keepOnTop.Start();
    }

    private void Commit(Bitmap bmp, int x, int y, int w, int h)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var dst = new POINT { X = x, Y = y };
            var size = new SIZE { CX = w, CY = h };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            SetWindowPos(Handle, HWND_TOP, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ---------------------------------------------------------------- outline glyphs (taskbar style)

    private static void DrawGlyph(Graphics g, DeviceKind kind, RectangleF r, Color color, float s)
    {
        using var pen = new Pen(color, Math.Max(1f, 1.3f * s)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new SolidBrush(color);

        switch (kind)
        {
            case DeviceKind.Headphones:
            {
                float cupW = 3.6f * s;
                g.DrawArc(pen, r.X + 1.2f * s, r.Y + 1f * s, r.Width - 2.4f * s, r.Height * 1.05f, 180, 180);
                using (var l = RoundedRect(new RectangleF(r.X + 0.6f * s, r.Y + r.Height * 0.52f, cupW, r.Height * 0.42f), 1.2f * s))
                    g.DrawPath(pen, l);
                using (var rr = RoundedRect(new RectangleF(r.Right - cupW - 0.6f * s, r.Y + r.Height * 0.52f, cupW, r.Height * 0.42f), 1.2f * s))
                    g.DrawPath(pen, rr);
                break;
            }
            case DeviceKind.Mouse:
            {
                float mw = r.Width * 0.62f;
                var mr = new RectangleF(r.X + (r.Width - mw) / 2f, r.Y + 0.5f * s, mw, r.Height - 1f * s);
                using var body = RoundedRect(mr, mw / 2f);
                g.DrawPath(pen, body);
                g.DrawLine(pen, mr.X + mw / 2f, mr.Y + 2f * s, mr.X + mw / 2f, mr.Y + r.Height * 0.36f);
                break;
            }
            case DeviceKind.Keyboard:
            {
                var kr = new RectangleF(r.X + 0.5f * s, r.Y + 3f * s, r.Width - 1f * s, r.Height - 6.5f * s);
                using var body = RoundedRect(kr, 1.8f * s);
                g.DrawPath(pen, body);
                float d = Math.Max(1f, 1.2f * s);
                for (int c = 0; c < 4; c++)
                    g.FillRectangle(fill, kr.X + 2.2f * s + c * 3f * s, kr.Y + 2.2f * s, d, d);
                g.FillRectangle(fill, kr.X + 3.6f * s, kr.Bottom - 3.4f * s, kr.Width - 7.2f * s, d);
                break;
            }
            case DeviceKind.Controller:
            {
                var cr = new RectangleF(r.X + 0.5f * s, r.Y + 3.5f * s, r.Width - 1f * s, r.Height - 7f * s);
                using var body = RoundedRect(cr, cr.Height / 2.2f);
                g.DrawPath(pen, body);
                float d = Math.Max(1f, 1.3f * s);
                g.FillRectangle(fill, cr.X + 3f * s, cr.Y + cr.Height / 2f - d / 2f, 3f * s, d);
                g.FillRectangle(fill, cr.X + 4f * s, cr.Y + cr.Height / 2f - 1.5f * s, d, 3f * s);
                g.FillEllipse(fill, cr.Right - 6f * s, cr.Y + cr.Height / 2f - 2f * s, d * 1.3f, d * 1.3f);
                g.FillEllipse(fill, cr.Right - 4f * s, cr.Y + cr.Height / 2f, d * 1.3f, d * 1.3f);
                break;
            }
            default:
            {
                var br = new RectangleF(r.X + 1f * s, r.Y + 3.5f * s, r.Width - 4f * s, r.Height - 7f * s);
                g.DrawRectangle(pen, br.X, br.Y, br.Width, br.Height);
                g.FillRectangle(fill, br.Right + 0.8f * s, br.Y + br.Height * 0.3f, 1.4f * s, br.Height * 0.4f);
                break;
            }
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _keepOnTop.Stop();
        _keepOnTop.Dispose();
        _menuTimer.Dispose();
        _tip.Dispose();
        _owner.DestroyHandle();
        DestroyHandle();
    }
}
