using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Tray;

public sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings;
    private readonly string _settingsPath;
    private readonly string _configPath;
    private readonly bool _isVelopackInstalled;

    // Hidden control: marshals FileSystemWatcher events onto the UI thread.
    private readonly Control _sync = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _retry = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 30_000 };
    private int _consecutiveReadFailures;

    // Live usage polling: 5 min steady state; all budget/backoff state lives in the
    // unit-tested FetchScheduler. Single-flight via _fetchInFlight (UI thread only).
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 300_000 };
    private readonly FetchScheduler _fetchScheduler = new();
    private readonly FetchLog _log = new(FetchLog.DefaultPath);
    private bool _fetchInFlight;
    private string? _rejectedToken;
    private string _lastFetchStatus = "no fetch yet";

    // Codex usage polling. Fully independent of the Claude path: its own scheduler, its own
    // single-flight flag, its own last-known-good snapshot. One provider's failure must never null,
    // clobber or delay the other's data. Driven by the same 5 min _poll tick, gated to 10 min by the
    // scheduler's floor — Claude's 20/h was tuned against a measured ceiling this endpoint does not
    // share, so Codex starts at half the rate and is revisited only if observed 429s justify it.
    private readonly FetchScheduler _codexScheduler = new(maxPerHour: 6, floor: TimeSpan.FromMinutes(10));
    private readonly CodexAvailability _codexAvailability = new();
    private readonly string _codexAuthPath = CodexPath.AuthFile(CodexPath.HomeFromEnvironment());
    private UsageSnapshot? _codexSnapshot;
    private bool _codexFetchInFlight;
    private bool _codexFetchFailed;
    private string? _codexRejectedToken;
    private string _codexFetchStatus = "no fetch yet";

    // Platform status polling: 60 s steady state (StatusPage's recommended cadence). All per-source
    // budget, backoff, single-flight and last-known-good state lives in the unit-tested
    // StatusMonitor. Fully independent of the usage path: a status failure can never null, clobber,
    // or delay usage data, and one source's failure can never affect the other's.
    private readonly System.Windows.Forms.Timer _statusPoll = new() { Interval = 60_000 };
    private readonly StatusMonitor _statusMonitor;

    // Last component names seen per source, for the settings dialog's reference caption. Kept here
    // rather than read from StatusMonitor because the monitor drops the entry for a disabled source,
    // and a user opening the dialog to re-enable one is precisely who needs the names.
    private readonly Dictionary<string, IReadOnlyList<string>> _componentNames =
        new(StringComparer.OrdinalIgnoreCase);

    // Desktop notifications. Every decision — baseline, arming, hysteresis, fingerprint, per-source
    // status state — lives in clock-free notification rules; the presenter only shows what it is
    // handed. One call site, in Render(): every path that changes data ends there, and the rules
    // make re-evaluating unchanged data a no-op, so a single site cannot be forgotten.
    private readonly UsageNotificationRules _usageNotifications = UsageNotificationRules.ForClaude();
    private readonly UsageNotificationRules _codexNotifications = UsageNotificationRules.ForCodex();
    private readonly StatusNotificationRules _statusNotifications = new();
    private readonly ToastPresenter _toasts;

    private readonly ContextMenuStrip _menu;
    private ToolStripMenuItem _updatedItem = null!, _restartToUpdateItem = null!;

    private NotifyIcon? _iconFive;
    private NotifyIcon? _iconSeven;
    private NotifyIcon? _codexIconFive;
    private NotifyIcon? _codexIconSeven;
    // Two sources, one shown: Claude Code's (cache + live, merged by SnapshotPrecedence) and the
    // Claude Desktop history. SourceSelection picks between them at render time; each slot keeps its
    // last-known-good value until its own allowance runs out, so a transient read failure never
    // blanks the display.
    private UsageSnapshot? _cliSnapshot;
    private UsageSnapshot? _desktopSnapshot;
    private DesktopHistoryStatus _desktopStatus = DesktopHistoryStatus.NotFound;
    private string? _noDataText;
    private UsageSource? _lastLoggedSource;
    private bool _settingsSaveFailed;
    private UsagePopup? _popup;
    private SettingsDialog? _settingsDialog;

    public TrayApp(Settings settings, string settingsPath, bool isVelopackInstalled)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        _configPath = ConfigPath.Resolve(settings.ConfigPathOverride);
        _isVelopackInstalled = isVelopackInstalled;
        _sync.CreateControl();
        _menu = BuildMenu();
        _statusMonitor = new StatusMonitor(settings.EnabledSources(_codexAvailability.State));
        _toasts = new ToastPresenter(AppInfo.Aumid, _sync, ShowPopup,
            message => _log.Write(DateTimeOffset.UtcNow, message));

        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_configPath))
            {
                SynchronizingObject = _sync,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            // Debounce ~500 ms: Claude Code rewrites the file in bursts.
            FileSystemEventHandler onChange = (_, _) => { _debounce.Stop(); _debounce.Start(); };
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Deleted += onChange;
            _watcher.Renamed += (_, _) => { _debounce.Stop(); _debounce.Start(); };
            _watcher.Error += (_, _) => Refresh(); // 30-second polling remains the recovery path
            _watcher.EnableRaisingEvents = true;
        }

        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        _retry.Tick += (_, _) => { _retry.Stop(); Refresh(); };
        _tick.Tick += (_, _) => Refresh(); // update relative text and recover from missed watcher events
        _tick.Start();
        _poll.Tick += (_, _) => { StartApiFetch(); StartCodexFetch(); };
        _poll.Start();
        _statusPoll.Tick += (_, _) => StartStatusFetch();
        _statusPoll.Start();

        ApplyDisplayMode();
        Refresh();
        StartApiFetch();
        StartCodexFetch();
        StartStatusFetch();
    }

    // ---- data ----

    private void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var read = UsageCacheReader.TryRead(_configPath);
        if (read is not null)
        {
            if (SnapshotPrecedence.IsNewer(read, _cliSnapshot)) _cliSnapshot = read;
            _consecutiveReadFailures = 0;
        }
        else if (!File.Exists(_configPath))
        {
            if (_cliSnapshot is null || now - _cliSnapshot.FetchedAt
                > TimeSpan.FromMinutes(_settings.StalenessMinutes))
            {
                _cliSnapshot = null;
            }
            _consecutiveReadFailures = 0;
        }
        else if (_cliSnapshot is null || ++_consecutiveReadFailures >= 3)
        {
            if (_cliSnapshot is null || now - _cliSnapshot.FetchedAt
                > TimeSpan.FromMinutes(_settings.StalenessMinutes))
            {
                _cliSnapshot = null;
            }
            _consecutiveReadFailures = 0;
        }
        else
        {
            _retry.Stop();
            _retry.Start(); // likely a partial replace; preserve the last known good snapshot briefly
        }

        // TimeZoneInfo.Local belongs here, not in Core: the anchor is a wall-clock statement, and
        // Core stays ambient-free so its DST cases are testable against an explicit zone.
        var desktop = DesktopUsageReader.ReadFirst(
            DesktopHistoryPath.ByFreshness(
                DesktopHistoryPath.Candidates(_settings.DesktopHistoryPathOverride,
                    DesktopHistoryPath.DefaultAppData, DesktopHistoryPath.DefaultLocalAppData)),
            now,
            WeeklyAnchor.TryParse(_settings.WeeklyResetAnchor),
            TimeZoneInfo.Local);
        _desktopStatus = desktop.Status;
        if (desktop.Snapshot is not null)
        {
            if (SnapshotPrecedence.IsNewer(desktop.Snapshot, _desktopSnapshot))
            {
                _desktopSnapshot = desktop.Snapshot;
                LogDesktopSample(now);
            }
        }
        else if (_desktopSnapshot is not null
            && SourceSelection.Age(_desktopSnapshot, now) > TimeSpan.FromHours(_settings.DesktopStalenessHours))
        {
            _desktopSnapshot = null; // past its allowance and no longer readable: let it go
        }

        // Computed here, not in Render(): it reads .claude.json again, and Render runs on every tick.
        _noDataText = _cliSnapshot is null && _desktopSnapshot is null
            ? NoDataReason.Describe(new NoDataFacts(
                UsageCacheReader.Status(_configPath),
                CredentialsReader.Status(CredentialsReader.DefaultPath, now),
                _desktopStatus))
            : null;

        Render();
    }

    // ---- live usage polling ----

    private void StartApiFetch()
    {
        var now = DateTimeOffset.UtcNow;
        if (_fetchInFlight) return; // transient; not worth logging on every timer tick
        if (!_fetchScheduler.CanFetch(now)) { _log.Write(now, "skip: budget/backoff gate not open yet"); return; }
        var token = CredentialsReader.TryReadAccessToken(CredentialsReader.DefaultPath, now);
        if (token is null)
        {
            // Normal on a desktop-only machine: the Claude Code the desktop app installs never writes
            // a credentials file. Say so in the popup rather than leaving "no fetch yet" forever.
            _lastFetchStatus = CredentialsReader.Status(CredentialsReader.DefaultPath, now) == CredentialStatus.Missing
                ? "no credentials file · live fetch off"
                : "no valid credentials · live fetch off";
            _log.Write(now, "skip: no valid access token (missing/expired/near-expiry)");
            // No usable token is a terminal outcome for arming: on a desktop-only machine this is
            // the permanent state, and waiting for a fetch that will never happen would leave usage
            // notifications off forever.
            if (_usageNotifications.NoteLiveOutcome(LiveOutcome.NoToken) is { } armed) _log.Write(now, armed);
            return;
        }
        if (token == _rejectedToken) { _log.Write(now, "skip: token previously rejected (401/403); waiting for refresh"); return; }

        _fetchInFlight = true;
        _fetchScheduler.RecordAttempt(now);
        _log.Write(now, "attempt: GET oauth/usage");
        _ = Task.Run(async () =>
        {
            var result = await UsageApiClient.FetchAsync(Http, token, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
            try { _sync.BeginInvoke((Action)(() => OnApiFetchCompleted(result, token))); }
            catch (InvalidOperationException) { /* app shutting down */ }
        });
    }

    private void OnApiFetchCompleted(UsageFetchResult result, string token)
    {
        _fetchInFlight = false;
        var now = DateTimeOffset.UtcNow;
        if (result.Snapshot is not null)
        {
            _fetchScheduler.RecordSuccess();
            _rejectedToken = null;
            bool adopted = SnapshotPrecedence.IsNewer(result.Snapshot, _cliSnapshot);
            if (adopted)
            {
                _cliSnapshot = result.Snapshot;
                _consecutiveReadFailures = 0;
            }
            string five = result.Snapshot.FiveHour?.Percent.ToString() ?? "-";
            string seven = result.Snapshot.SevenDay?.Percent.ToString() ?? "-";
            _lastFetchStatus = $"live · 5h={five}% 7d={seven}%";
            _log.Write(now, $"200 ok: 5h={five}% 7d={seven}% ({(adopted ? "adopted" : "not newer than current, kept")})");
            Arm(LiveOutcome.Snapshot, now);
        }
        else if (result.Unauthorized)
        {
            // Claude Code owns the credentials; wait for it to refresh them rather than retrying.
            _rejectedToken = token;
            _lastFetchStatus = "token rejected (401/403)";
            _log.Write(now, "401/403 unauthorized: token rejected; waiting for Claude Code to refresh it");
            Arm(LiveOutcome.Unauthorized, now);
        }
        else if (result.RateLimited)
        {
            _fetchScheduler.RecordRateLimited(now, result.RetryAfter);
            string ra = result.RetryAfter is { } r ? $"{(int)r.TotalSeconds}s" : "none";
            _lastFetchStatus = "rate-limited (429)";
            _log.Write(now, $"429 rate-limited: retry-after={ra}; backing off max(Retry-After, 60s), bounded by 20/h cap");
            Arm(LiveOutcome.RateLimited, now);
        }
        else
        {
            _fetchScheduler.RecordFailure(now);
            _lastFetchStatus = "network/other error";
            _log.Write(now, "network/other error: no response; escalating 5/10/20 min backoff");
            Arm(LiveOutcome.Failed, now);
        }
        Render();
    }

    private void Arm(LiveOutcome outcome, DateTimeOffset now)
    {
        if (_usageNotifications.NoteLiveOutcome(outcome) is { } line) _log.Write(now, line);
    }

    private void StartCodexFetch()
    {
        var now = DateTimeOffset.UtcNow;
        if (_codexFetchInFlight) return;
        if (!_codexScheduler.CanFetch(now)) return;   // silent: the 10 min floor skips most ticks

        var auth = CodexCredentialsReader.TryRead(_codexAuthPath, now);
        if (auth is null)
        {
            _codexFetchFailed = true;
            var status = CodexCredentialsReader.Status(_codexAuthPath, now);
            if (status == CredentialStatus.Missing)
            {
                _codexAvailability.NoteCredentialsMissing();
                _codexFetchStatus = "no Codex credentials";
            }
            else
            {
                _codexAvailability.NoteCredentialsUnreadable();
                _codexFetchStatus = "Codex credentials unusable";
            }
            ArmCodex(LiveOutcome.NoToken, now);
            ApplyDisplayMode();
            _statusMonitor.ApplyEnabled(_settings.EnabledSources(_codexAvailability.State));
            Render();
            return;
        }

        _codexAvailability.NoteCredentialsReadable();
        if (auth.AccessToken == _codexRejectedToken) return;

        _codexFetchInFlight = true;
        _codexScheduler.RecordAttempt(now);
        _log.Write(now, "codex: attempt: GET codex/usage");
        _ = Task.Run(async () =>
        {
            var result = await CodexUsageApiClient.FetchAsync(Http, auth, DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);
            try { _sync.BeginInvoke((Action)(() => OnCodexFetchCompleted(result, auth.AccessToken))); }
            catch (InvalidOperationException) { /* app shutting down */ }
        });
    }

    private void OnCodexFetchCompleted(UsageFetchResult result, string token)
    {
        _codexFetchInFlight = false;
        _codexFetchFailed = result.Snapshot is null;
        var now = DateTimeOffset.UtcNow;

        if (result.Snapshot is not null)
        {
            _codexScheduler.RecordSuccess();
            _codexRejectedToken = null;
            _codexSnapshot = result.Snapshot;
            _codexAvailability.NoteSnapshot();
            ArmCodex(LiveOutcome.Snapshot, now);

            string five = result.Snapshot.FiveHour?.Percent.ToString() ?? "-";
            string seven = result.Snapshot.SevenDay?.Percent.ToString() ?? "-";
            // Percentages only. The payload also carries an email, a user id, an account id and the
            // spend cap's money amounts; none of them is ever written here.
            string spend = result.Snapshot.ScopedLimits
                .FirstOrDefault(l => l.Label == CodexUsageJson.SpendControlLabel) is { } s
                ? $" spend={s.Percent}%" : "";
            _codexFetchStatus = $"live · 5h={five}% 7d={seven}%";
            _log.Write(now, $"codex: 200 ok: 5h={five}% 7d={seven}%{spend}");
        }
        else if (result.Unauthorized)
        {
            _codexRejectedToken = token;
            ArmCodex(LiveOutcome.Unauthorized, now);
            _codexAvailability.NoteFetchFailure();
            _codexFetchStatus = "Codex token rejected (401/403)";
            _log.Write(now, "codex: 401/403 unauthorized: token rejected; waiting for Codex to refresh it");
        }
        else if (result.RateLimited)
        {
            _codexScheduler.RecordRateLimited(now, result.RetryAfter);
            ArmCodex(LiveOutcome.RateLimited, now);
            _codexAvailability.NoteFetchFailure();
            string ra = result.RetryAfter is { } r ? $"{(int)r.TotalSeconds}s" : "none";
            _codexFetchStatus = "Codex rate-limited (429)";
            _log.Write(now, $"codex: 429 rate-limited: retry-after={ra}");
        }
        else
        {
            _codexScheduler.RecordFailure(now);
            ArmCodex(LiveOutcome.Failed, now);
            _codexAvailability.NoteFetchFailure();
            _codexFetchStatus = "Codex network/other error";
            _log.Write(now, "codex: network/other error: no response");
        }

        ApplyDisplayMode();
        _statusMonitor.ApplyEnabled(_settings.EnabledSources(_codexAvailability.State));
        Render();
    }

    private void ArmCodex(LiveOutcome outcome, DateTimeOffset now)
    {
        if (_codexNotifications.NoteLiveOutcome(outcome) is { } line) _log.Write(now, $"codex: {line}");
    }

    // ---- platform status polling ----

    private void StartStatusFetch()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var source in _statusMonitor.TakeDue(now))
        {
            _log.Write(now, $"status[{source.Id}]: attempt: GET summary.json");
            _ = Task.Run(async () =>
            {
                var result = await PlatformStatusApi.FetchAsync(Http, source, DateTimeOffset.UtcNow,
                    CancellationToken.None).ConfigureAwait(false);
                try { _sync.BeginInvoke((Action)(() => OnStatusFetchCompleted(source.Id, result))); }
                catch (InvalidOperationException) { /* app shutting down */ }
            });
        }
    }

    private void OnStatusFetchCompleted(string sourceId, PlatformStatus? result)
    {
        var now = DateTimeOffset.UtcNow;
        // For the settings dialog's reference caption, and ahead of Accept so a source disabled
        // mid-flight still leaves its names behind. A failed fetch keeps the previous names, the way
        // Accept keeps the previous status; a payload under the wrong id is refused here too.
        if (result is not null
            && string.Equals(result.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
        {
            _componentNames[sourceId] = result.ComponentNames;
        }
        if (!_statusMonitor.Accept(sourceId, result, now))
        {
            _log.Write(now, $"status[{sourceId}]: discarded: source disabled or id mismatch");
            return;
        }
        if (result is null)
        {
            // Keep the last-known-good state: a dead endpoint degrades to stale, never to blank.
            _log.Write(now, $"status[{sourceId}]: error: no usable response; backing off");
        }
        else if (result.Degraded)
        {
            var what = result.Incidents.Count > 0
                ? string.Join(", ", result.Incidents.Select(i => i.Name))
                : string.Join(", ", result.Components.Select(c => c.Name));
            _log.Write(now, $"status[{sourceId}]: degraded: indicator={result.Indicator} " +
                $"incidents={result.Incidents.Count} components={result.Components.Count}: {what}");
        }
        else
        {
            _log.Write(now, $"status[{sourceId}]: ok: indicator={result.Indicator} ({result.Description})");
        }
        Render();
    }

    // ---- rendering ----

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        var choice = SourceSelection.Choose(_cliSnapshot, _desktopSnapshot, now, _settings);
        LogSourceChange(choice, now);
        var codexChoice = CodexDisplay.Choose(_codexSnapshot, now, _settings, _codexFetchFailed);
        Notify(choice, codexChoice, now);
        bool claudeDegraded = _statusMonitor.BadgeDegraded(UsageProvider.Claude);
        bool codexDegraded = _statusMonitor.BadgeDegraded(UsageProvider.Codex);

        string claudeNoData = _noDataText ?? NoDataReason.Default;

        if (_iconFive is not null)
            Apply(_iconFive, '5', choice, choice.Snapshot?.FiveHour, "5h", UsageValues.FiveHourPeriod,
                clockwise: true, claudeDegraded, now, UsageProvider.Claude, BadgeShape.Disc, claudeNoData);
        if (_iconSeven is not null)
            Apply(_iconSeven, '7', choice, choice.Snapshot?.SevenDay, "7d", UsageValues.SevenDayPeriod,
                clockwise: false, claudeDegraded, now, UsageProvider.Claude, BadgeShape.Disc, claudeNoData);

        // Codex tooltips are prefixed, because four badges in a tray need to say which account they
        // are about — and they carry OpenAI's outage suffix, never Claude's.
        if (_codexIconFive is not null)
            Apply(_codexIconFive, '5', codexChoice, codexChoice.Snapshot?.FiveHour, "Codex 5h",
                UsageValues.FiveHourPeriod, clockwise: true, codexDegraded, now,
                UsageProvider.Codex, BadgeShape.RoundedSquare, _codexFetchStatus);
        if (_codexIconSeven is not null)
            Apply(_codexIconSeven, '7', codexChoice, codexChoice.Snapshot?.SevenDay, "Codex 7d",
                UsageValues.SevenDayPeriod, clockwise: false, codexDegraded, now,
                UsageProvider.Codex, BadgeShape.RoundedSquare, _codexFetchStatus);

        _updatedItem.Text = _settingsSaveFailed
            ? "Settings could not be saved"
            : choice.Snapshot is null
                ? "No usage data"
                : $"Updated {RelativeTime.Ago(choice.Snapshot.FetchedAt, now)}";

        _restartToUpdateItem.Enabled = UpdateCheck.IsUpdateReady;
    }

    /// <summary>The single notification call site. Runs on every Render — startup, the 30 s tick, the
    /// watcher debounce, both fetch completions, a settings save, a manual refresh. The 30 s tick is
    /// required, not merely tolerated: pace severity moves with the clock, so a value crossing into
    /// red because the window elapsed is a real crossing, and the rules' hysteresis is what stops the
    /// same clock from flapping it.</summary>
    private void Notify(DisplayChoice choice, DisplayChoice codexChoice, DateTimeOffset now)
    {
        var usage = _usageNotifications.OnUsage(choice, _settings, now);
        foreach (var line in usage.Log) _log.Write(now, line);
        if (usage.RemoveUsageToast) _toasts.Remove(_usageNotifications.Tag);
        if (usage.Notification is { } toast) _toasts.Show(toast);

        if (_codexAvailability.State == CodexState.Available)
        {
            var codex = _codexNotifications.OnUsage(codexChoice, _settings, now);
            foreach (var line in codex.Log) _log.Write(now, $"codex: {line}");
            if (codex.RemoveUsageToast) _toasts.Remove(_codexNotifications.Tag);
            if (codex.Notification is { } codexToast) _toasts.Show(codexToast);
        }

        foreach (var outcome in _statusNotifications.OnStatus(_statusMonitor.Sources(), _settings))
        {
            foreach (var line in outcome.Log) _log.Write(now, line);
            if (outcome.Notification is { } status) _toasts.Show(status);
        }
    }

    private void Apply(NotifyIcon icon, char digit, DisplayChoice choice, WindowUsage? usage, string label,
        TimeSpan period, bool clockwise, bool degraded, DateTimeOffset now,
        UsageProvider provider, BadgeShape shape, string noDataFallback)
    {
        int size = IconRenderer.SystemTrayIconSize();
        var old = icon.Icon;

        if (usage is null)
        {
            icon.Icon = IconRenderer.RenderNeutral(size, warning: degraded, shape: shape);
            // A snapshot can exist and still be missing just this window -- that is a per-window
            // gap, not the general no-data sentence. The general sentence is the caller's, because
            // Claude's reason ("no credentials file") is not Codex's.
            string noDataText = choice.Snapshot is not null ? $"{label}: no data" : noDataFallback;
            icon.Text = WithStatus(noDataText, now, provider);
        }
        else
        {
            var elapsed = TimeMarker.ElapsedFraction(ResetPresentation.EffectiveResetsAt(usage, now), period, now);
            var severity = UsageValues.WindowSeverity(usage, period, _settings, now);
            icon.Icon = IconRenderer.Render(digit, usage.Percent, severity, clockwise,
                dimmed: choice.Stale, size, warning: degraded, shape: shape);
            var tooltip = UsageTooltip.Build(label, usage, elapsed, _settings.PaceColors,
                _settings.Thresholds.Red, choice.Stale, choice.Snapshot?.Source, choice.Snapshot?.FetchedAt, now);
            icon.Text = WithStatus(tooltip, now, provider);
        }
        old?.Dispose();
    }

    /// <summary>One line per adopted desktop sample. Percentages and age only.</summary>
    private void LogDesktopSample(DateTimeOffset now)
    {
        if (_desktopSnapshot is not { } s) return;
        string five = s.FiveHour?.Percent.ToString() ?? "-";
        string seven = s.SevenDay?.Percent.ToString() ?? "-";
        _log.Write(now, $"desktop: adopted 5h={five}% 7d={seven}% updated {RelativeTime.Ago(s.FetchedAt, now)}");
    }

    /// <summary>One line whenever the displayed source changes, so a "shows the wrong numbers" report
    /// can be traced to which file they came from.</summary>
    private void LogSourceChange(DisplayChoice choice, DateTimeOffset now)
    {
        var source = choice.Snapshot?.Source;
        if (source == _lastLoggedSource) return;
        _lastLoggedSource = source;
        if (source is null) _log.Write(now, "source: none");
        else if (source == UsageSource.DesktopHistory)
        {
            var cli = _cliSnapshot is null ? "absent" : $"stale, updated {RelativeTime.Ago(_cliSnapshot.FetchedAt, now)}";
            _log.Write(now, $"source: desktop history (claude code {cli})");
        }
        else _log.Write(now, "source: claude code");
    }

    private static string TrimTooltip(string text)
        => text.Length <= 127 ? text : text[..126] + "…"; // NotifyIcon.Text hard limit

    /// <summary>The usage tooltip plus every relevant disruption, composed in Core so the badge-raising
    /// suffix is reserved before anything is cut (StatusDetail.ComposeTooltip). TrimTooltip stays as
    /// the last-resort guard for the NotifyIcon limit; after ComposeTooltip it should never fire.</summary>
    private string WithStatus(string text, DateTimeOffset now, UsageProvider provider)
        => TrimTooltip(StatusDetail.ComposeTooltip(text, _statusMonitor.Sources(), now, _settings.StalenessMinutes, provider));

    // ---- display mode / icons ----

    private void ApplyDisplayMode()
    {
        var claude = IconPresence.ForClaude(_settings.DisplayMode);
        var codex = IconPresence.ForCodex(_codexAvailability.State, _settings.CodexDisplayMode);

        // Best effort only: Windows decides notification-area order and overflow placement. That is
        // also why the provider is encoded in the badge's silhouette rather than in its position.
        if (!claude.Five) DisposeNotifyIcon(ref _iconFive);
        if (!claude.Seven) DisposeNotifyIcon(ref _iconSeven);
        if (!codex.Five) DisposeNotifyIcon(ref _codexIconFive);
        if (!codex.Seven) DisposeNotifyIcon(ref _codexIconSeven);

        if (claude.Five && _iconFive is null) _iconFive = CreateIcon();
        if (claude.Seven && _iconSeven is null) _iconSeven = CreateIcon();
        if (codex.Five && _codexIconFive is null) _codexIconFive = CreateIcon();
        if (codex.Seven && _codexIconSeven is null) _codexIconSeven = CreateIcon();
    }

    private NotifyIcon CreateIcon()
    {
        var icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPopup(); };
        return icon;
    }

    /// <summary>Left-click popup with both windows, countdowns, and last-updated line.</summary>
    public void ShowPopup()
    {
        if (_popup is { IsDisposed: false }) { _popup.Close(); }
        var now = DateTimeOffset.UtcNow;
        _popup = new UsagePopup(SourceSelection.Choose(_cliSnapshot, _desktopSnapshot, now, _settings),
            _settings, now, _statusMonitor.Sources(), _lastFetchStatus, _noDataText,
            codex: _codexAvailability.State == CodexState.Available
                ? CodexDisplay.Choose(_codexSnapshot, now, _settings, _codexFetchFailed) : null,
            codexFetchStatus: _codexFetchStatus);
        _popup.Show();
        _popup.Activate();
    }

    // ---- menu ----

    private ContextMenuStrip BuildMenu()
    {
        _updatedItem = new ToolStripMenuItem("Updated —") { Enabled = false };

        // Disabled until a staged update is downloaded (see Render()). ApplyUpdatesAndRestart
        // terminates the process, so all NotifyIcons must be disposed first to avoid ghost icons.
        _restartToUpdateItem = new ToolStripMenuItem("Restart to update", null, (_, _) =>
        {
            DisposeNotifyIcon(ref _iconFive);
            DisposeNotifyIcon(ref _iconSeven);
            DisposeNotifyIcon(ref _codexIconFive);
            DisposeNotifyIcon(ref _codexIconSeven);
            try { UpdateCheck.RestartToApply(); }
            catch { /* Velopack apply failed; recover below */ }
            // Still running means the apply did not restart the process (no-op on stale
            // readiness, or a failed apply) — recreate the icons so the app stays reachable.
            ApplyDisplayMode();
            Render();
        }) { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Refresh now", null, (_, _) => { Refresh(); StartApiFetch(); StartCodexFetch(); StartStatusFetch(); }));
        menu.Items.Add(_restartToUpdateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_updatedItem);
        // Dispose the NotifyIcons before ending the message loop — a process that exits
        // with visible icons leaves ghost entries in the tray until the user mouses over them.
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) =>
        {
            DisposeNotifyIcon(ref _iconFive);
            DisposeNotifyIcon(ref _iconSeven);
            DisposeNotifyIcon(ref _codexIconFive);
            DisposeNotifyIcon(ref _codexIconSeven);
            ExitThread();
        }));
        return menu;
    }

    // ---- settings ----

    /// <summary>Modeless and single-instance: a second click re-activates the open window rather than
    /// stacking another one, and the tray menu stays usable while it is up.</summary>
    private void ShowSettings()
    {
        if (_settingsDialog is { IsDisposed: false })
        {
            if (_settingsDialog.WindowState == FormWindowState.Minimized)
                _settingsDialog.WindowState = FormWindowState.Normal;
            _settingsDialog.Activate();
            return;
        }
        _settingsDialog = new SettingsDialog(_settings, _isVelopackInstalled, TryIsStartupEnabled(),
            ApplySettings, BuildUpdateOptions(),
            SourceSelection.Choose(_cliSnapshot, _desktopSnapshot, DateTimeOffset.UtcNow, _settings)
                .Snapshot?.Source == UsageSource.DesktopHistory,
            _componentNames, codexState: _codexAvailability.State);
        _settingsDialog.FormClosed += (_, _) => _settingsDialog = null;
        _settingsDialog.Show();
        _settingsDialog.Activate();
    }

    /// <summary>What the dialog shows and does about updates. The staged-update state comes from the
    /// background loop, so a check that already ran is visible without pressing anything.</summary>
    private UpdateOptions BuildUpdateOptions() => new(
        InstalledVersion: UpdateCheck.InstalledVersion,
        IsInstalled: _isVelopackInstalled,
        InitialState: !_isVelopackInstalled ? UpdateAvailability.NotInstalled
            : UpdateCheck.IsUpdateReady ? UpdateAvailability.UpdateReady
            : UpdateAvailability.Unknown,
        LatestVersion: UpdateCheck.LatestKnownVersion,
        InitialReleaseNotes: UpdateCheck.LatestKnownReleaseNotes,
        CheckNow: UpdateCheck.CheckNowAsync,
        // With notes, the question comes with what is changing; without them there is nothing to show
        // beyond the question itself, so a plain prompt is the honest fallback.
        Confirm: (question, notes) => notes is { Length: > 0 }
            ? ReleaseNotesDialog.Confirm(_settingsDialog, AppInfo.Window("Update"), question, notes)
            : MessageBox.Show(_settingsDialog, question, AppInfo.Window("Update"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes,
        RestartToApply: () =>
        {
            // ApplyUpdatesAndRestart terminates the process, so all NotifyIcons must go first or the
            // tray keeps ghost entries until the user mouses over them.
            DisposeNotifyIcon(ref _iconFive);
            DisposeNotifyIcon(ref _iconSeven);
            DisposeNotifyIcon(ref _codexIconFive);
            DisposeNotifyIcon(ref _codexIconSeven);
            try { UpdateCheck.RestartToApply(); }
            catch { /* apply failed; recover below */ }
            // Still here means the apply did not restart us. Put the icons back so the app stays
            // reachable, exactly as the menu item does.
            ApplyDisplayMode();
            Render();
        });

    /// <summary>Copies an edited draft onto the live settings and repaints everything at once, so the
    /// badges and any open popup follow immediately with no restart. Returns false when persisting
    /// failed, which the dialog reports instead of closing.</summary>
    private bool ApplySettings(Settings edited)
    {
        // Startup registration is the one setting that lives outside the file, so it is applied here
        // rather than on every keystroke — Cancel must leave the registry untouched too.
        if (_isVelopackInstalled && edited.RunAtStartup != TryIsStartupEnabled())
        {
            try
            {
                if (edited.RunAtStartup) StartupRegistration.Enable();
                else StartupRegistration.Disable();
            }
            catch
            {
                // Best-effort (e.g. GPO-locked HKCU). Never record a preference we failed to apply:
                // re-read the actual state so the saved value cannot lie about the registry.
            }
            edited.RunAtStartup = TryIsStartupEnabled();
        }

        _settings.DisplayMode = edited.DisplayMode;
        _settings.CodexDisplayMode = edited.CodexDisplayMode;
        _settings.CodexNotifications = edited.CodexNotifications;
        _settings.Thresholds = edited.Thresholds;
        _settings.StalenessMinutes = edited.StalenessMinutes;
        _settings.DesktopStalenessHours = edited.DesktopStalenessHours;
        _settings.PaceColors = edited.PaceColors;
        _settings.RunAtStartup = edited.RunAtStartup;
        _settings.UseBetaReleases = edited.UseBetaReleases;
        _settings.StatusSources = edited.StatusSources;
        _settings.UsageNotifications = edited.UsageNotifications;

        // Takes effect on the next check rather than at the next launch. A no-op when unchanged, so
        // saving unrelated edits never discards a staged update.
        UpdateCheck.UseRing(_settings.UseBetaReleases);

        PersistSettings();
        ApplyDisplayMode();
        _statusMonitor.ApplyEnabled(_settings.EnabledSources(_codexAvailability.State));
        StartStatusFetch();
        Refresh();
        // The popup closes when it loses focus, so it is normally already gone by the time Save is
        // clicked; rebuild it only if it somehow survived, so it cannot keep showing the old colours.
        if (_popup is { IsDisposed: false, Visible: true }) ShowPopup();
        return !_settingsSaveFailed;
    }

    private void PersistSettings()
    {
        try
        {
            _settings.Save(_settingsPath);
            _settingsSaveFailed = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _settingsSaveFailed = true;
        }
    }

    private static void DisposeNotifyIcon(ref NotifyIcon? icon)
    {
        if (icon is null) return;
        var rendered = icon.Icon;
        icon.Dispose();
        rendered?.Dispose();
        icon = null;
    }

    /// <summary>Registry access can be GPO-locked; a failed read must never crash the tray.</summary>
    private static bool TryIsStartupEnabled()
    {
        try { return StartupRegistration.IsEnabled(); }
        catch { return false; }
    }

    // ---- teardown ----

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeNotifyIcon(ref _iconFive);
            DisposeNotifyIcon(ref _iconSeven);
            DisposeNotifyIcon(ref _codexIconFive);
            DisposeNotifyIcon(ref _codexIconSeven);
            _settingsDialog?.Dispose();
            _watcher?.Dispose();
            _debounce.Dispose();
            _retry.Dispose();
            _tick.Dispose();
            _poll.Dispose();
            _statusPoll.Dispose();
            _menu.Dispose();
            _toasts.Dispose();
            _sync.Dispose();
        }
        base.Dispose(disposing);
    }
}
