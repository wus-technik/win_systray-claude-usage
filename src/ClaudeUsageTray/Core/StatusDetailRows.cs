namespace ClaudeUsageTray.Core;

/// <summary>One detail line under a status header. Link is the incident shortlink when the page
/// offers one; component rows have none, because status.openai.com publishes no per-incident link.</summary>
public sealed record StatusRow(string Text, string? Link);

public static partial class StatusDetail
{
    /// <summary>The detail rows for one source, at most <paramref name="max"/> of them. Selection
    /// happens after filtering, so incidents that are all filtered out fall through to the watched
    /// components rather than leaving a degraded source with an empty section.</summary>
    public static IReadOnlyList<StatusRow> Rows(PlatformStatus status, IReadOnlyList<string> filter,
        DateTimeOffset now, int max)
        => Selected(status, filter, now).Take(max).ToList();

    /// <summary>How many rows the cap left out, for the "+N more" line.</summary>
    public static int HiddenCount(PlatformStatus status, IReadOnlyList<string> filter, int max)
        => Math.Max(0, Selected(status, filter, DateTimeOffset.MinValue).Count - max);

    /// <summary>The header line: the source's name and the page's own banner text, verbatim. A
    /// disruption the filter excluded says so, so an empty section never looks like a parse
    /// failure.</summary>
    public static string Header(StatusSource source, PlatformStatus? status, bool relevant, bool stale)
    {
        if (status is null) return $"{source.DisplayName} status: unavailable";
        var text = string.IsNullOrWhiteSpace(status.Description) ? status.Indicator : status.Description;
        var header = $"{source.DisplayName} status: {text}";
        if (status.Degraded && !relevant) header += " · outside your watched components";
        if (stale) header += " · stale";
        return header;
    }

    private static List<StatusRow> Selected(PlatformStatus status, IReadOnlyList<string> filter,
        DateTimeOffset now)
    {
        var incidents = status.Incidents.Where(i => IncidentWatched(i, filter)).ToList();
        if (incidents.Count > 0)
            return incidents.Select(i => new StatusRow(DescribeIncident(i, now), i.Shortlink)).ToList();

        return status.Components
            .Where(c => ComponentFilter.Matches(c.Name, filter))
            .Select(c => new StatusRow($"{c.Name} — {Unfold(c.Status)}", null))
            .ToList();
    }

    /// <summary>One incident row: name, status with initial capital, impact when not none/missing,
    /// affected components, and age.</summary>
    private static string DescribeIncident(PlatformIncident incident, DateTimeOffset now)
    {
        var parts = new List<string> { $"{incident.Name} — {Capitalize(incident.Status)}" };
        if (!string.IsNullOrEmpty(incident.Impact) && incident.Impact != "none")
            parts.Add(incident.Impact);
        if (incident.Components.Count > 0)
            parts.Add(string.Join(", ", incident.Components));
        if (incident.UpdatedAt is { } updated)
            parts.Add($"updated {RelativeTime.Ago(updated, now)}");
        return string.Join(" · ", parts);
    }

    /// <summary>`degraded_performance` as `Degraded performance` — the page's vocabulary, made
    /// readable, never translated into ours.</summary>
    private static string Unfold(string status) => Capitalize(status.Replace('_', ' '));

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
