namespace ClaudeUsageTray.Core;

/// <summary>What the icons and the popup show, and whether to mark it stale.</summary>
public sealed record DisplayChoice(UsageSnapshot? Snapshot, bool Stale);

/// <summary>Fallback-only precedence between Claude Code's data (cache + live, already merged by
/// <see cref="SnapshotPrecedence"/>) and the Claude Desktop history. A current Claude Code snapshot
/// always wins: it is the richer one (reset times, scoped limits, money). The desktop history steps
/// in when Claude Code's is absent or past its cutoff — which is what fixes the desktop-only case,
/// and what replaces a Claude Code cache frozen for days by design rather than by accident. Each
/// source has its own allowance, because their cadences differ by an order of magnitude.</summary>
public static class SourceSelection
{
    /// <summary>Clock skew up to this counts as an age of zero. Beyond it the timestamp is not
    /// trusted, and the snapshot can only ever be shown as stale.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public static DisplayChoice Choose(UsageSnapshot? cli, UsageSnapshot? desktop, DateTimeOffset now, Settings settings)
    {
        if (cli is not null && Age(cli, now) <= TimeSpan.FromMinutes(settings.StalenessMinutes))
            return new(cli, false);
        if (desktop is not null && Age(desktop, now) <= TimeSpan.FromHours(settings.DesktopStalenessHours))
            return new(desktop, false);

        // Both stale, or only one present: a dead source degrades to stale, never to blank.
        if (cli is null && desktop is null) return new(null, false);
        if (cli is null) return new(desktop, true);
        if (desktop is null) return new(cli, true);
        return new(Age(desktop, now) < Age(cli, now) ? desktop : cli, true);
    }

    /// <summary>now - FetchedAt, clamped: small future skew is zero, anything further in the future
    /// is TimeSpan.MaxValue so it fails every freshness test and loses every "newer" comparison.</summary>
    public static TimeSpan Age(UsageSnapshot snapshot, DateTimeOffset now)
    {
        var age = now - snapshot.FetchedAt;
        if (age < -FutureTolerance) return TimeSpan.MaxValue;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
