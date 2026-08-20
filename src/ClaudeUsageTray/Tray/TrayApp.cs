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

    private readonly ContextMenuStrip _menu;
    private ToolStripMenuItem _modeFive = null!, _modeSeven = null!, _modeBoth = null!;
    private ToolStripMenuItem _startupItem = null!, _updatedItem = null!, _restartToUpdateItem = null!;

    private NotifyIcon? _iconFive;
    private NotifyIcon? _iconSeven;
    private UsageSnapshot? _snapshot;
    private bool _settingsSaveFailed;
    private UsagePopup? _popup;

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

        ApplyDisplayMode();
        Refresh();
        StartApiFetch();
    }

    // ---- data ----

    private void Refresh()
    {
        var read = UsageCacheReader.TryRead(_configPath);
        if (read is not null)
        {
            if (SnapshotPrecedence.IsNewer(read, _snapshot)) _snapshot = read;
            _consecutiveReadFailures = 0;
        }
        else if (!File.Exists(_configPath))
        {
            if (_snapshot is null || DateTimeOffset.UtcNow - _snapshot.FetchedAt
                > TimeSpan.FromMinutes(_settings.StalenessMinutes))
            {
                _snapshot = null;
            }
            _consecutiveReadFailures = 0;
        }
        else if (_snapshot is null || ++_consecutiveReadFailures >= 3)
        {
            if (_snapshot is null || DateTimeOffset.UtcNow - _snapshot.FetchedAt
                > TimeSpan.FromMinutes(_settings.StalenessMinutes))
            {
                _snapshot = null;
            }
            _consecutiveReadFailures = 0;
        }
        else
        {
            _retry.Stop();
            _retry.Start(); // likely a partial replace; preserve the last known good snapshot briefly
        }
        Render();
    }

    // ---- live usage polling ----

    private void StartApiFetch()
    {
        var now = DateTimeOffset.UtcNow;
        if (_fetchInFlight) return; // transient; not worth logging on every timer tick
        if (!_fetchScheduler.CanFetch(now)) { _log.Write(now, "skip: budget/backoff gate not open yet"); return; }
        var token = CredentialsReader.TryReadAccessToken(CredentialsReader.DefaultPath, now);
        if (token is null) { _log.Write(now, "skip: no valid access token (missing/expired/near-expiry)"); return; }
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
            bool adopted = SnapshotPrecedence.IsNewer(result.Snapshot, _snapshot);
            if (adopted)
            {
                _snapshot = result.Snapshot;
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

    // ---- rendering ----

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        bool stale = _snapshot is not null
            && now - _snapshot.FetchedAt > TimeSpan.FromMinutes(_settings.StalenessMinutes);

        if (_iconFive is not null)
            Apply(_iconFive, '5', _snapshot?.FiveHour, "5h", TimeSpan.FromHours(5), clockwise: true, stale, now);
        if (_iconSeven is not null)
            Apply(_iconSeven, '7', _snapshot?.SevenDay, "7d", TimeSpan.FromDays(7), clockwise: false, stale, now);

        _updatedItem.Text = _settingsSaveFailed
            ? "Settings could not be saved"
            : _snapshot is null
                ? "No usage data"
                : $"Updated {RelativeTime.Ago(_snapshot.FetchedAt, now)}";

        _restartToUpdateItem.Enabled = UpdateCheck.IsUpdateReady;
    }

    private void Apply(NotifyIcon icon, char digit, WindowUsage? usage, string label, TimeSpan period,
        bool clockwise, bool stale, DateTimeOffset now)
    {
        int size = IconRenderer.SystemTrayIconSize();
        var old = icon.Icon;

        if (usage is null)
        {
            icon.Icon = IconRenderer.RenderNeutral(size);
            icon.Text = "No Claude usage data yet — run Claude Code.";
        }
        else
        {
            // No hysteresis: fetches are minutes apart and the ratio only moves fast early in a
            // period, which SeverityRules' dead zone already keeps out of the badge.
            var elapsed = TimeMarker.ElapsedFraction(usage.ResetsAt, period, now);
            var severity = _settings.PaceColors
                ? SeverityRules.ForPace(usage.Percent, elapsed, _settings.Thresholds.Orange, _settings.Thresholds.Red)
                : SeverityRules.For(usage.Percent, _settings.Thresholds.Orange, _settings.Thresholds.Red);
            icon.Icon = IconRenderer.Render(digit, usage.Percent, severity, clockwise, dimmed: stale, size);
            icon.Text = TrimTooltip(BuildTooltip(label, usage, elapsed, stale, now));
        }
        old?.Dispose();
    }

    private string BuildTooltip(string label, WindowUsage usage, double? elapsedFraction, bool stale,
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
            if (stale && resetsAt <= now) parts.Add("awaiting refresh"); // cached % may be the prior window
        }
        if (stale && _snapshot is not null)
            parts.Add($"stale · updated {RelativeTime.Ago(_snapshot.FetchedAt, now)}");
        return string.Join(" · ", parts);
    }

    private static string TrimTooltip(string text)
        => text.Length <= 127 ? text : text[..126] + "…"; // NotifyIcon.Text hard limit

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

        _modeFive.Checked = _settings.DisplayMode == DisplayMode.FiveHour;
        _modeSeven.Checked = _settings.DisplayMode == DisplayMode.SevenDay;
        _modeBoth.Checked = _settings.DisplayMode == DisplayMode.Both;
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
        _popup = new UsagePopup(_snapshot, _settings, DateTimeOffset.UtcNow, _lastFetchStatus);
        _popup.Show();
        _popup.Activate();
    }

    // ---- menu ----

    private ContextMenuStrip BuildMenu()
    {
        _modeFive = new ToolStripMenuItem("Show 5h", null, (_, _) => SetDisplayMode(DisplayMode.FiveHour)) { CheckOnClick = true };
        _modeSeven = new ToolStripMenuItem("Show 7d", null, (_, _) => SetDisplayMode(DisplayMode.SevenDay)) { CheckOnClick = true };
        _modeBoth = new ToolStripMenuItem("Show both", null, (_, _) => SetDisplayMode(DisplayMode.Both)) { CheckOnClick = true };

        _startupItem = new ToolStripMenuItem("Run at startup")
        {
            Checked = _isVelopackInstalled && TryIsStartupEnabled(),
            Enabled = _isVelopackInstalled,
            ToolTipText = _isVelopackInstalled ? "" : "Available only in the installed app.",
        };
        _startupItem.Click += (_, _) =>
        {
            try
            {
                if (StartupRegistration.IsEnabled()) StartupRegistration.Disable();
                else StartupRegistration.Enable();
                _startupItem.Checked = StartupRegistration.IsEnabled();
                _settings.RunAtStartup = _startupItem.Checked;
                PersistSettings();
            }
            catch
            {
                // Startup registration is best-effort (e.g. GPO-locked HKCU). The checkbox must
                // never lie about a state it failed to change, so re-read the actual state.
                _startupItem.Checked = TryIsStartupEnabled();
            }
            Render();
        };

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
        menu.Items.Add(_modeFive);
        menu.Items.Add(_modeSeven);
        menu.Items.Add(_modeBoth);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripMenuItem("Refresh now", null, (_, _) => { Refresh(); StartApiFetch(); }));
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

    private void SetDisplayMode(DisplayMode mode)
    {
        _settings.DisplayMode = mode;
        PersistSettings();
        ApplyDisplayMode();
        Render();
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
            _watcher?.Dispose();
            _debounce.Dispose();
            _retry.Dispose();
            _tick.Dispose();
            _poll.Dispose();
            _menu.Dispose();
            _sync.Dispose();
        }
        base.Dispose(disposing);
    }
}
