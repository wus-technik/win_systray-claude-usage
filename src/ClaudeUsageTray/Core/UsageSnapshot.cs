namespace ClaudeUsageTray.Core;

/// <summary>Which program produced the data. The cache file and the live API are both Claude
/// Code's; the distinction the user sees is Claude Code versus the Claude Desktop history.</summary>
public enum UsageSource { ClaudeCode, DesktopHistory }

/// <summary>Where a window's ResetsAt came from. Reported is the payload's own value (Claude Code,
/// cache or live). Inferred is reconstructed from the Claude Desktop history and is marked in the UI
/// and withheld from the notifier. Stated is the user's own weekly anchor — an assertion, not an
/// estimate, so it renders unmarked and may notify.</summary>
public enum ResetOrigin { Reported, Inferred, Stated }

/// <summary>Usage for one rolling window. Percent is the raw integer from the cache (may exceed 100).</summary>
public sealed record WindowUsage(int Percent, DateTimeOffset? ResetsAt)
{
    /// <summary>An init property rather than a third positional member on purpose: a positional one
    /// would change Deconstruct and force every construction site to pass a value, and the point is
    /// that the existing sites are untouched, not merely that they still compile. Every Claude Code
    /// path is Reported by construction rather than by remembering to pass it. (It does participate
    /// in the generated Equals either way — that is what the equality test below pins.)</summary>
    public ResetOrigin Origin { get; init; } = ResetOrigin.Reported;
}

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

    public UsageSource Source { get; init; } = UsageSource.ClaudeCode;
}
