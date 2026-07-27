namespace ClaudeUsageTray.Core;

/// <summary>The scoped-limit rows the popup should draw, plus how many were withheld.</summary>
public sealed record ScopedRows(IReadOnlyList<ScopedLimit> Visible, int HiddenCount);

public static class PopupRows
{
    /// <summary>Row budget for scoped limits. The popup is AutoSize and PositionNearCursor clamps
    /// its position but not its size, so an unbounded row count would clip off-screen.</summary>
    public const int Cap = 4;

    /// <summary>Active limits always render, even past the cap: the cap exists to bound a list of
    /// background limits, and must never be the reason the limit actually throttling the user is
    /// invisible. HiddenCount therefore counts only withheld inactive rows.</summary>
    public static ScopedRows ForScopedLimits(IReadOnlyList<ScopedLimit> limits)
    {
        var active = limits.Where(l => l.IsActive).ToList();
        var inactive = limits.Where(l => !l.IsActive).ToList();

        var slots = Math.Max(0, Cap - active.Count);
        var shownInactive = Math.Min(slots, inactive.Count);

        return new ScopedRows(
            [.. active, .. inactive.Take(shownInactive)],
            inactive.Count - shownInactive);
    }
}
