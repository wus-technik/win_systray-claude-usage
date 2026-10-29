namespace ClaudeUsageTray.Core;

/// <summary>
/// Remembers what it last saw and turns one provider's readings into transitions. Stateful but
/// clock-free: the caller supplies now, so every rule below is a unit test. One instance per
/// provider — the provider's notification settings arrive through <paramref name="notifications"/>
/// rather than being read off Settings, because an instance that reached for
/// Settings.UsageNotifications would run every provider on Claude's level. A selector rather than a
/// value: Settings is replaced wholesale on save, so a captured reference would go stale.
///
/// Usage rules, in order: stale → nothing recorded; fingerprint change → clear; source switch →
/// clear; unarmed → record baseline; per key, notify on crossing from below the level to at/above it,
/// re-armed only on return to Green; absent keys evicted; crossings coalesced into one toast; the
/// on/off switch suppresses only the output, never the recording. Retraction is driven by whether a
/// key's toast was actually shown (Announced), not by the hysteresis latch (Notified) alone — a key
/// already red at the baseline, or one that crossed while notifications were off, was never named in
/// any toast and so leaving red must not retract someone else's.
/// </summary>
public sealed class UsageNotificationRules(
    string tag, Func<Settings, UsageNotificationSettings> notifications, string titlePrefix = "")
{
    public static UsageNotificationRules ForClaude()
        => new(NotificationTags.Usage("claude"), s => s.UsageNotifications);

    public static UsageNotificationRules ForCodex()
        => new(NotificationTags.Usage("codex"), s => s.CodexNotifications, "Codex ");

    /// <summary>This provider's Action Center slot. The retraction site reads it from here rather
    /// than from a constant, so emission and retraction cannot drift.</summary>
    public string Tag => tag;

    /// <summary>Toast lifetime for a value with no known reset (credits), so Action Center does not
    /// assert a red credit line for days.</summary>
    public static readonly TimeSpan DefaultUsageToastLifetime = TimeSpan.FromHours(6);

    /// <summary>Above: at or above the configured level at the last evaluation. Notified: has fired
    /// and not yet returned to Green — the hysteresis latch, unaffected by whether anything was ever
    /// shown. Announced: true only while a toast naming this key is actually on screen — set when the
    /// key is part of an emitted notification, cleared as soon as the key drops below the level. Only
    /// Announced may trigger a retraction; Notified alone must not, or a key that was already red
    /// before the app ever toasted (or crossed while notifications were off) would retract a toast
    /// that never named it.</summary>
    private sealed class KeyState
    {
        public bool Above;
        public bool Notified;
        public bool Announced;
    }

    /// <summary>The settings that can change a verdict without the value moving. The on/off switches
    /// are deliberately absent: they change no verdict.</summary>
    private sealed record Fingerprint(int Orange, int Red, bool PaceColors, int StalenessMinutes,
        int DesktopStalenessHours, NotifyLevel Level, string? WeeklyResetAnchor)
    {
        public static Fingerprint Of(Settings s, UsageNotificationSettings n) => new(s.Thresholds.Orange,
            s.Thresholds.Red, s.PaceColors, s.StalenessMinutes, s.DesktopStalenessHours, n.Level,
            s.WeeklyResetAnchor);
    }

    private readonly Dictionary<string, KeyState> _keys = new(UsageValues.KeyComparer);
    private Fingerprint? _fingerprint;
    private UsageSource? _source;
    private bool _armed;
    private int _concludedAttempts;
    private bool _loggedUnarmedBaseline;
    private bool _lastWasStale;

    public bool IsArmed => _armed;

    /// <summary>Arms on the first terminal outcome — a snapshot, a rejected token, or no token at all
    /// — or on the third concluded attempt of any kind, so a permanently offline machine still gets
    /// usage toasts from cache. 429 and network errors are not terminal: the documented common case is
    /// a rate-limited first fetch, and arming on it would baseline from the cache and then toast about
    /// a limit that was already red when the first successful fetch arrives. Arming clears all usage
    /// state, so the next evaluation is an ordinary first-sight baseline. Returns a log line when it
    /// armed, else null.</summary>
    public string? NoteLiveOutcome(LiveOutcome outcome)
    {
        if (_armed) return null;
        _concludedAttempts++;
        bool terminal = outcome is LiveOutcome.Snapshot or LiveOutcome.Unauthorized or LiveOutcome.NoToken;
        if (!terminal && _concludedAttempts < 3) return null;

        _armed = true;
        _keys.Clear();
        return $"notify[usage]: armed on {(terminal ? outcome.ToString() : $"attempt {_concludedAttempts} ({outcome})")}; next reading is the baseline";
    }

    public NotificationOutcome OnUsage(DisplayChoice choice, Settings settings, DateTimeOffset now)
    {
        var log = new List<string>();

        // Rule 1 — first, before anything else: nothing recorded, so a crossing during the gap is
        // still compared against the pre-stale state, and an unarmed startup can never baseline
        // against an hours-old cache.
        if (choice.Snapshot is not { } snapshot || choice.Stale)
        {
            if (choice.Snapshot is not null && !_lastWasStale) log.Add("notify[usage]: stale; recording nothing until fresh data returns");
            _lastWasStale = choice.Snapshot is not null;
            return new(null, log, false);
        }
        _lastWasStale = false;

        var mine = notifications(settings);

        // Rule 2 — the user's own edit is never news.
        var fingerprint = Fingerprint.Of(settings, mine);
        if (_fingerprint is not null && fingerprint != _fingerprint)
        {
            _keys.Clear();
            log.Add("notify[usage]: fingerprint change (thresholds/pace/staleness/level/anchor); rebaselining");
        }
        _fingerprint = fingerprint;

        // Rule 3 — Claude Code and the Desktop history measure different things.
        if (_source is not null && snapshot.Source != _source)
        {
            _keys.Clear();
            log.Add($"notify[usage]: source switch {_source} → {snapshot.Source}; rebaselining");
        }
        _source = snapshot.Source;

        var values = UsageValues.Enumerate(snapshot, settings, now);
        var level = mine.Level;

        // Rule 6 — eviction first, so a vanished key cannot leave a latch behind. A notified key that
        // stops being reported has left the level exactly as surely as one that drops below it — the
        // toast named it, and the payload no longer backs that claim — so it is also an exit.
        var present = new HashSet<string>(values.Select(v => v.Key), UsageValues.KeyComparer);
        bool exited = false;
        foreach (var gone in _keys.Keys.Where(k => !present.Contains(k)).ToList())
        {
            if (_keys[gone] is { Above: true, Announced: true }) exited = true;
            _keys.Remove(gone);
        }

        var crossed = new List<UsageValue>();
        var latched = new List<UsageValue>();
        foreach (var value in values)
        {
            bool above = AtOrAbove(Verdict(value), level);
            if (!_keys.TryGetValue(value.Key, out var state))
            {
                // First sight is always baseline: startup into red, or a limit the payload only just
                // began reporting. Notified mirrors Above so a pre-existing red cannot toast on flap.
                _keys[value.Key] = new KeyState { Above = above, Notified = above };
                continue;
            }
            if (above && !state.Above && _armed)
            {
                if (!state.Notified) crossed.Add(value);
                else latched.Add(value);   // red → orange → red on the clock: one toast, not two
            }
            if (!above && state.Above && state.Announced) exited = true;
            if (!above) state.Announced = false;   // dropped below the level (Green included): nothing left to retract
            if (Verdict(value) == Severity.Green) state.Notified = false;   // hysteresis exit
            else if (above && !state.Above && _armed) state.Notified = true;
            state.Above = above;
        }

        // Rule 4 — unarmed: everything above recorded, nothing said.
        if (!_armed)
        {
            if (!_loggedUnarmedBaseline && values.Count > 0)
            {
                _loggedUnarmedBaseline = true;
                log.Add($"notify[usage]: unarmed baseline from {snapshot.Source}; waiting for a terminal live outcome");
            }
            return new(null, log, false);
        }

        // One tag holds one toast, and its body may name several keys. The moment any of them leaves
        // the level the toast asserts something false, so it is retracted whole; a key still red is
        // still red on the badge and in the popup, which is where a persistent claim belongs.
        bool remove = exited;

        if (latched.Count > 0)
            log.Add($"notify[usage]: hysteresis; {Describe(latched)} re-entered the level but stays latched until green");

        if (crossed.Count == 0) return new(null, log, remove);

        if (!mine.Enabled)
        {
            log.Add($"notify[usage]: switched off; suppressed crossing of {Describe(crossed)}");
            return new(null, log, remove);
        }

        // Only the path that actually returns a notification marks these keys as toasted — a
        // suppressed or unarmed crossing must never leave an Announced latch behind.
        foreach (var value in crossed)
            _keys[value.Key].Announced = true;

        var notification = Compose(crossed, now);
        log.Add($"notify[usage]: emitted for {Describe(crossed)}");
        return new(notification, log, false);
    }

    /// <summary>The verdict every notification decision reads. One accessor rather than four reads
    /// of the field, so a fifth decision site cannot be added against the drawing severity: an
    /// inferred reset colours the badge but must never be the reason for an interruption.</summary>
    private static Severity Verdict(UsageValue value) => value.NotifySeverity;

    private static bool AtOrAbove(Severity severity, NotifyLevel level) => level switch
    {
        NotifyLevel.Orange => severity >= Severity.Orange,
        _ => severity == Severity.Red,
    };

    /// <summary>"5-hour window (90 %) and Fable weekly (92 %) are now red". Mixed severities under
    /// level Orange become one sentence per colour, red first.</summary>
    private Notification Compose(IReadOnlyList<UsageValue> crossed, DateTimeOffset now)
    {
        var sentences = new List<string>();
        foreach (var severity in new[] { Severity.Red, Severity.Orange })
        {
            var group = crossed.Where(v => Verdict(v) == severity).ToList();
            if (group.Count == 0) continue;
            var names = group.Select(v => $"{v.Label} ({v.Percent} %)").ToList();
            var joined = names.Count == 1 ? names[0]
                : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
            sentences.Add($"{joined} {(names.Count == 1 ? "is" : "are")} now {severity.ToString().ToLowerInvariant()}");
        }

        var worst = crossed.Any(v => Verdict(v) == Severity.Red) ? "red" : "orange";
        var title = crossed.Count == 1
            ? $"{titlePrefix}usage limit {worst}" : $"{titlePrefix}usage limits {worst}";
        title = char.ToUpperInvariant(title[0]) + title[1..];

        DateTimeOffset? latestReset = crossed.Select(v => v.ResetsAt).Where(r => r is { } d && d > now).Max();
        var expires = latestReset ?? now + DefaultUsageToastLifetime;
        return new Notification(title, string.Join(" · ", sentences), NotificationTags.OpenPopupArgument, tag, expires);
    }

    /// <summary>Log-safe: the fixed keys by name, scoped limits by count only — their labels are the
    /// account-specific model names fetch.log must never carry. Codex spend control is a fixed
    /// app-supplied label and may be named with its percentage.</summary>
    private static string Describe(IReadOnlyList<UsageValue> values)
    {
        var fixedKeys = values.Where(v => v.Key is "5h" or "7d" or "credits").Select(v => $"{v.Key}={v.Percent}%");
        int scoped = values.Count(v => v.Key is not ("5h" or "7d" or "credits")
            && v.Key != CodexUsageJson.SpendControlLabel);
        var parts = fixedKeys.ToList();
        parts.AddRange(values.Where(v => v.Key == CodexUsageJson.SpendControlLabel)
            .Select(v => $"spend={v.Percent}%"));
        if (scoped > 0) parts.Add($"scoped={scoped}");
        return string.Join(" ", parts);
    }
}
