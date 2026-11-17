using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using static DeviceBattery.NativeMethods;

namespace DeviceBattery;

/// <summary>
/// Renders one tray icon per device: a small device glyph in the top-left corner and the
/// battery percentage below/right of it, on a color-coded rounded background.
/// The GDI HICON behind the current icon is tracked and destroyed with DestroyIcon as soon as
/// it is replaced (or on Dispose) to avoid handle leaks. One IconGenerator per NotifyIcon.
/// </summary>
public sealed class IconGenerator : IDisposable
{
    private IntPtr _currentHandle = IntPtr.Zero;
    private IntPtr _pendingDestroy = IntPtr.Zero;

    public static Color ColorFor(int percent) =>
        percent > 50 ? Color.FromArgb(34, 160, 80) :
        percent >= 20 ? Color.FromArgb(240, 180, 20) :
        Color.FromArgb(214, 48, 64);

    /// <summary>Creates an icon (percent == null => neutral "no device" icon).</summary>
    public Icon Create(DeviceKind kind, int? percent, int? sizeOverride = null)
    {
        // Render at the shell's small-icon size so the icon is crisp at the current DPI.
        int size = Math.Clamp(sizeOverride ?? SystemInformation.SmallIconSize.Width, 16, 64);
        float s = size / 32f;

        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            Color bg = percent is null ? Color.FromArgb(105, 105, 115) : ColorFor(percent.Value);
            Color fg = percent is >= 20 and <= 50 ? Color.FromArgb(25, 25, 25) : Color.White;

            using (var path = RoundedRect(new RectangleF(0, 0, size, size), size * 0.2f))
            using (var brush = new SolidBrush(bg))
                g.FillPath(brush, path);

            // Corner glyph (top-left).
            DrawGlyph(g, kind, new RectangleF(3f * s, 2f * s, 14f * s, 9f * s), fg, bg, s);

            // Percentage text below the glyph, right aligned.
            string text = percent is null ? "–" : percent.Value.ToString();
            float fontPx = text.Length >= 3 ? 14f * s : 18f * s;
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
            fmt.Alignment = StringAlignment.Far;
            fmt.LineAlignment = StringAlignment.Far;
            fmt.FormatFlags |= StringFormatFlags.NoWrap;
            using var textBrush = new SolidBrush(fg);
            g.DrawString(text, font, textBrush, new RectangleF(0, 0, size - 1.5f * s, size - 1f * s), fmt);
        }

        IntPtr hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon); // does not own the handle; we destroy it ourselves

        _pendingDestroy = _currentHandle;
        _currentHandle = hIcon;
        return icon;
    }

    /// <summary>Call right after NotifyIcon.Icon was set to the icon returned by Create.</summary>
    public void ReleasePrevious()
    {
        if (_pendingDestroy != IntPtr.Zero)
        {
            DestroyIcon(_pendingDestroy);
            _pendingDestroy = IntPtr.Zero;
        }
    }

    // Solid (filled) glyphs with background-colored cut-outs: they stay readable at 16 px.
    private static void DrawGlyph(Graphics g, DeviceKind kind, RectangleF r, Color color, Color cutout, float s)
    {
        using var fill = new SolidBrush(color);
        using var hole = new SolidBrush(cutout);
        float px = Math.Max(1f, 1.5f * s); // ~1 device pixel at 16 px

        switch (kind)
        {
            case DeviceKind.Keyboard:
            {
                using var body = RoundedRect(new RectangleF(r.X, r.Y, r.Width, r.Height), 1.2f * s);
                g.FillPath(fill, body);
                // two rows of keys + a space bar
                for (int col = 0; col < 5; col++)
                    g.FillRectangle(hole, r.X + 1.6f * s + col * 2.5f * s, r.Y + 1.6f * s, px, px);
                for (int col = 0; col < 5; col++)
                    g.FillRectangle(hole, r.X + 1.6f * s + col * 2.5f * s, r.Y + 4.2f * s, px, px);
                g.FillRectangle(hole, r.X + 3.6f * s, r.Y + r.Height - 3f * s, r.Width - 7.2f * s, px);
                break;
            }
            case DeviceKind.Mouse:
            {
                float mw = 7f * s;
                var mr = new RectangleF(r.X + (r.Width - mw) / 2f, r.Y, mw, r.Height);
                using var body = RoundedRect(mr, mw / 2f);
                g.FillPath(fill, body);
                g.FillRectangle(hole, mr.X + mw / 2f - px / 2f, mr.Y + 0.6f * s, px, r.Height * 0.4f);
                break;
            }
            case DeviceKind.Headphones:
            {
                using var pen = new Pen(color, Math.Max(1f, 2f * s));
                float cupW = 3.6f * s;
                // headband
                g.DrawArc(pen, r.X + cupW / 2f, r.Y + 0.5f * s, r.Width - cupW, r.Height * 1.4f, 180, 180);
                // ear cups
                g.FillRectangle(fill, r.X, r.Y + r.Height * 0.45f, cupW, r.Height * 0.55f);
                g.FillRectangle(fill, r.Right - cupW, r.Y + r.Height * 0.45f, cupW, r.Height * 0.55f);
                break;
            }
            case DeviceKind.Controller:
            {
                using var body = RoundedRect(new RectangleF(r.X, r.Y + 1f * s, r.Width, r.Height - 1f * s), 3.5f * s);
                g.FillPath(fill, body);
                // d-pad cross (left) + two buttons (right)
                g.FillRectangle(hole, r.X + 2.6f * s, r.Y + 4f * s, 3f * s, px);
                g.FillRectangle(hole, r.X + 3.6f * s, r.Y + 3f * s, px, 3f * s);
                g.FillEllipse(hole, r.Right - 6f * s, r.Y + 3.6f * s, px * 1.2f, px * 1.2f);
                g.FillEllipse(hole, r.Right - 4f * s, r.Y + 4.8f * s, px * 1.2f, px * 1.2f);
                break;
            }
            default:
            {
                // Generic: battery.
                var br = new RectangleF(r.X + 1.5f * s, r.Y + 1f * s, r.Width - 4f * s, r.Height - 2f * s);
                g.FillRectangle(fill, br);
                g.FillRectangle(fill, br.Right, br.Y + br.Height * 0.3f, 1.8f * s, br.Height * 0.4f);
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
        ReleasePrevious();
        if (_currentHandle != IntPtr.Zero)
        {
            DestroyIcon(_currentHandle);
            _currentHandle = IntPtr.Zero;
        }
    }
}
