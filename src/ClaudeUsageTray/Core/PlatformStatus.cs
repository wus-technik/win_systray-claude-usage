namespace ClaudeUsageTray.Core;

/// <summary>One currently unresolved incident from the Claude status page. Status is
/// investigating/identified/monitoring, or "unknown" when the page omits it; Impact is
/// none/minor/major/severe/critical, or null when the page omits it. Component names are
/// the page's own, shown as-is.</summary>
public sealed record PlatformIncident(
    string Name, string Status, string? Impact, string? Shortlink,
    DateTimeOffset? UpdatedAt, IReadOnlyList<string> Components);

/// <summary>One component the page reports as anything other than operational. Name and status are
/// the page's own words; a renamed or new component needs no app update.</summary>
public sealed record PlatformComponent(string Name, string Status);

/// <summary>One successful fetch of the page's overall state. Degraded is the page's own banner —
/// any indicator other than "none" — so an indicator StatusPage has not invented yet still fails
/// towards visible rather than invisible. Components carries only the non-operational entries, so
/// no caller can accidentally render a wall of healthy components.</summary>
public sealed record PlatformStatus(
    string SourceId, DateTimeOffset FetchedAt, string Indicator, string Description,
    IReadOnlyList<PlatformIncident> Incidents, IReadOnlyList<PlatformComponent> Components)
{
    public bool Degraded => Indicator != "none";
}
