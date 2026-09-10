namespace ClaudeUsageTray.Core;

/// <summary>One notifiable value of a snapshot. Key is stable across polls and is what the
/// notifier files its state under; Label is the popup's caption text, so a toast and the row it
/// refers to cannot be worded differently.
///
/// Two severities, computed in the same call from the same inputs — the anti-drift property that
/// matters, not the field count. Severity is what the badge and the bar draw. NotifySeverity is what
/// every notification decision reads, and it ignores an inferred reset: a wrong badge colour is a
/// glance you re-check, a wrong toast is an interruption you cannot undo. On Claude Code data, and
/// on any stated or reported reset, the two are equal by construction.</summary>
public sealed record UsageValue(string Key, string Label, int Percent, Severity Severity,
    DateTimeOffset? ResetsAt, Severity NotifySeverity);

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

    /// <summary>The drawing verdict and the notification verdict for one window, from one set of
    /// inputs. An inferred reset is withheld from the second — a null fraction still falls back to
    /// the absolute thresholds, so an inferred-reset value at 90 % toasts exactly as it does today.</summary>
    public static (Severity Draw, Severity Notify) WindowSeverities(WindowUsage usage, TimeSpan period,
        Settings settings, DateTimeOffset now)
    {
        var fraction = TimeMarker.ElapsedFraction(usage.ResetsAt, period, now);
        return (SeverityRules.ForSettings(settings, usage.Percent, fraction),
            SeverityRules.ForSettings(settings, usage.Percent,
                usage.Origin == ResetOrigin.Inferred ? null : fraction));
    }

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
        {
            var (draw, notify) = WindowSeverities(five, FiveHourPeriod, settings, now);
            values.Add(new("5h", "5-hour window", five.Percent, draw, five.ResetsAt, notify));
        }
        if (snapshot.SevenDay is { } seven)
        {
            var (draw, notify) = WindowSeverities(seven, SevenDayPeriod, settings, now);
            values.Add(new("7d", "7-day window", seven.Percent, draw, seven.ResetsAt, notify));
        }
        foreach (var limit in snapshot.ScopedLimits)
        {
            // Scoped limits and credits never carry an inferred reset: only the desktop reader
            // assigns Inferred, and it emits neither.
            var severity = ScopedSeverity(limit, settings, now);
            values.Add(new(limit.Label, $"{limit.Label} weekly", limit.Percent, severity, limit.ResetsAt, severity));
        }
        if (snapshot.Credits is { } credits)
        {
            var severity = CreditSeverity(credits, settings);
            values.Add(new("credits", "Credits", credits.Percent, severity, null, severity));
        }
        return values;
    }
}
