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

    // Platform status polling: 60 s steady state (StatusPage's recommended cadence); all
    // budget/backoff state lives in the unit-tested StatusScheduler. Single-flight via
    // _statusInFlight (UI thread only). Fully independent of the usage path: a status failure
    // can never null, clobber, or delay usage data.
    private readonly System.Windows.Forms.Timer _statusPoll = new() { Interval = 60_000 };
    private readonly StatusScheduler _statusScheduler = new();
    private bool _statusInFlight;
    private PlatformStatus? _status;

    private readonly ContextMenuStrip _menu;
    private ToolStripMenuItem _updatedItem = null!, _restartToUpdateItem = null!;

    private NotifyIcon? _iconFive;
    private NotifyIcon? _iconSeven;
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
        _poll.Tick += (_, _) => StartApiFetch();
        _poll.Start();
        _statusPoll.Tick += (_, _) => StartStatusFetch();
        _statusPoll.Start();

        ApplyDisplayMode();
        Refresh();
        StartApiFetch();
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

        var desktop = DesktopUsageReader.ReadFirst(DesktopHistoryPath.ByFreshness(
            DesktopHistoryPath.Candidates(_settings.DesktopHistoryPathOverride,
                DesktopHistoryPath.DefaultAppData, DesktopHistoryPath.DefaultLocalAppData)), now);
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
        }
        else if (result.Unauthorized)
        {
            // Claude Code owns the credentials; wait for it to refresh them rather than retrying.
            _rejectedToken = token;
            _lastFetchStatus = "token rejected (401/403)";
            _log.Write(now, "401/403 unauthorized: token rejected; waiting for Claude Code to refresh it");
        }
        else if (result.RateLimited)
        {
            _fetchScheduler.RecordRateLimited(now, result.RetryAfter);
            string ra = result.RetryAfter is { } r ? $"{(int)r.TotalSeconds}s" : "none";
            _lastFetchStatus = "rate-limited (429)";
            _log.Write(now, $"429 rate-limited: retry-after={ra}; backing off max(Retry-After, 60s), bounded by 20/h cap");
        }
        else
        {
            _fetchScheduler.RecordFailure(now);
            _lastFetchStatus = "network/other error";
            _log.Write(now, "network/other error: no response; escalating 5/10/20 min backoff");
        }
        Render();
    }

    // ---- platform status polling ----

    private void StartStatusFetch()
    {
        var now = DateTimeOffset.UtcNow;
        if (_statusInFlight) return; // transient; not worth logging on every timer tick
        if (!_statusScheduler.CanFetch(now)) { _log.Write(now, "status: skip: budget/backoff gate not open yet"); return; }

        _statusInFlight = true;
        _statusScheduler.RecordAttempt(now);
        _log.Write(now, "status: attempt: GET summary.json");
        _ = Task.Run(async () =>
        {
            var result = await PlatformStatusApi.FetchAsync(Http, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
            try { _sync.BeginInvoke((Action)(() => OnStatusFetchCompleted(result))); }
            catch (InvalidOperationException) { /* app shutting down */ }
        });
    }

    private void OnStatusFetchCompleted(PlatformStatus? result)
    {
        _statusInFlight = false;
        var now = DateTimeOffset.UtcNow;
        if (result is null)
        {
            // Keep the last-known-good state: a dead endpoint degrades to stale, never to blank.
            _statusScheduler.RecordFailure(now);
            _log.Write(now, "status: error: no usable response; backing off");
        }
        else
        {
            _statusScheduler.RecordSuccess();
            _status = result;
            if (result.Degraded)
            {
                string names = string.Join(", ", result.Incidents.Select(i => i.Name));
                _log.Write(now, $"status: degraded: indicator={result.Indicator} incidents={result.Incidents.Count}: {names}");
            }
            else
            {
                _log.Write(now, $"status: ok: indicator={result.Indicator} ({result.Description}) incidents={result.Incidents.Count}");
            }
        }
        Render();
    }

    // ---- rendering ----

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        var choice = SourceSelection.Choose(_cliSnapshot, _desktopSnapshot, now, _settings);
        LogSourceChange(choice, now);
        bool degraded = _status is { Degraded: true };
        // A real outage must not vanish because *our* network is down: the state keeps being
        // displayed once fetched, only marked stale.
        bool statusStale = _status is not null
            && now - _status.FetchedAt > TimeSpan.FromMinutes(_settings.StalenessMinutes);

        if (_iconFive is not null)
            Apply(_iconFive, '5', choice, choice.Snapshot?.FiveHour, "5h", TimeSpan.FromHours(5),
                clockwise: true, degraded, statusStale, now);
        if (_iconSeven is not null)
            Apply(_iconSeven, '7', choice, choice.Snapshot?.SevenDay, "7d", TimeSpan.FromDays(7),
                clockwise: false, degraded, statusStale, now);

        _updatedItem.Text = _settingsSaveFailed
            ? "Settings could not be saved"
            : choice.Snapshot is null
                ? "No usage data"
                : $"Updated {RelativeTime.Ago(choice.Snapshot.FetchedAt, now)}";

        _restartToUpdateItem.Enabled = UpdateCheck.IsUpdateReady;
    }

    private void Apply(NotifyIcon icon, char digit, DisplayChoice choice, WindowUsage? usage, string label,
        TimeSpan period, bool clockwise, bool degraded, bool statusStale, DateTimeOffset now)
    {
        int size = IconRenderer.SystemTrayIconSize();
        var old = icon.Icon;

        if (usage is null)
        {
            icon.Icon = IconRenderer.RenderNeutral(size, warning: degraded);
            // A snapshot can exist and still be missing just this window (e.g. a desktop sample
            // without fh) -- that is a per-window gap, not the general no-data sentence.
            string noDataText = choice.Snapshot is not null
                ? $"{label}: no data"
                : _noDataText ?? NoDataReason.Default;
            icon.Text = TrimTooltip(noDataText + StatusSuffix(statusStale));
        }
        else
        {
            // No hysteresis: fetches are minutes apart and the ratio only moves fast early in a
            // period, which SeverityRules' dead zone already keeps out of the badge.
            var elapsed = TimeMarker.ElapsedFraction(usage.ResetsAt, period, now);
            var severity = _settings.PaceColors
                ? SeverityRules.ForPace(usage.Percent, elapsed, _settings.Thresholds.Orange, _settings.Thresholds.Red)
                : SeverityRules.For(usage.Percent, _settings.Thresholds.Orange, _settings.Thresholds.Red);
            icon.Icon = IconRenderer.Render(digit, usage.Percent, severity, clockwise,
                dimmed: choice.Stale, size, warning: degraded);
            icon.Text = TrimTooltip(BuildTooltip(label, usage, elapsed, choice, now) + StatusSuffix(statusStale));
        }
        old?.Dispose();
    }

    private string BuildTooltip(string label, WindowUsage usage, double? elapsedFraction, DisplayChoice choice,
        DateTimeOffset now)
    {
        var parts = new List<string> { label, $"{usage.Percent}%" };
        // Only when pace decided the colour — otherwise the badge means percent and needs no gloss.
        if (_settings.PaceColors
            && PaceFormat.Describe(SeverityRules.PaceRatio(
                usage.Percent, elapsedFraction, _settings.Thresholds.Red)) is { Length: > 0 } pace)
            parts.Add(pace);
        if (usage.ResetsAt is { } resetsAt)
        {
            parts.Add($"resets in {RelativeTime.In(resetsAt, now)}");
            if (choice.Stale && resetsAt <= now) parts.Add("awaiting refresh"); // cached % may be the prior window
        }
        if (choice.Snapshot is { } snapshot)
        {
            bool desktop = snapshot.Source == UsageSource.DesktopHistory;
            if (choice.Stale)
                parts.Add($"stale · {(desktop ? "Claude Desktop history · " : "")}updated {RelativeTime.Ago(snapshot.FetchedAt, now)}");
            else if (desktop)
                parts.Add($"Claude Desktop history · updated {RelativeTime.Ago(snapshot.FetchedAt, now)}");
        }
        return string.Join(" · ", parts);
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

    /// <summary>The disruption names itself in the tooltip; normal operation and a never-fetched
    /// status stay unobtrusive.</summary>
    private string StatusSuffix(bool statusStale)
    {
        if (_status is not { Degraded: true } status) return "";
        var text = string.IsNullOrWhiteSpace(status.Description) ? status.Indicator : status.Description;
        return $" · Claude: {text}{(statusStale ? " (stale)" : "")}";
    }

    // ---- display mode / icons ----

    private void ApplyDisplayMode()
    {
        bool wantFive = _settings.DisplayMode is DisplayMode.FiveHour or DisplayMode.Both;
        bool wantSeven = _settings.DisplayMode is DisplayMode.SevenDay or DisplayMode.Both;

        // Best effort only: Windows decides notification-area order and overflow placement.
        if (!wantFive) DisposeNotifyIcon(ref _iconFive);
        if (!wantSeven) DisposeNotifyIcon(ref _iconSeven);
        if (wantFive && _iconFive is null) _iconFive = CreateIcon();
        if (wantSeven && _iconSeven is null) _iconSeven = CreateIcon();
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
            _settings, now, _status, _lastFetchStatus, _noDataText);
        _popup.Show();
        _popup.Activate();
    }

    // ---- menu ----

    private ContextMenuStrip BuildMenu()
    {
        _updatedItem = new ToolStripMenuItem("Updated —") { Enabled = false };

        // Disabled until a staged update is downloaded (see Render()). ApplyUpdatesAndRestart
        // terminates the process, so both NotifyIcons must be disposed first to avoid ghost icons.
        _restartToUpdateItem = new ToolStripMenuItem("Restart to update", null, (_, _) =>
        {
            DisposeNotifyIcon(ref _iconFive);
            DisposeNotifyIcon(ref _iconSeven);
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
        menu.Items.Add(new ToolStripMenuItem("Refresh now", null, (_, _) => { Refresh(); StartApiFetch(); StartStatusFetch(); }));
        menu.Items.Add(_restartToUpdateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_updatedItem);
        // Dispose the NotifyIcons before ending the message loop — a process that exits
        // with visible icons leaves ghost entries in the tray until the user mouses over them.
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) =>
        {
            DisposeNotifyIcon(ref _iconFive);
            DisposeNotifyIcon(ref _iconSeven);
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
            ApplySettings, BuildUpdateOptions());
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
            // ApplyUpdatesAndRestart terminates the process, so both NotifyIcons must go first or the
            // tray keeps ghost entries until the user mouses over them.
            DisposeNotifyIcon(ref _iconFive);
            DisposeNotifyIcon(ref _iconSeven);
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
        _settings.Thresholds = edited.Thresholds;
        _settings.StalenessMinutes = edited.StalenessMinutes;
        _settings.DesktopStalenessHours = edited.DesktopStalenessHours;
        _settings.PaceColors = edited.PaceColors;
        _settings.RunAtStartup = edited.RunAtStartup;
        _settings.UseBetaReleases = edited.UseBetaReleases;

        // Takes effect on the next check rather than at the next launch. A no-op when unchanged, so
        // saving unrelated edits never discards a staged update.
        UpdateCheck.UseRing(_settings.UseBetaReleases);

        PersistSettings();
        ApplyDisplayMode();
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
            _settingsDialog?.Dispose();
            _watcher?.Dispose();
            _debounce.Dispose();
            _retry.Dispose();
            _tick.Dispose();
            _poll.Dispose();
            _statusPoll.Dispose();
            _menu.Dispose();
            _sync.Dispose();
        }
        base.Dispose(disposing);
    }
}
