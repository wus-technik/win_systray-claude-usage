using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Tray;

public static class IconRenderer
{
    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>System small-icon size (per-monitor DPI aware via PerMonitorV2), floor 16 px.</summary>
    public static int SystemTrayIconSize() => Math.Max(16, GetSystemMetrics(SM_CXSMICON));

    /// <summary>
    /// Progress-ring icon: arc from 12 o'clock, fill = percent (clamped 0–100), color = severity,
    /// faint same-hue track for the remainder, single centered digit for the window (5/7).
    /// clockwise=true for the 5h window, false (counter-clockwise) for 7d. dimmed = stale data.
    /// </summary>
    public static Icon Render(char digit, int percent, Severity severity, bool clockwise, bool dimmed, int size)
    {
        var color = severity switch
        {
            Severity.Red => Color.FromArgb(224, 68, 68),
            Severity.Orange => Color.FromArgb(232, 150, 40),
            _ => Color.FromArgb(64, 184, 96),
        };
        return Draw(digit.ToString(), Math.Clamp(percent, 0, 100), color, clockwise, dimmed, size);
    }

    /// <summary>Grey empty ring with a centered em-dash: the "no usage data yet" state.</summary>
    public static Icon RenderNeutral(int size)
        => Draw("—", percent: 0, Color.FromArgb(150, 150, 150), clockwise: true, dimmed: false, size);

    private static Icon Draw(string glyph, int percent, Color color, bool clockwise, bool dimmed, int size)
    {
        if (dimmed) color = Color.FromArgb(120, color);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float stroke = Math.Max(2f, size / 8f);
            var ringRect = new RectangleF(stroke / 2f, stroke / 2f, size - stroke, size - stroke);

            // Faint same-hue track for the unused remainder.
            using (var trackPen = new Pen(Color.FromArgb(dimmed ? 40 : 70, color), stroke))
                g.DrawEllipse(trackPen, ringRect);

            // Usage arc, from 12 o'clock (-90°). Counter-clockwise = negative sweep.
            if (percent > 0)
            {
                float sweep = 360f * percent / 100f;
                if (!clockwise) sweep = -sweep;
                using var arcPen = new Pen(color, stroke)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arcPen, ringRect, -90f, sweep);
            }

            // Single centered glyph. White reads on the (typically dark) taskbar.
            var textColor = dimmed ? Color.FromArgb(160, Color.White) : Color.White;
            using var font = new Font("Segoe UI", size * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(textColor);
            using var format = new StringFormat
                { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var native = Icon.FromHandle(hIcon);
            return (Icon)native.Clone(); // clone so the icon outlives the HICON we destroy
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
