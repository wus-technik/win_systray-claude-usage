namespace ClaudeUsageTray.Core;

/// <summary>How long a scoped limit's window is. An enum rather than a TimeSpan because a calendar
/// month has no fixed length: the payload gives a reset instant, not a window, and the only honest
/// things to say about the window are its name and an approximate length for pacing.</summary>
public enum ScopedPeriod { Weekly, Monthly }

public static class ScopedPeriods
{
    /// <summary>Pacing length only. Monthly is 30 days, so a 31-day month's first day has a reset
    /// further out than one period — TimeMarker.ElapsedFraction answers null there and the value
    /// paces absolutely, which is the fail-safe direction. The countdown shown to the user comes
    /// from the payload's own ResetsAt and is exact regardless.</summary>
    public static TimeSpan Length(ScopedPeriod period) => period switch
    {
        ScopedPeriod.Monthly => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(7),
    };

    /// <summary>The caption word. Used by both the popup row and the notification label, so a toast
    /// and the row it refers to cannot word the same limit differently.</summary>
    public static string Word(ScopedPeriod period) => period switch
    {
        ScopedPeriod.Monthly => "monthly",
        _ => "weekly",
    };
}
