namespace ClaudeUsageTray.Core;

/// <summary>Freshness rule: a snapshot only replaces the current one when strictly newer —
/// this is what stops the 30 s cache re-read from clobbering a fresher API fetch.</summary>
public static class SnapshotPrecedence
{
    public static bool IsNewer(UsageSnapshot? candidate, UsageSnapshot? current)
        => candidate is not null && (current is null || candidate.FetchedAt > current.FetchedAt);
}
