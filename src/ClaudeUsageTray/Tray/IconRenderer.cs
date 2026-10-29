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
    /// for 7d. dimmed = stale data. warning = the Claude platform is degraded: a white
    /// exclamation badge in the bottom-right corner, drawn over everything and never dimmed.
    /// </summary>
    public static Icon Render(char digit, int percent, Severity severity, bool clockwise,
        bool dimmed, int size, bool warning = false, BadgeShape shape = BadgeShape.Disc)
    {
        var color = severity switch
        {
            Severity.Red => Color.FromArgb(224, 68, 68),
            Severity.Orange => Color.FromArgb(232, 150, 40),
            _ => Color.FromArgb(64, 184, 96),
        };
        return Draw(digit.ToString(), Math.Clamp(percent, 0, 100), color, clockwise, dimmed, size, warning, shape);
    }

    /// <summary>Grey badge with a centered em-dash: the "no usage data yet" state. The warning
    /// badge applies here too — an outage with no usage data must still be visible.</summary>
    public static Icon RenderNeutral(int size, bool warning = false, BadgeShape shape = BadgeShape.Disc)
        => Draw("—", percent: 0, Color.FromArgb(150, 150, 150), clockwise: true, dimmed: false, size, warning, shape);

    private static Icon Draw(string glyph, int percent, Color color, bool clockwise, bool dimmed,
        int size, bool warning, BadgeShape shape)
    {
        if (dimmed) color = Color.FromArgb(120, color);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var rect = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
            using var path = ShapePath(rect, shape);

            // Translucent tinted disc marks the unused fraction.
            using (var back = new SolidBrush(Color.FromArgb(dimmed ? 50 : 90, color)))
                g.FillPath(back, path);

            // Solid usage wedge from 12 o'clock (-90°); counter-clockwise = negative sweep (7d).
            if (percent > 0)
            {
                float sweep = 360f * percent / 100f;
                if (!clockwise) sweep = -sweep;
                // Keep the wedge inside the silhouette. Save/Restore avoids retaining the Region
                // allocated by reading Graphics.Clip on every tray render.
                var state = g.Save();
                g.SetClip(path);
                using (var fill = new SolidBrush(color))
                    g.FillPie(fill, rect.X, rect.Y, rect.Width, rect.Height, -90f, sweep);
                g.Restore(state);
            }

            using (var rim = new Pen(color, 1f))
                g.DrawPath(rim, path);

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

            if (warning) DrawWarningBadge(g, size);
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

    /// <summary>The corner radius scales with the icon so 16 px and 32 px badges read as the same
    /// shape; below 2 px the rounding would be indistinguishable from a square.</summary>
    private static GraphicsPath ShapePath(RectangleF rect, BadgeShape shape)
    {
        var path = new GraphicsPath();
        if (shape == BadgeShape.Disc)
        {
            path.AddEllipse(rect);
            return path;
        }

        float r = Math.Max(2f, rect.Width * 0.22f);
        float d = r * 2f;
        path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    /// <summary>White disc with a dark rim and a shape-drawn exclamation, inscribed in the
    /// bottom-right corner — the conventional notification position, away from the centred
    /// digit. Drawn last so it rides over the rim; white fill plus dark rim reads on dark AND
    /// light taskbars, the same dual-legibility rule the digit halo was built for. Never
    /// dimmed: dimming encodes stale *usage* data, and the service state is fresh from its own
    /// fetch — a real outage must not fade. The exclamation is shapes, not text: a 5–6 px font
    /// would sit below the legibility floor this project already enforces for the digit, and a
    /// shape-drawn "!" is the only form guaranteed to read at 16 px.</summary>
    private static void DrawWarningBadge(Graphics g, int size)
    {
        float d = size * 0.45f;
        float cx = size - d / 2f;
        float cy = size - d / 2f;

        using (var fill = new SolidBrush(Color.White))
            g.FillEllipse(fill, cx - d / 2f, cy - d / 2f, d, d);
        using (var rim = new Pen(Color.FromArgb(30, 30, 30), 1f))
            g.DrawEllipse(rim, cx - d / 2f, cy - d / 2f, d, d);

        using var mark = new SolidBrush(Color.FromArgb(30, 30, 30));
        float w = d * 0.22f;
        g.FillRectangle(mark, cx - w / 2f, cy - d * 0.26f, w, d * 0.40f); // stem
        g.FillRectangle(mark, cx - w / 2f, cy + d * 0.24f, w, w);          // dot
    }
}
