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

        ApplyDisplayMode();
        Refresh();
    }

    // ---- data ----

    private void Refresh()
    {
        var read = UsageCacheReader.TryRead(_configPath);
        if (read is not null)
        {
            _snapshot = read;
            _consecutiveReadFailures = 0;
        }
        else if (!File.Exists(_configPath))
        {
            _snapshot = null;
            _consecutiveReadFailures = 0;
        }
        else if (_snapshot is null || ++_consecutiveReadFailures >= 3)
        {
            _snapshot = null; // permanently malformed/unavailable cache: show neutral state
            _consecutiveReadFailures = 0;
        }
        else
        {
            _retry.Stop();
            _retry.Start(); // likely a partial replace; preserve the last known good snapshot briefly
        }
        Render();
    }

    // ---- rendering ----

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        bool stale = _snapshot is not null
            && now - _snapshot.FetchedAt > TimeSpan.FromMinutes(_settings.StalenessMinutes);

        if (_iconFive is not null) Apply(_iconFive, '5', _snapshot?.FiveHour, "5h", clockwise: true, stale, now);
        if (_iconSeven is not null) Apply(_iconSeven, '7', _snapshot?.SevenDay, "7d", clockwise: false, stale, now);

        _updatedItem.Text = _settingsSaveFailed
            ? "Settings could not be saved"
            : _snapshot is null
                ? "No usage data"
                : $"Updated {RelativeTime.Ago(_snapshot.FetchedAt, now)}";

        _restartToUpdateItem.Enabled = UpdateCheck.IsUpdateReady;
    }

    private void Apply(NotifyIcon icon, char digit, WindowUsage? usage, string label, bool clockwise,
        bool stale, DateTimeOffset now)
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
            var severity = SeverityRules.For(usage.Percent, _settings.Thresholds.Orange, _settings.Thresholds.Red);
            icon.Icon = IconRenderer.Render(digit, usage.Percent, severity, clockwise, dimmed: stale, size);
            icon.Text = TrimTooltip(BuildTooltip(label, usage, stale, now));
        }
        old?.Dispose();
    }

    private string BuildTooltip(string label, WindowUsage usage, bool stale, DateTimeOffset now)
    {
        var parts = new List<string> { label, $"{usage.Percent}%" };
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
        _popup = new UsagePopup(_snapshot, _settings, DateTimeOffset.UtcNow);
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
            Checked = _isVelopackInstalled && StartupRegistration.IsEnabled(),
            Enabled = _isVelopackInstalled,
            ToolTipText = _isVelopackInstalled ? "" : "Available only in the installed app.",
        };
        _startupItem.Click += (_, _) =>
        {
            if (StartupRegistration.IsEnabled()) StartupRegistration.Disable();
            else StartupRegistration.Enable();
            _startupItem.Checked = StartupRegistration.IsEnabled();
            _settings.RunAtStartup = _startupItem.Checked;
            PersistSettings();
            Render();
        };

        _updatedItem = new ToolStripMenuItem("Updated —") { Enabled = false };

        // Disabled until a staged update is downloaded (see Render()). ApplyUpdatesAndRestart
        // terminates the process, so both NotifyIcons must be disposed first to avoid ghost icons.
        _restartToUpdateItem = new ToolStripMenuItem("Restart to update", null, (_, _) =>
        {
            DisposeNotifyIcon(ref _iconFive);
            DisposeNotifyIcon(ref _iconSeven);
            UpdateCheck.RestartToApply();
        }) { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_modeFive);
        menu.Items.Add(_modeSeven);
        menu.Items.Add(_modeBoth);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripMenuItem("Refresh now", null, (_, _) => Refresh()));
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
            _menu.Dispose();
            _sync.Dispose();
        }
        base.Dispose(disposing);
    }
}
