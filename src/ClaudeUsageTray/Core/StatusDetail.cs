namespace ClaudeUsageTray.Core;

/// <summary>How loudly a source's banner should be drawn. Muted covers health, no data, and a
/// disruption the user filtered out; the split between Warning and Alert follows the page's own
/// indicator, with anything unrecognised treated as the louder of the two.</summary>
public enum StatusEmphasis { Muted, Warning, Alert }

/// <summary>Every display decision about platform status, as pure functions: what counts as
/// relevant under a watch filter, which rows to draw, what the header and tooltip say. The WinForms
/// layer turns the results into controls and decides nothing.</summary>
public static partial class StatusDetail
{
    /// <summary>Whether a disruption is worth colour and tooltip space under this filter. True when
    /// the page is degraded and either the filter is empty, a watched component or incident is
    /// affected, or nothing in the payload identifies what is affected at all — that last case is
    /// the same "fail towards visible" rule the unknown-indicator handling uses.</summary>
    public static bool IsRelevant(PlatformStatus status, IReadOnlyList<string> filter)
    {
        if (!status.Degraded) return false;
        if (filter.Count == 0) return true;
        foreach (var incident in status.Incidents)
            if (IncidentWatched(incident, filter)) return true;
        foreach (var component in status.Components)
            if (ComponentFilter.Matches(component.Name, filter)) return true;
        return !Identifies(status);
    }

    public static StatusEmphasis Emphasis(PlatformStatus? status, bool relevant)
    {
        if (status is not { Degraded: true } || !relevant) return StatusEmphasis.Muted;
        return status.Indicator == "minor" ? StatusEmphasis.Warning : StatusEmphasis.Alert;
    }

    /// <summary>An incident naming no components counts as watched: "unclassified" must not mean
    /// "invisible".</summary>
    private static bool IncidentWatched(PlatformIncident incident, IReadOnlyList<string> filter)
    {
        if (incident.Components.Count == 0) return true;
        foreach (var name in incident.Components)
            if (ComponentFilter.Matches(name, filter)) return true;
        return false;
    }

    /// <summary>Whether the payload says anything at all about what is affected.</summary>
    private static bool Identifies(PlatformStatus status)
        => status.Components.Count > 0 || status.Incidents.Any(i => i.Components.Count > 0);
}
