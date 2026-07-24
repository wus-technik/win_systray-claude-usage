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
    /// Filled-badge icon: translucent severity-tinted disc, solid pie wedge from 12 o'clock
    /// for usage (clamped 0–100), 1 px rim, centered digit with a dark halo so it reads on
    /// dark AND light taskbars. clockwise=true for the 5h window, false (counter-clockwise)
    /// for 7d. dimmed = stale data.
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

    /// <summary>Grey badge with a centered em-dash: the "no usage data yet" state.</summary>
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

            var rect = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);

            // Translucent tinted disc marks the unused fraction.
            using (var back = new SolidBrush(Color.FromArgb(dimmed ? 50 : 90, color)))
                g.FillEllipse(back, rect);

            // Solid usage wedge from 12 o'clock (-90°); counter-clockwise = negative sweep (7d).
            if (percent > 0)
            {
                float sweep = 360f * percent / 100f;
                if (!clockwise) sweep = -sweep;
                using var fill = new SolidBrush(color);
                g.FillPie(fill, rect.X, rect.Y, rect.Width, rect.Height, -90f, sweep);
            }

            using (var rim = new Pen(color, 1f))
                g.DrawEllipse(rim, rect);

            // Digit over a 1 px dark halo: readable on the filled and unfilled halves
            // and on light taskbars (the v0.1 ring left the center transparent).
            var textColor = dimmed ? Color.FromArgb(160, Color.White) : Color.White;
            using var font = new Font("Segoe UI", size * 0.60f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
                { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var halo = new SolidBrush(Color.FromArgb(dimmed ? 90 : 140, 0, 0, 0)))
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0)
                            g.DrawString(glyph, font, halo, new RectangleF(dx, dy, size, size), format);
            }
            using (var brush = new SolidBrush(textColor))
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
