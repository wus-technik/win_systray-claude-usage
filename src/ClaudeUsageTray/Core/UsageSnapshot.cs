namespace ClaudeUsageTray.Core;

/// <summary>Usage for one rolling window. Percent is the raw integer from the cache (may exceed 100).</summary>
public sealed record WindowUsage(int Percent, DateTimeOffset? ResetsAt);

/// <summary>One scoped weekly limit from limits[] — scoped to a model (e.g. Fable), a surface, or
/// both. Label is payload-derived and doubles as the dedup key. IsActive is retained but never
/// filtered on: the observed payload has a real 90% weekly limit flagged is_active:false, so the
/// flag cannot mean "does not apply".</summary>
public sealed record ScopedLimit(
    string Label, string? ModelId, int Percent, DateTimeOffset? ResetsAt, bool IsActive);

/// <summary>An amount in the payload's own money encoding: minor units + ISO code + exponent.</summary>
public sealed record Money(long AmountMinor, string Currency, int Exponent);

/// <summary>Credit state beyond the percentage. A single bool cannot express the observed case of
/// spend.enabled == true alongside an org spend cap already being reached.</summary>
public sealed record CreditState(bool Enabled, string? DisabledReason, bool LimitReached);

/// <summary>Extra-usage credits. Used/Limit are null when only the legacy extra_usage block is
/// available: its units are unverified (the field is named used_credits, and spend.cap carries
/// separate money and credits slots), so Percent is then the only trustworthy figure.</summary>
public sealed record CreditUsage(
    Money? Used, Money? Limit, int Percent, string? PayloadSeverity, CreditState State);

/// <summary>The parsed usage payload. Windows are null when absent from the source.</summary>
public sealed record UsageSnapshot(
    DateTimeOffset FetchedAt,
    WindowUsage? FiveHour,
    WindowUsage? SevenDay,
    IReadOnlyList<ScopedLimit>? ScopedLimits = null,
    CreditUsage? Credits = null)
{
    /// <summary>Empty means absent. Never null to consumers, whatever the caller passed.</summary>
    public IReadOnlyList<ScopedLimit> ScopedLimits { get; init; } = ScopedLimits ?? [];
}
