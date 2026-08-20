using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Tray;

/// <summary>The usage bar drawing, shared by the popup rows and the settings dialog's preview.
/// One renderer rather than two, so the preview cannot drift from the bar it is previewing.</summary>
public static class UsageBar
{
    public const int DefaultWidth = 240;
    public const int DefaultHeight = 12;

    public static Color ColorFor(Severity severity) => severity switch
    {
        Severity.Red => Color.FromArgb(224, 68, 68),
        Severity.Orange => Color.FromArgb(232, 150, 40),
        _ => Color.FromArgb(64, 184, 96),
    };

    /// <summary>Custom-drawn because ProgressBar can't be recolored per-severity. elapsedFraction,
    /// when non-null, marks how far the clock has moved through the limit's reset period: fill short
    /// of the marker is burning slower than the clock, fill past it is on track to hit the cap
    /// early.</summary>
    public static void Paint(Graphics g, int width, int height, int percent, Severity severity,
        double? elapsedFraction = null)
    {
        g.FillRectangle(SystemBrushes.ControlLight, 0, 0, width, height);
        using var brush = new SolidBrush(ColorFor(severity));
        g.FillRectangle(brush, 0, 0, width * Math.Clamp(percent, 0, 100) / 100, height);

        if (elapsedFraction is { } fraction)
        {
            // A full-height band, not the inset notch this started as: the border overdraws the
            // outer row of anything touching an edge, so 3px ticks left just 4 black pixels, which
            // read as grey at 1:1 on a 96-DPI screen however dark the pen. Width is filled as a
            // rectangle rather than stroked with a wide pen because GDI+ centres pen strokes on the
            // coordinate, which would put a column outside the range checked below.
            const int markerWidth = 2;
            // Span markerWidth columns inside 1..width-2: the border drawn below owns x=0 and
            // x=width-1 and would otherwise swallow the marker at fraction 0.0 and 1.0.
            var x = 1 + (int)Math.Round((width - 2 - markerWidth) * Math.Clamp(fraction, 0, 1));
            using var markerBrush = new SolidBrush(SystemColors.ControlText);
            g.FillRectangle(markerBrush, x, 0, markerWidth, height);
        }
        g.DrawRectangle(SystemPens.ControlDark, 0, 0, width - 1, height - 1);
    }
}
