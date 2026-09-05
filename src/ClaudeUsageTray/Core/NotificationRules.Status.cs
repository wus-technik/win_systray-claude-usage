namespace ClaudeUsageTray.Core;

public sealed partial class NotificationRules
{
    public static string StatusTag(string sourceId) => "status:" + sourceId;

    /// <summary>Per-source memory. Relevant is the state being watched — IsRelevant under the watch
    /// filter, not Degraded — and Filter/Notify are what re-baselines it when they change.</summary>
    private sealed class SourceState
    {
        public bool Relevant;
        public IReadOnlyList<string> Filter = [];
        public bool Notify;
    }

    private readonly Dictionary<string, SourceState> _sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One outcome per source that has something to say — a notification or a log line —
    /// in view order. Sources not in <paramref name="views"/> are disabled and drop their state, so
    /// re-enabling one re-baselines instead of announcing an outage that began while it was off.</summary>
    public IReadOnlyList<NotificationOutcome> OnStatus(IReadOnlyList<SourceView> views, Settings settings)
    {
        var present = new HashSet<string>(views.Select(v => v.Source.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _sources.Keys.Where(k => !present.Contains(k)).ToList()) _sources.Remove(gone);

        var outcomes = new List<NotificationOutcome>();
        foreach (var view in views)
        {
            var outcome = OnStatus(view, settings.NotifyFor(view.Source.Id));
            if (outcome.Notification is not null || outcome.Log.Count > 0) outcomes.Add(outcome);
        }
        return outcomes;
    }

    private NotificationOutcome OnStatus(SourceView view, bool notify)
    {
        var id = view.Source.Id;
        var log = new List<string>();

        // A null Status is not a reading: Render() runs before the first fetch completes, and
        // collapsing null into "not relevant" would make the first fetch of an already-degraded page
        // a false → true transition at startup.
        if (view.Status is not { } status) return new(null, log, false);

        bool relevant = StatusDetail.IsRelevant(status, view.Filter);

        if (!_sources.TryGetValue(id, out var state))
        {
            _sources[id] = new SourceState { Relevant = relevant, Filter = view.Filter, Notify = notify };
            return new(null, log, false);   // first reading is baseline
        }

        // StatusMonitor.ApplyEnabled keeps a source's PlatformStatus across a settings change, so a
        // widened filter would flip IsRelevant against an unchanged payload. The user's edit is not news.
        bool filterChanged = !state.Filter.SequenceEqual(view.Filter, StringComparer.OrdinalIgnoreCase);
        if (filterChanged || state.Notify != notify)
        {
            log.Add($"notify[{StatusTag(id)}]: {(filterChanged ? "filter change" : "notify change")}; rebaselining");
            state.Relevant = relevant;
            state.Filter = view.Filter;
            state.Notify = notify;
            return new(null, log, false);
        }

        if (relevant == state.Relevant) return new(null, log, false);
        state.Relevant = relevant;

        var notification = relevant ? Degraded(view.Source, status, view.Filter) : Recovered(view.Source, status);
        var direction = relevant ? "degraded" : "recovered";
        if (!notify)
        {
            log.Add($"notify[{StatusTag(id)}]: switched off; suppressed {direction}");
            return new(null, log, false);
        }
        log.Add($"notify[{StatusTag(id)}]: emitted {direction} (indicator={status.Indicator})");
        return new(notification, log, false);
    }

    private static Notification Degraded(StatusSource source, PlatformStatus status, IReadOnlyList<string> filter)
    {
        var summary = StatusDetail.Summary(status, filter);
        var body = summary.Length > 0 ? summary
            : string.IsNullOrWhiteSpace(status.Description) ? status.Indicator : status.Description;
        return new Notification(StatusDetail.Header(source, status, relevant: true, stale: false), body,
            OpenPopupArgument, StatusTag(source.Id), null);
    }

    /// <summary>The watched disruption is over. The body is the popup's own header for this state,
    /// which distinguishes a clear page ("Claude status: All Systems Operational") from one that is
    /// still degraded outside the watch filter — saying "all clear" there would be a falsehood about a
    /// page the user can go and read.</summary>
    private static Notification Recovered(StatusSource source, PlatformStatus status)
        => new($"{source.DisplayName} status recovered",
            StatusDetail.Header(source, status, relevant: false, stale: false),
            OpenPopupArgument, StatusTag(source.Id), null);
}
