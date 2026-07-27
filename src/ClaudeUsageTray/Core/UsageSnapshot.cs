namespace ClaudeUsageTray.Core;

/// <summary>Usage for one rolling window. Percent is the raw integer from the cache (may exceed 100).</summary>
public sealed record WindowUsage(int Percent, DateTimeOffset? ResetsAt);

/// <summary>One scoped weekly limit from limits[] — scoped to a model (e.g. Fable), a surface, or
/// both. Label is payload-derived and doubles as the dedup key. IsActive is retained but never
/// filtered on: the observed payload has a real 90% weekly limit flagged is_active:false, so the
/// flag cannot mean "does not apply".</summary>
public sealed record ScopedLimit(
    string Label, string? ModelId, int Percent, DateTimeOffset? ResetsAt, bool IsActive);

/// <summary>The parsed usage payload. Windows are null when absent from the source.</summary>
public sealed record UsageSnapshot(
    DateTimeOffset FetchedAt,
    WindowUsage? FiveHour,
    WindowUsage? SevenDay,
    IReadOnlyList<ScopedLimit>? ScopedLimits = null)
{
    /// <summary>Empty means absent. Never null to consumers, whatever the caller passed.</summary>
    public IReadOnlyList<ScopedLimit> ScopedLimits { get; init; } = ScopedLimits ?? [];
}
