namespace ClaudeUsageTray.Core;

/// <summary>One currently unresolved incident from the Claude status page. Status is
/// investigating/identified/monitoring, or "unknown" when the page omits it; Impact is
/// none/minor/major/severe/critical, or null when the page omits it. Component names are
/// the page's own, shown as-is.</summary>
public sealed record PlatformIncident(
    string Name, string Status, string? Impact, string? Shortlink,
    DateTimeOffset? UpdatedAt, IReadOnlyList<string> Components);

/// <summary>One successful fetch of the page's overall state. Degraded is the page's own
/// banner — any indicator other than "none" — so an indicator StatusPage has not invented
/// yet still fails towards visible rather than invisible.</summary>
public sealed record PlatformStatus(
    DateTimeOffset FetchedAt, string Indicator, string Description,
    IReadOnlyList<PlatformIncident> Incidents)
{
    public bool Degraded => Indicator != "none";
}