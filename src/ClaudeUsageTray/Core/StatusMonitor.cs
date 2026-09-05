namespace ClaudeUsageTray.Core;

/// <summary>
/// Per-source status state: which page is due for a fetch, what each one last said, and whether the
/// badge should warn. Clock-free and thread-free — every timestamp arrives from the caller — so the
/// whole multi-source policy is unit-testable and TrayApp is left holding only the HTTP call.
///
/// Each source carries its own <see cref="StatusScheduler"/>, which is what makes the isolation
/// invariant structural rather than a promise: one page timing out cannot back off, blank, or delay
/// the other.
/// </summary>
public sealed class StatusMonitor
{
    private sealed class Entry(StatusSource source, IReadOnlyList<string> filter)
    {
        public StatusSource Source { get; } = source;
        public IReadOnlyList<string> Filter { get; set; } = filter;
        public StatusScheduler Scheduler { get; } = new();
        public PlatformStatus? Status { get; set; }
        public bool InFlight { get; set; }
    }

    private readonly List<Entry> _entries = [];

    public StatusMonitor(IReadOnlyList<(StatusSource Source, IReadOnlyList<string> Filter)> enabled)
        => ApplyEnabled(enabled);

    /// <summary>The sources whose gate is open, marked in-flight and charged an attempt. Named for
    /// the mutation: a pure query paired with a separate RecordAttempt is a call that can be
    /// forgotten, and forgetting it means hammering a public endpoint.</summary>
    public IReadOnlyList<StatusSource> TakeDue(DateTimeOffset now)
    {
        var due = new List<StatusSource>();
        foreach (var entry in _entries)
        {
            if (entry.InFlight || !entry.Scheduler.CanFetch(now)) continue;
            entry.InFlight = true;
            entry.Scheduler.RecordAttempt(now);
            due.Add(entry.Source);
        }
        return due;
    }

    /// <summary>Files a completed fetch, or a null for a failed one. Returns false when the result
    /// was discarded: the source was disabled while its fetch was outstanding, or the payload's own
    /// SourceId disagrees with the id it arrived under. Both should be unreachable — which is
    /// exactly why neither may silently file an OpenAI outage under Claude. If a source is disabled
    /// and re-enabled while its fetch is outstanding, the completion is filed under the fresh entry —
    /// nothing is resurrected — and the 30 s floor charged by the new entry's first TakeDue prevents a
    /// double fetch.</summary>
    public bool Accept(string sourceId, PlatformStatus? result, DateTimeOffset now)
    {
        var entry = Find(sourceId);
        if (entry is null) return false;

        // The fetch is over either way. Clearing InFlight before any early return is what keeps a
        // discarded result from parking the source out of TakeDue for good.
        entry.InFlight = false;
        if (result is not null && !string.Equals(result.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
        {
            entry.Scheduler.RecordFailure(now);   // something is wrong with this source; back off, do not store
            return false;
        }

        if (result is null)
        {
            // Keep the last-known-good state: a dead endpoint degrades to stale, never to blank.
            entry.Scheduler.RecordFailure(now);
            return true;
        }
        entry.Scheduler.RecordSuccess();
        entry.Status = result;
        return true;
    }

    public PlatformStatus? Status(string sourceId) => Find(sourceId)?.Status;

    public IReadOnlyList<SourceView> Sources()
        => _entries.Select(e => new SourceView(e.Source, e.Status, e.Filter)).ToList();

    /// <summary>Whether the tray icon should carry the warning marker. Deliberately does not consult
    /// the watch filter for any source: the Claude filter has no dialog control, and a
    /// README-only JSON key must not be able to disarm the tray's main warning.</summary>
    public bool BadgeDegraded()
        => _entries.Any(e => e.Source.RaisesBadge && e.Status is { Degraded: true });

    /// <summary>Replaces the enabled set, keeping the state of sources that stay enabled — toggling
    /// one source must not blank another's banner, and with it the badge, for a poll cycle. Entries
    /// follow registry order; a newly added source is immediately due.</summary>
    public void ApplyEnabled(IReadOnlyList<(StatusSource Source, IReadOnlyList<string> Filter)> enabled)
    {
        var kept = new List<Entry>();
        foreach (var source in StatusSourceRegistry.All)
        {
            var match = enabled.FirstOrDefault(e => e.Source.Id == source.Id);
            if (match.Source is null) continue;
            var existing = Find(source.Id);
            if (existing is not null)
            {
                existing.Filter = match.Filter;
                kept.Add(existing);
            }
            else
            {
                kept.Add(new Entry(match.Source, match.Filter));
            }
        }
        _entries.Clear();
        _entries.AddRange(kept);
    }

    private Entry? Find(string sourceId)
        => _entries.FirstOrDefault(e => string.Equals(e.Source.Id, sourceId, StringComparison.OrdinalIgnoreCase));
}
