namespace ClaudeUsageTray.Core;

public static class RelativeTime
{
    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var elapsed = now - then;
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        return Span(elapsed) + " ago";
    }

    public static string In(DateTimeOffset target, DateTimeOffset now)
    {
        var remaining = target - now;
        if (remaining <= TimeSpan.Zero) return "now";
        return Span(remaining);
    }

    private static string Span(TimeSpan d)
    {
        if (d.TotalDays >= 1)
        {
            int days = (int)d.TotalDays;
            return d.Hours > 0 ? $"{days}d {d.Hours}h" : $"{days}d";
        }
        if (d.TotalHours >= 1)
        {
            int hours = (int)d.TotalHours;
            return d.Minutes > 0 ? $"{hours}h {d.Minutes}m" : $"{hours}h";
        }
        return $"{Math.Max(1, (int)Math.Ceiling(d.TotalMinutes))}m";
    }
}
