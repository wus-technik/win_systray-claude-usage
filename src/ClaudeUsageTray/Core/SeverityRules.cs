namespace ClaudeUsageTray.Core;

public enum Severity { Green, Orange, Red }

public static class SeverityRules
{
    /// <summary>Below this share of the period elapsed, the ratio is noise: one minute into a
    /// five-hour window, 2% used is already 1.7x pace and means nothing.</summary>
    private const double DeadZone = 0.10;

    /// <summary>Below this percent used, pace never escalates. A steep ratio over a trivial
    /// absolute number is not a warning worth a colour.</summary>
    private const int Floor = 20;

    private const double OrangeRatio = 1.10;
    private const double RedRatio = 1.75;

    /// <summary>&lt; orangeAt → Green, orangeAt..redAbove → Orange, &gt; redAbove → Red.</summary>
    public static Severity For(int percent, int orangeAt = 50, int redAbove = 85)
        => percent > redAbove ? Severity.Red
         : percent >= orangeAt ? Severity.Orange
         : Severity.Green;

    /// <summary>Severity from usage relative to the clock: ratio 1.0 means the fill sits exactly on
    /// the elapsed marker, above 1.0 means the cap arrives before the reset. The pace verdict
    /// replaces the absolute one rather than being maxed with it — 60% used with 5.5 of 7 days gone
    /// is genuinely fine, and saying so is the point. Two guards survive it: past redAbove is always
    /// Red (running out is running out), and below the floor or inside the early-period dead zone the
    /// absolute thresholds decide instead. A null fraction — no reset time, or one too stale to
    /// trust — also falls back to absolute.</summary>
    public static Severity ForPace(int percent, double? elapsedFraction,
        int orangeAt = 50, int redAbove = 85)
    {
        if (percent > redAbove) return Severity.Red;
        if (PaceRatio(percent, elapsedFraction, redAbove) is not { } ratio)
            return For(percent, orangeAt, redAbove);

        return ratio >= RedRatio ? Severity.Red
             : ratio >= OrangeRatio ? Severity.Orange
             : Severity.Green;
    }

    /// <summary>The ratio behind a <see cref="ForPace"/> verdict, or null whenever pace did not
    /// decide it — so callers can show the number exactly when it explains the colour.</summary>
    public static double? PaceRatio(int percent, double? elapsedFraction, int redAbove = 85)
    {
        if (percent > redAbove) return null;
        if (elapsedFraction is not { } elapsed || elapsed < DeadZone) return null;
        if (percent < Floor) return null;
        return percent / (elapsed * 100);
    }
}
