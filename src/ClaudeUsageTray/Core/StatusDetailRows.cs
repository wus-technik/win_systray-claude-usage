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

    /// <summary>The tooltip tail naming every relevant disruption, badge-raising source first.
    /// <paramref name="available"/> is how many characters the caller has left before NotifyIcon's
    /// 127-character limit: suffixes for non-badge sources are dropped **whole** when they do not
    /// fit, because a half-cut "· OpenAI: Minor serv…" is worse than none, and the badge-raising
    /// source's suffix is the text that explains the marker on the icon — it is never dropped.</summary>
    public static string TooltipSuffix(IReadOnlyList<SourceView> sources, DateTimeOffset now,
        int stalenessMinutes, int available)
    {
        var ordered = sources
            .Where(v => v.Status is { Degraded: true } s && IsRelevant(s, v.Filter))
            .OrderByDescending(v => v.Source.RaisesBadge)
            .ToList();

        var text = "";
        foreach (var view in ordered)
        {
            var status = view.Status!;
            var words = string.IsNullOrWhiteSpace(status.Description) ? status.Indicator : status.Description;
            var stale = now - status.FetchedAt > TimeSpan.FromMinutes(stalenessMinutes);
            var piece = $" · {view.Source.DisplayName}: {words}{(stale ? " (stale)" : "")}";
            if (!view.Source.RaisesBadge && text.Length + piece.Length > available) continue;
            text += piece;
        }
        return text;
    }

    /// <summary>NotifyIcon.Text hard limit.</summary>
    public const int TooltipLimit = 127;

    /// <summary>The complete tray tooltip. Badge-raising suffixes are reserved first — when the usage
    /// text plus those suffixes would not fit, the usage text is shortened, never the suffix — and the
    /// non-badge suffixes get whatever budget is left, dropped whole when it is not enough. Doing this
    /// here rather than in TrayApp is what makes the "badge suffix survives" rule a tested fact
    /// instead of a hope about trim order.</summary>
    public static string ComposeTooltip(string usageText, IReadOnlyList<SourceView> sources,
        DateTimeOffset now, int stalenessMinutes, int limit = TooltipLimit)
    {
        var badge = TooltipSuffix(sources.Where(v => v.Source.RaisesBadge).ToList(), now, stalenessMinutes, limit);
        var room = limit - badge.Length;
        var head = usageText.Length <= room ? usageText
            : room <= 1 ? ""
            : usageText[..(room - 1)] + "…";
        var text = head + badge;
        return text + TooltipSuffix(sources.Where(v => !v.Source.RaisesBadge).ToList(), now, stalenessMinutes,
            available: limit - text.Length);
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
