namespace ClaudeUsageTray.Core;

/// <summary>Composes one window's fragment of the tray tooltip — before
/// <see cref="StatusDetail.ComposeTooltip"/> reserves room for platform-status suffixes and before
/// TrayApp's last-resort <c>TrimTooltip</c> cut. Pulled out of <c>Tray/TrayApp</c> so the
/// 127-character NotifyIcon budget (<see cref="StatusDetail.TooltipLimit"/>) is something a test can
/// hold this to, rather than something checked by eye against one machine's history file.</summary>
public static class UsageTooltip
{
    /// <summary>The "(estimate)" hedge sits inside the same clause as the "resets in" time it
    /// qualifies, not trailing the whole string. Both cuts that can shorten this text —
    /// TrimTooltip's hard cut and ComposeTooltip's head-shortening to make room for a status-badge
    /// suffix — cut from the right, so keeping the hedge next to the time it qualifies means the
    /// "updated … ago" tail is what disappears first, never the marking that makes an inferred reset
    /// recognisable as a guess.</summary>
    public static string Build(string label, WindowUsage usage, double? elapsedFraction, bool paceColors,
        int redThreshold, bool stale, UsageSource? source, DateTimeOffset? snapshotFetchedAt, DateTimeOffset now)
    {
        var parts = new List<string> { label, $"{usage.Percent}%" };
        // Only when pace decided the colour — otherwise the badge means percent and needs no gloss.
        if (paceColors
            && PaceFormat.Describe(SeverityRules.PaceRatio(usage.Percent, elapsedFraction, redThreshold))
                is { Length: > 0 } pace)
            parts.Add(pace);

        bool desktop = source == UsageSource.DesktopHistory;
        var resetsAt = ResetPresentation.EffectiveResetsAt(usage, now);
        if (resetsAt is { } r)
        {
            bool estimated = desktop && usage.Origin == ResetOrigin.Inferred;
            parts.Add($"resets in {(estimated ? "~" : "")}{RelativeTime.In(r, now)}{(estimated ? " (estimate)" : "")}");
            if (stale && r <= now) parts.Add("awaiting refresh"); // cached % may be the prior window
        }
        else if (desktop)
        {
            parts.Add("no reset time");
        }

        if (snapshotFetchedAt is { } fetchedAt)
        {
            if (stale)
                parts.Add($"stale · {(desktop ? "Claude Desktop history · " : "")}updated {RelativeTime.Ago(fetchedAt, now)}");
            else if (desktop)
                parts.Add($"Claude Desktop history · updated {RelativeTime.Ago(fetchedAt, now)}");
        }
        return string.Join(" · ", parts);
    }
}
