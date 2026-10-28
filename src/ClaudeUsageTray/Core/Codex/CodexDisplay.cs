namespace ClaudeUsageTray.Core;

/// <summary>What the Codex icons and the Codex popup section show. Codex has a single source, so
/// there is no precedence contest to run — but staleness is still a decision, and it belongs here
/// rather than in the UI. Reuses <see cref="SourceSelection.Age"/> so clock skew is clamped the same
/// way it is for Claude's slots, and shares Claude's StalenessMinutes allowance: both are live API
/// readings on a minutes cadence, unlike the Desktop history's hours.</summary>
public static class CodexDisplay
{
    public static DisplayChoice Choose(UsageSnapshot? snapshot, DateTimeOffset now, Settings settings,
        bool fetchFailed = false)
    {
        if (snapshot is null) return new(null, false);
        var stale = fetchFailed
            || SourceSelection.Age(snapshot, now) > TimeSpan.FromMinutes(settings.StalenessMinutes);
        return new(snapshot, stale);
    }
}
