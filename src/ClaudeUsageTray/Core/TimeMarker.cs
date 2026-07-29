namespace ClaudeUsageTray.Core;

public static class TimeMarker
{
    /// <summary>Elapsed fraction (0..1) of a period ending at resetsAt, or null when it cannot be
    /// trusted: no reset time, a non-positive period, or a fraction outside 0..1. Out-of-range is
    /// hidden rather than clamped in both directions — a marker pinned to an edge would assert a
    /// position the data does not support. A fraction above 1 means resetsAt is already past (a
    /// stale snapshot); below 0 means it is further out than one period (inconsistent data).</summary>
    public static double? ElapsedFraction(DateTimeOffset? resetsAt, TimeSpan period, DateTimeOffset now)
    {
        if (resetsAt is not { } reset || period <= TimeSpan.Zero) return null;

        var fraction = 1 - (reset - now) / period;
        return fraction is >= 0 and <= 1 ? fraction : null;
    }
}
