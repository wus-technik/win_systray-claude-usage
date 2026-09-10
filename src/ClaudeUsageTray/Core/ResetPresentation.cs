namespace ClaudeUsageTray.Core;

/// <summary>The single place that decides whether a window's <see cref="WindowUsage.ResetsAt"/> is
/// still fit to show. `TrayApp` re-reads the desktop history every 30 s but only adopts a re-read
/// when <see cref="SnapshotPrecedence.IsNewer"/> — deliberately, since that rule is what stops a
/// stale cache re-read from clobbering a fresher API fetch. The cost is that when Claude Desktop
/// stops writing samples, the held snapshot keeps whatever inferred reset it last had even after
/// that instant has passed: an expired estimate says nothing about whatever may have started since
/// (the same reasoning gate 2 applies at inference time), so it is treated as absent here instead of
/// changing the adoption rule. `Reported` and `Stated` resets are assertions, not guesses, and are
/// left exactly as given even when they too lie in the past — that case already has its own
/// "awaiting refresh" wording downstream.</summary>
public static class ResetPresentation
{
    public static DateTimeOffset? EffectiveResetsAt(WindowUsage usage, DateTimeOffset now)
        => usage.Origin == ResetOrigin.Inferred && usage.ResetsAt is { } r && r <= now ? null : usage.ResetsAt;
}
