namespace ClaudeUsageTray.Core;

/// <summary>One notifiable value of a snapshot. Key is stable across polls and is what the
/// notifier files its state under; Label is the popup's caption text, so a toast and the row it
/// refers to cannot be worded differently.</summary>
public sealed record UsageValue(string Key, string Label, int Percent, Severity Severity, DateTimeOffset? ResetsAt);

/// <summary>The severity every surface draws for a value — badge, popup bar, toast — computed in
/// exactly one place. This type exists to prevent drift, not to save typing: three copies of "which
/// severity is this" would make the toast's agreement with the bar beside it a coincidence.</summary>
public static class UsageValues
{
    public static readonly TimeSpan FiveHourPeriod = TimeSpan.FromHours(5);
    public static readonly TimeSpan SevenDayPeriod = TimeSpan.FromDays(7);

    /// <summary>Matches the scoped-limit dedup in UsageJson.ReadScopedLimits, so a label whose case
    /// changed does not read as one key vanishing and another appearing.</summary>
    public static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    public static Severity WindowSeverity(WindowUsage usage, TimeSpan period, Settings settings, DateTimeOffset now)
        => SeverityRules.ForSettings(settings, usage.Percent,
            TimeMarker.ElapsedFraction(usage.ResetsAt, period, now));

    /// <summary>Scoped limits are weekly, whatever model or surface they are scoped to.</summary>
    public static Severity ScopedSeverity(ScopedLimit limit, Settings settings, DateTimeOffset now)
        => SeverityRules.ForSettings(settings, limit.Percent,
            TimeMarker.ElapsedFraction(limit.ResetsAt, SevenDayPeriod, now));

    /// <summary>Credits prefer the payload's own severity: it can encode account state — a spend cap
    /// already reached — that a percentage cannot express. Only when the payload says nothing do the
    /// configured thresholds decide, and then purely absolute: credits carry no reset time.</summary>
    public static Severity CreditSeverity(CreditUsage credits, Settings settings)
        => SeverityRules.FromPayload(credits.PayloadSeverity)
           ?? SeverityRules.ForSettings(settings, credits.Percent, null);

    /// <summary>Every value the snapshot has, in popup order. Absent values produce no row. All scoped
    /// limits are included, not just the four the popup draws: a limit pushed below a display cap says
    /// nothing about whether it matters.</summary>
    public static IReadOnlyList<UsageValue> Enumerate(UsageSnapshot snapshot, Settings settings, DateTimeOffset now)
    {
        var values = new List<UsageValue>();
        if (snapshot.FiveHour is { } five)
            values.Add(new("5h", "5-hour window", five.Percent,
                WindowSeverity(five, FiveHourPeriod, settings, now), five.ResetsAt));
        if (snapshot.SevenDay is { } seven)
            values.Add(new("7d", "7-day window", seven.Percent,
                WindowSeverity(seven, SevenDayPeriod, settings, now), seven.ResetsAt));
        foreach (var limit in snapshot.ScopedLimits)
            values.Add(new(limit.Label, $"{limit.Label} weekly", limit.Percent,
                ScopedSeverity(limit, settings, now), limit.ResetsAt));
        if (snapshot.Credits is { } credits)
            values.Add(new("credits", "Credits", credits.Percent, CreditSeverity(credits, settings), null));
        return values;
    }
}
