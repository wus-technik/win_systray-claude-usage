using System.Diagnostics;
using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Tray;

/// <summary>Modeless settings window: display mode, startup, colour thresholds, pace colouring and
/// staleness. Modeless rather than modal because a modal dialog with no owner window would swallow
/// clicks on the tray icon that opened it, leaving the app looking hung.
///
/// A thin shell over <see cref="ThresholdRules"/>: every value is edited on a clone, so Cancel costs
/// nothing, and the invariant is enforced by wiring the two spinners' ranges to each other rather
/// than by validating on Save — an invalid pair is unreachable, so it can never silently snap back
/// to the defaults behind the user's back.</summary>
public sealed class SettingsDialog : Form
{
    /// <summary>Sample fill for the preview. Above the pace floor and inside the default orange
    /// band, so moving either threshold across it visibly changes the colour.</summary>
    private const int PreviewPercent = 60;

    /// <summary>Sample elapsed fraction for the preview: half the period gone, which puts the sample
    /// slightly ahead of the clock and so shows a pace verdict rather than nothing.</summary>
    private const double PreviewElapsedFraction = 0.5;

    private readonly Settings _draft;
    private readonly bool _canRunAtStartup;
    private readonly Func<Settings, bool> _save;

    private readonly RadioButton _modeFive = new() { Name = "modeFive", Text = "5-hour window only", AutoSize = true };
    private readonly RadioButton _modeSeven = new() { Name = "modeSeven", Text = "7-day window only", AutoSize = true };
    private readonly RadioButton _modeBoth = new() { Name = "modeBoth", Text = "Both", AutoSize = true };
    private readonly CheckBox _startup = new() { Name = "startup", Text = "Run at startup", AutoSize = true };
    private readonly NumericUpDown _orange = new() { Name = "orange", Minimum = 0, Maximum = 99, Width = 60 };
    private readonly NumericUpDown _red = new() { Name = "red", Minimum = 1, Maximum = 100, Width = 60 };
    private readonly CheckBox _paceColors = new() { Name = "paceColors", Text = "Colour by pace (usage against time elapsed)", AutoSize = true };
    private readonly NumericUpDown _staleness = new() { Name = "staleness", Minimum = 0, Maximum = 1440, Width = 60 };
    private readonly NumericUpDown _desktopStaleness = new() { Name = "desktopStaleness", Minimum = 1, Maximum = 168, Width = 60 };
    private readonly TextBox _weeklyAnchor = new() { Name = "weeklyAnchor", Width = 120 };
    private readonly Label _weeklyAnchorError = new()
    {
        Name = "weeklyAnchorError",
        Text = "Use a weekday and a time, e.g. Thu 03:00.",
        AutoSize = true,
        ForeColor = Color.Firebrick,
        Visible = false,
    };
    private readonly bool _desktopSource;
    private readonly Panel _preview = new() { Name = "preview", Width = UsageBar.DefaultWidth, Height = UsageBar.DefaultHeight };
    private readonly Label _previewCaption = new() { Name = "previewCaption", AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _error = new() { Name = "error", AutoSize = true, ForeColor = Color.Firebrick, Visible = false };
    // A LinkLabel, so the creator doubles as the way to the project page. Still a Label as far as
    // the grid — and the tests — are concerned.
    private readonly LinkLabel _creator = new() { Name = "creator", AutoSize = true };
    private readonly Label _installedVersion = new() { Name = "installedVersion", AutoSize = true };
    private readonly Label _updateStatus = new() { Name = "updateStatus", AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Button _updateNow = new() { Name = "updateNow", Text = "Update now", AutoSize = true };
    // The glyph is the control; AccessibleName carries the meaning, since a screen reader reads
    // neither a dingbat nor a tooltip as a name.
    private readonly Button _checkUpdates = new()
    {
        Name = "checkUpdates",
        Text = "⟳",
        AccessibleName = "Check for updates",
        AutoSize = false,
        Size = new Size(26, 25),
        FlatStyle = FlatStyle.Standard,
        Font = new Font("Segoe UI Symbol", 10f),
    };
    private readonly CheckBox _betaReleases = new()
    {
        Name = "betaReleases",
        Text = "Use beta releases (pre-release builds, may be unstable)",
        AutoSize = true,
    };
    private readonly CheckBox _watchClaude = new()
        { Name = "watchClaude", Text = "Watch Claude status", AutoSize = true };
    private readonly TextBox _claudeComponents = new() { Name = "claudeComponents", Width = 240 };
    private readonly Label _claudeComponentsCaption = new()
        { Text = "Components (comma-separated, blank = all)", AutoSize = true };
    private readonly CheckBox _watchOpenAi = new()
        { Name = "watchOpenAi", Text = "Watch OpenAI status", AutoSize = true };
    private readonly TextBox _openAiComponents = new() { Name = "openAiComponents", Width = 240 };
    private readonly Label _openAiComponentsCaption = new()
        { Text = "Components (comma-separated, blank = all)", AutoSize = true };
    private readonly Label _claudeComponentsHint = new()
    {
        Name = "claudeComponentsHint",
        AutoSize = true,
        MaximumSize = new Size(320, 0),
        ForeColor = SystemColors.GrayText,
    };
    private readonly Label _openAiComponentsHint = new()
    {
        Name = "openAiComponentsHint",
        AutoSize = true,
        MaximumSize = new Size(320, 0),
        ForeColor = SystemColors.GrayText,
    };
    private readonly CheckBox _notifyUsage = new()
        { Name = "notifyUsage", Text = "Notify when a limit turns", AutoSize = true };
    private readonly ComboBox _notifyLevel = new()
        { Name = "notifyLevel", DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly CheckBox _notifyClaude = new()
        { Name = "notifyClaude", Text = "Notify when Claude platform status changes", AutoSize = true };
    private readonly CheckBox _notifyOpenAi = new()
        { Name = "notifyOpenAi", Text = "Notify when OpenAI platform status changes", AutoSize = true };
    private readonly TabControl _tabs = new() { Name = "tabs", Margin = new Padding(0, 0, 0, 4) };

    /// <summary>Combo rows in NotifyLevel order, so SelectedIndex casts straight to the enum.</summary>
    private static readonly string[] LevelLabels = ["Red only", "Orange and red"];
    private readonly UpdateOptions _updates;
    private UpdateAvailability _updateState;
    private string? _releaseNotes;
    private string? _latestVersion;
    private bool _suspendSync;

    /// <param name="settings">The live settings. Cloned immediately; never mutated by this form.</param>
    /// <param name="canRunAtStartup">False outside the installed app, where there is no launcher to register.</param>
    /// <param name="runAtStartup">Whether the launcher is actually registered, read from the registry
    /// rather than taken from <paramref name="settings"/>: a preference the registry refused is still
    /// in the file, and the checkbox must not claim a state it never reached.</param>
    /// <param name="save">Applies the edited settings, returning false if persisting them failed — the
    /// dialog then stays open and says so rather than closing on a read-only profile.</param>
    /// <param name="updates">Version and update state; see <see cref="UpdateOptions"/>.</param>
    /// <param name="desktopSource">Whether the Claude Desktop history is the active source right
    /// now. Frozen at open time on purpose: SourceSelection.Choose can flip on any 30 s tick, and a
    /// group that vanishes under an open dialog — discarding a half-typed anchor — is worse than one
    /// that is briefly out of date.</param>
    /// <param name="componentNames">Every component each page currently lists, keyed by source id —
    /// the greyed caption under each watch-filter box. Frozen at open time for the same reason
    /// <paramref name="desktopSource"/> is, and supplied from TrayApp's own cache rather than read
    /// live: StatusMonitor holds no entry for a disabled source, which is exactly the case where the
    /// user has opened this dialog to turn a page back on and narrow it. A missing or empty entry
    /// reads "not fetched yet".</param>
    public SettingsDialog(Settings settings, bool canRunAtStartup, bool runAtStartup,
        Func<Settings, bool> save, UpdateOptions updates, bool desktopSource,
        IReadOnlyDictionary<string, IReadOnlyList<string>> componentNames)
    {
        _draft = Clone(settings);
        _canRunAtStartup = canRunAtStartup;
        _save = save;
        _desktopSource = desktopSource;
        _updates = updates;
        _updateState = updates.InitialState;
        _latestVersion = updates.LatestVersion;
        _releaseNotes = updates.InitialReleaseNotes;
        _claudeComponentsHint.Text = HintFor(componentNames, StatusSourceRegistry.Claude.Id);
        _openAiComponentsHint.Text = HintFor(componentNames, StatusSourceRegistry.OpenAi.Id);

        Text = AppInfo.Window("Settings");
        Icon = AppIcon.Value;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        Controls.Add(BuildLayout());
        LoadFrom(_draft, _canRunAtStartup && runAtStartup);
        WireLiveSync();
        RefreshUpdateSection();
    }

    // ---- layout ----

    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };

        // The error label and the buttons stay outside the tabs: Save has to be reachable from every
        // page, and a failed save has to be readable from whichever page the user was on.
        layout.Controls.Add(BuildTabs());
        layout.Controls.Add(_error);
        layout.Controls.Add(BuildButtons());
        return layout;
    }

    /// <summary>Four pages grouped by what a user changes together, not by the order the sections
    /// were written in. Notifications sits with Platform status because two of its three controls are
    /// per-source status toggles; the beta ring sits with About because it steers the updater
    /// directly above it.</summary>
    private Control BuildTabs()
    {
        _tabs.TabPages.Add(Page("general", "General", BuildGeneralPage()));
        _tabs.TabPages.Add(Page("appearance", "Appearance", BuildAppearancePage()));
        _tabs.TabPages.Add(Page("status", "Status", BuildStatusPage()));
        _tabs.TabPages.Add(Page("about", "About", BuildAboutPage()));
        return _tabs;
    }

    /// <summary>A TabControl never sizes itself to its pages, so the form would otherwise inherit the
    /// designer default. Measured once the handle exists, because DisplayRectangle — the inset the tab
    /// strip and borders cost — is only meaningful then.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FitTabsToLargestPage();
    }

    /// <summary>The app is PerMonitorV2, and WinForms rescales the fixed size it was given without
    /// rescaling the pages' preferred sizes by exactly the same factor — font rounding and the hint
    /// labels' fixed wrap width both drift. Deferred, so it runs after that scaling, not during it.</summary>
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        if (IsHandleCreated) BeginInvoke(FitTabsToLargestPage);
    }

    /// <summary>Fixed at the largest page, not re-measured per tab: the height then comes from the
    /// tallest group rather than the sum of all of them, and switching tabs never resizes the window.</summary>
    private void FitTabsToLargestPage()
    {
        var content = Size.Empty;
        foreach (TabPage page in _tabs.TabPages)
        {
            var needed = page.Controls[0].PreferredSize;
            content = new Size(
                Math.Max(content.Width, needed.Width + page.Padding.Horizontal),
                Math.Max(content.Height, needed.Height + page.Padding.Vertical));
        }

        _tabs.Size = content + (_tabs.Size - _tabs.DisplayRectangle.Size);

        // Multiline is off: a control narrower than its own headers grows scroll arrows instead of
        // wrapping. The pages are the wider of the two today, but only measuring keeps that true.
        // GetTabRect needs the strip to exist, which it does not yet when the form's handle is being
        // created — realize it here rather than leaving the check to a hook that may never run.
        _tabs.CreateControl();
        if (!_tabs.IsHandleCreated) return;

        int headers = 0;
        for (int index = 0; index < _tabs.TabPages.Count; index++) headers += _tabs.GetTabRect(index).Width;
        if (headers > _tabs.Width) _tabs.Width = headers;
    }

    private static TabPage Page(string name, string text, Control content)
    {
        content.Dock = DockStyle.Fill;
        var page = new TabPage(text)
        {
            Name = name,
            Padding = new Padding(8),
            UseVisualStyleBackColor = true,
        };
        page.Controls.Add(content);
        return page;
    }

    /// <summary>An empty page body, sized to its content. The pages differ only in what goes in.</summary>
    private static TableLayoutPanel PagePanel() => new()
    {
        ColumnCount = 1,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };

    private Control BuildGeneralPage()
    {
        var page = PagePanel();

        page.Controls.Add(Heading("Tray icons"));
        page.Controls.Add(Indent(_modeFive));
        page.Controls.Add(Indent(_modeSeven));
        page.Controls.Add(Indent(_modeBoth));

        _startup.Enabled = _canRunAtStartup;
        page.Controls.Add(Indent(_startup));
        if (!_canRunAtStartup)
        {
            page.Controls.Add(Indent(new Label
            {
                Text = "Available only in the installed app.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
            }));
        }

        page.Controls.Add(Heading("Refresh"));
        page.Controls.Add(Spinners(
            ("Treat data as stale after", _staleness, "minutes"),
            ("Claude Desktop history stale after", _desktopStaleness, "hours")));

        // Only while the desktop history is the live source: a Claude Code user puzzling over a
        // setting that does nothing for them is the far more common outcome than the reverse.
        if (_desktopSource)
        {
            page.Controls.Add(Heading("Claude Desktop"));
            page.Controls.Add(Indent(new Label
            {
                Text = "Weekly reset (read it off Claude's own UI), e.g. Thu 03:00",
                AutoSize = true,
            }));
            page.Controls.Add(Indent(_weeklyAnchor));
            page.Controls.Add(Indent(_weeklyAnchorError));
        }

        SetOrder(_modeFive, _modeSeven, _modeBoth, _startup, _staleness, _desktopStaleness, _weeklyAnchor);
        return page;
    }

    private Control BuildAppearancePage()
    {
        var page = PagePanel();

        page.Controls.Add(Heading("Colour thresholds"));
        page.Controls.Add(Spinners(("Orange at", _orange, "%"), ("Red above", _red, "%")));
        page.Controls.Add(Indent(_paceColors));
        if (_desktopSource)
        {
            page.Controls.Add(Indent(new Label
            {
                Text = "Needs a reset time; without one the plain thresholds decide.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
            }));
        }
        page.Controls.Add(Indent(_preview));
        page.Controls.Add(Indent(_previewCaption));

        SetOrder(_orange, _red, _paceColors);
        return page;
    }

    private Control BuildStatusPage()
    {
        var page = PagePanel();

        // Both pages, each with its own watch filter. The notify checkboxes stay under
        // Notifications, where the two of them already sit together.
        page.Controls.Add(Heading("Platform status"));
        page.Controls.Add(Indent(_watchClaude));
        page.Controls.Add(Indent(_claudeComponentsCaption));
        page.Controls.Add(Indent(_claudeComponents));
        page.Controls.Add(Indent(_claudeComponentsHint));
        page.Controls.Add(Indent(_watchOpenAi));
        page.Controls.Add(Indent(_openAiComponentsCaption));
        page.Controls.Add(Indent(_openAiComponents));
        page.Controls.Add(Indent(_openAiComponentsHint));

        page.Controls.Add(Heading("Notifications"));
        _notifyLevel.Items.AddRange(LevelLabels);
        var usageRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(16, 0, 0, 2) };
        _notifyUsage.Margin = new Padding(0, 4, 4, 0);
        _notifyLevel.Margin = new Padding(0);
        usageRow.Controls.Add(_notifyUsage);
        usageRow.Controls.Add(_notifyLevel);
        SetOrder(_notifyUsage, _notifyLevel);
        page.Controls.Add(usageRow);
        page.Controls.Add(Indent(_notifyClaude));
        page.Controls.Add(Indent(_notifyOpenAi));

        // usageRow, not the two controls inside it: they sit one level down and get their own run above.
        SetOrder(_watchClaude, _claudeComponents, _watchOpenAi, _openAiComponents,
            usageRow, _notifyClaude, _notifyOpenAi);
        return page;
    }

    private Control BuildAboutPage()
    {
        var page = PagePanel();

        page.Controls.Add(Heading("About"));
        page.Controls.Add(BuildAbout());

        // Which ring the updater follows belongs next to the update controls it changes. Disabled
        // outside the installed app for the same reason those are: there is nothing to update.
        _betaReleases.Enabled = _updates.IsInstalled;
        page.Controls.Add(Indent(_betaReleases));

        SetOrder(_creator, _checkUpdates, _updateNow, _betaReleases);
        return page;
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 10, 0, 4),
    };

    private static Control Indent(Control inner)
    {
        inner.Margin = new Padding(16, inner.Margin.Top, 0, inner.Margin.Bottom);
        return inner;
    }

    /// <summary>An ascending run in reading order, over the children of **one** container. TabIndex
    /// is only ever compared among siblings, so a nested row gets its own run rather than continuing
    /// its parent's — a single run spanning both would reach the nested row last.</summary>
    private static void SetOrder(params Control[] controls)
    {
        for (int index = 0; index < controls.Length; index++) controls[index].TabIndex = index;
    }

    /// <summary>The page's own component names, as a reference caption. Never a prefill: the box
    /// shows exactly what is stored, so "blank = all" stays literally true.</summary>
    private static string HintFor(IReadOnlyDictionary<string, IReadOnlyList<string>> names, string sourceId)
        => names.TryGetValue(sourceId, out var list) && list.Count > 0
            ? "Page lists: " + string.Join(", ", list)
            : "Page lists: not fetched yet";

    /// <summary>Labelled spinners with their units trailing. Every spinner passed in one call shares
    /// a grid, so their boxes line up in a column however wide the labels are — two rows built as two
    /// separate grids would each size to their own label and land the boxes a few pixels apart.</summary>
    private static Control Spinners(params (string Label, NumericUpDown Spinner, string Unit)[] rows)
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = rows.Length,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(16, 0, 0, 2),
        };
        for (int row = 0; row < rows.Length; row++)
        {
            var (label, spinner, unit) = rows[row];
            grid.Controls.Add(new Label
            {
                Text = label, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 5, 6, 2),
            }, 0, row);
            grid.Controls.Add(spinner, 1, row);
            grid.Controls.Add(new Label
            {
                Text = unit, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(6, 5, 0, 2),
            }, 2, row);
        }
        return grid;
    }

    /// <summary>Creator, version, update state and the two controls that act on it — refresh checks,
    /// Update now installs — in a single grid so the three values line up under each other.</summary>
    private Control BuildAbout()
    {
        _installedVersion.Text = _updates.InstalledVersion;
        _creator.Text = AppInfo.CreatorForLabel;
        _creator.Click += (_, _) => OpenUrl(AppInfo.ProjectUrl);
        new ToolTip().SetToolTip(_creator, AppInfo.ProjectUrl);
        _checkUpdates.Click += async (_, _) => await CheckForUpdatesAsync();
        _updateNow.Click += (_, _) => ApplyUpdate();
        new ToolTip().SetToolTip(_checkUpdates, "Check for updates");

        var grid = new TableLayoutPanel
        {
            ColumnCount = 4,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(16, 0, 0, 2),
        };
        grid.Controls.Add(new Label { Text = "Created by", AutoSize = true, Margin = new Padding(0, 3, 8, 2) }, 0, 0);
        grid.Controls.Add(_creator, 1, 0);
        grid.Controls.Add(new Label { Text = "Installed", AutoSize = true, Margin = new Padding(0, 3, 8, 2) }, 0, 1);
        grid.Controls.Add(_installedVersion, 1, 1);
        grid.Controls.Add(new Label { Text = "Updates", AutoSize = true, Margin = new Padding(0, 3, 8, 0) }, 0, 2);
        _updateStatus.Margin = new Padding(0, 3, 8, 0);
        grid.Controls.Add(_updateStatus, 1, 2);
        grid.Controls.Add(_checkUpdates, 2, 2);
        grid.Controls.Add(_updateNow, 3, 2);
        return grid;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser, or a dead link: neither is worth taking the dialog down for */ }
    }

    /// <summary>One check, and nothing else — finding an update must not start installing one. Runs on
    /// the UI thread up to the await and back on it afterwards, so the labels are only ever touched
    /// from one thread.</summary>
    private async Task CheckForUpdatesAsync()
    {
        if (!VersionDisplay.CanCheck(_updateState, _updates.IsInstalled)) return;

        _updateState = UpdateAvailability.Checking;
        _latestVersion = null;
        _releaseNotes = null;
        RefreshUpdateSection();

        var (state, latest, notes) = await _updates.CheckNow();
        if (IsDisposed || Disposing) return; // closed while the feed was answering

        _updateState = state;
        _latestVersion = latest;
        _releaseNotes = ReleaseNotes.Format(notes);
        RefreshUpdateSection();
    }

    /// <summary>Installs what a check already found: show what is changing, then restart. Only
    /// reachable while an update is staged, so it never has to check first.</summary>
    private void ApplyUpdate()
    {
        if (!VersionDisplay.CanApply(_updateState, _updates.IsInstalled)) return;

        var question = _latestVersion is { Length: > 0 } version
            ? $"Version {version} is ready. Restart now to install it?"
            : "An update is ready. Restart now to install it?";
        if (!_updates.Confirm(question, _releaseNotes)) return; // stays staged; the menu can still apply it

        // Save first: the restart does not come back, and throwing away half-finished edits to install
        // an update would be a nasty surprise. A failed save cancels the restart rather than losing them.
        if (!Commit(closeOnSuccess: false)) return;
        _updates.RestartToApply();
    }

    private void RefreshUpdateSection()
    {
        _updateStatus.Text = VersionDisplay.Describe(_updateState, _latestVersion);
        _checkUpdates.Enabled = VersionDisplay.CanCheck(_updateState, _updates.IsInstalled);
        _updateNow.Enabled = VersionDisplay.CanApply(_updateState, _updates.IsInstalled);
        // Grey means "nothing to do here", which is wrong for something waiting to be installed.
        _updateStatus.ForeColor = _updateState switch
        {
            UpdateAvailability.UpdateReady => SystemColors.ControlText,
            UpdateAvailability.Failed => Color.Firebrick,
            _ => SystemColors.GrayText,
        };
    }

    private Control BuildButtons()
    {
        var reset = new Button { Name = "reset", Text = "Reset to defaults", AutoSize = true };
        var cancel = new Button { Name = "cancel", Text = "Cancel", AutoSize = true };
        var save = new Button { Name = "save", Text = "Save", AutoSize = true };

        // Defaults for the thresholds and staleness only: the display mode and startup preference are
        // not what "reset the colours to defaults" means, and silently unregistering the launcher
        // would be a surprising side effect of a button about colours.
        reset.Click += (_, _) => ResetThresholdsToDefaults();
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => Commit();

        // Enter and Esc come from these two, so the dialog never needs the mouse.
        AcceptButton = save;
        CancelButton = cancel;

        var row = new TableLayoutPanel
        {
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 12, 0, 0),
            Dock = DockStyle.Fill,
        };
        // Push Cancel/Save to the right; Reset stays left, away from them.
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(reset, 0, 0);
        row.Controls.Add(cancel, 1, 0);
        row.Controls.Add(save, 2, 0);

        SetOrder(reset, cancel, save);
        return row;
    }

    // ---- state ----

    private void LoadFrom(Settings source, bool runAtStartup)
    {
        _suspendSync = true;
        _modeFive.Checked = source.DisplayMode == DisplayMode.FiveHour;
        _modeSeven.Checked = source.DisplayMode == DisplayMode.SevenDay;
        _modeBoth.Checked = source.DisplayMode == DisplayMode.Both;
        _startup.Checked = runAtStartup;
        _paceColors.Checked = source.PaceColors;
        // Null is resolved at startup (Program.cs) against the installed channel; falling back to
        // false here only covers a dialog constructed straight from a file, as the tests do.
        _betaReleases.Checked = source.UseBetaReleases ?? false;
        var claude = source.StatusSources.GetValueOrDefault("claude");
        _watchClaude.Checked = claude?.Enabled ?? true;
        _claudeComponents.Text = ComponentFilter.Format(
            claude?.Components ?? [.. StatusSourceRegistry.Claude.DefaultComponents]);
        _claudeComponents.Enabled = _watchClaude.Checked;
        _notifyClaude.Checked = claude?.Notify ?? true;
        _notifyClaude.Enabled = _watchClaude.Checked;
        var openAi = source.StatusSources.GetValueOrDefault("openai");
        _watchOpenAi.Checked = openAi?.Enabled ?? false;
        _openAiComponents.Text = ComponentFilter.Format(
            openAi?.Components ?? [.. StatusSourceRegistry.OpenAi.DefaultComponents]);
        _openAiComponents.Enabled = _watchOpenAi.Checked;
        _notifyUsage.Checked = source.UsageNotifications.Enabled;
        _notifyLevel.SelectedIndex = IndexOf(source.UsageNotifications.Level);
        _notifyLevel.Enabled = _notifyUsage.Checked;
        _notifyOpenAi.Checked = openAi?.Notify ?? true;
        _notifyOpenAi.Enabled = _watchOpenAi.Checked;
        _weeklyAnchor.Text = source.WeeklyResetAnchor ?? "";
        _suspendSync = false;
        SetThresholds(source.Thresholds.Orange, source.Thresholds.Red, source.StalenessMinutes,
            source.DesktopStalenessHours);
    }

    private void ResetThresholdsToDefaults()
    {
        _paceColors.Checked = new Settings().PaceColors;
        SetThresholds(ThresholdRules.DefaultOrange, ThresholdRules.DefaultRed,
            ThresholdRules.DefaultStalenessMinutes, ThresholdRules.DefaultDesktopStalenessHours);
    }

    private void SetThresholds(int orange, int red, int stalenessMinutes, int desktopStalenessHours)
    {
        // The two spinners constrain each other, so a straight assignment could be clipped by a range
        // still describing the previous pair. Widen both, assign, then let the sync re-narrow them.
        _suspendSync = true;
        _orange.Maximum = 99;
        _red.Minimum = 1;
        (orange, red) = ThresholdRules.Clamp(orange, red);
        _orange.Value = orange;
        _red.Value = red;
        _staleness.Value = Math.Clamp(stalenessMinutes, (int)_staleness.Minimum, (int)_staleness.Maximum);
        _desktopStaleness.Value = Math.Clamp(desktopStalenessHours,
            (int)_desktopStaleness.Minimum, (int)_desktopStaleness.Maximum);
        _suspendSync = false;
        SyncRangesAndPreview();
    }

    private void WireLiveSync()
    {
        _orange.ValueChanged += (_, _) => SyncRangesAndPreview();
        _red.ValueChanged += (_, _) => SyncRangesAndPreview();
        _paceColors.CheckedChanged += (_, _) => SyncRangesAndPreview();
        _watchClaude.CheckedChanged += (_, _) =>
        {
            _claudeComponents.Enabled = _watchClaude.Checked;
            _notifyClaude.Enabled = _watchClaude.Checked;   // disabled, not unchecked: the choice survives
        };
        _watchOpenAi.CheckedChanged += (_, _) =>
        {
            _openAiComponents.Enabled = _watchOpenAi.Checked;
            _notifyOpenAi.Enabled = _watchOpenAi.Checked;   // disabled, not unchecked: the choice survives
        };
        _notifyUsage.CheckedChanged += (_, _) => _notifyLevel.Enabled = _notifyUsage.Checked;
        _weeklyAnchor.TextChanged += (_, _) =>
        {
            if (_suspendSync) return;
            _weeklyAnchorError.Visible = !string.IsNullOrWhiteSpace(_weeklyAnchor.Text)
                && WeeklyAnchor.TryParse(_weeklyAnchor.Text) is null;
        };
        _preview.Paint += (_, e) => UsageBar.Paint(e.Graphics, _preview.Width, _preview.Height,
            PreviewPercent, PreviewSeverity(), PreviewFraction());
    }

    /// <summary>Makes an invalid pair unreachable: each spinner's range is pinned one step clear of
    /// the other's current value, so the user cannot type or click their way to orange >= red.</summary>
    private void SyncRangesAndPreview()
    {
        if (_suspendSync) return;
        _orange.Maximum = _red.Value - 1;
        _red.Minimum = _orange.Value + 1;
        _preview.Invalidate();
        _previewCaption.Text = PreviewCaption();
    }

    /// <summary>The edited values as a settings object, without touching the live instance.</summary>
    public Settings Draft()
    {
        var draft = Clone(_draft);
        draft.DisplayMode = _modeFive.Checked ? DisplayMode.FiveHour
            : _modeSeven.Checked ? DisplayMode.SevenDay
            : DisplayMode.Both;
        draft.Thresholds = new Thresholds { Orange = (int)_orange.Value, Red = (int)_red.Value };
        draft.PaceColors = _paceColors.Checked;
        draft.StalenessMinutes = (int)_staleness.Value;
        draft.DesktopStalenessHours = (int)_desktopStaleness.Value;
        draft.RunAtStartup = _startup.Checked;
        draft.UseBetaReleases = _betaReleases.Checked;
        draft.UsageNotifications = new UsageNotificationSettings
        {
            Enabled = _notifyUsage.Checked,
            Level = LevelAt(_notifyLevel.SelectedIndex),
        };
        // The typed filter and the notify choice are kept even when unchecked, so turning the source
        // back on does not lose either.
        draft.StatusSources["openai"] = new StatusSourceSettings
        {
            Enabled = _watchOpenAi.Checked,
            Notify = _notifyOpenAi.Checked,
            Components = [.. ComponentFilter.Parse(_openAiComponents.Text)],
        };
        // All three fields are edited here now; the filter and the notify choice are kept even when
        // unchecked, so turning the source back on does not lose either.
        draft.StatusSources["claude"] = new StatusSourceSettings
        {
            Enabled = _watchClaude.Checked,
            Notify = _notifyClaude.Checked,
            Components = [.. ComponentFilter.Parse(_claudeComponents.Text)],
        };
        // Hidden group: keep whatever the clone carries, so opening the dialog on Claude Code data
        // cannot wipe an anchor set while the desktop history was the source. Blank clears it;
        // unparseable keeps the stored value, since silently nulling what they typed would look like
        // the field does not work.
        if (_desktopSource)
        {
            draft.WeeklyResetAnchor = string.IsNullOrWhiteSpace(_weeklyAnchor.Text)
                ? null
                : WeeklyAnchor.TryParse(_weeklyAnchor.Text)?.Format() ?? draft.WeeklyResetAnchor;
        }
        return draft;
    }

    private static int IndexOf(NotifyLevel level) => level == NotifyLevel.Orange ? 1 : 0;
    private static NotifyLevel LevelAt(int index) => index == 1 ? NotifyLevel.Orange : NotifyLevel.Red;

    /// <summary>Persists the draft, reporting whether it stuck. The update path saves without closing,
    /// since it is about to restart the app instead.</summary>
    private bool Commit(bool closeOnSuccess = true)
    {
        if (_save(Draft()))
        {
            _error.Visible = false;
            if (closeOnSuccess) Close();
            return true;
        }
        // Same wording as the tray menu's own failure line, so the two never disagree.
        _error.Text = "Settings could not be saved.";
        _error.Visible = true;
        return false;
    }

    // ---- preview ----

    private double? PreviewFraction() => _paceColors.Checked ? PreviewElapsedFraction : null;

    private Severity PreviewSeverity()
        => SeverityRules.ForSettings(Draft(), PreviewPercent, PreviewFraction());

    /// <summary>Says what the sample bar is and, when pace decided its colour, what the colour means
    /// — under pace colouring it no longer means "percent used".</summary>
    private string PreviewCaption()
    {
        var caption = $"Preview: {PreviewPercent}% used";
        if (!_paceColors.Checked) return $"{caption} · colour from the thresholds above";
        caption += $", {PreviewElapsedFraction * 100:0}% of the period elapsed";
        var pace = PaceFormat.Describe(
            SeverityRules.PaceRatio(PreviewPercent, PreviewFraction(), (int)_red.Value));
        return pace.Length == 0 ? caption : $"{caption} · {pace}";
    }

    private static Settings Clone(Settings source) => new()
    {
        DisplayMode = source.DisplayMode,
        Thresholds = new Thresholds { Orange = source.Thresholds.Orange, Red = source.Thresholds.Red },
        StalenessMinutes = source.StalenessMinutes,
        DesktopStalenessHours = source.DesktopStalenessHours,
        WeeklyResetAnchor = source.WeeklyResetAnchor,
        RunAtStartup = source.RunAtStartup,
        PaceColors = source.PaceColors,
        UseBetaReleases = source.UseBetaReleases,
        ConfigPathOverride = source.ConfigPathOverride,
        DesktopHistoryPathOverride = source.DesktopHistoryPathOverride,
        UsageNotifications = new UsageNotificationSettings
        {
            Enabled = source.UsageNotifications.Enabled,
            Level = source.UsageNotifications.Level,
        },
        StatusSources = source.StatusSources.ToDictionary(
            e => e.Key,
            e => e.Value is null ? null : new StatusSourceSettings
            {
                Enabled = e.Value.Enabled,
                Notify = e.Value.Notify,
                Components = e.Value.Components is null ? null : [.. e.Value.Components],
            },
            StringComparer.OrdinalIgnoreCase),
    };
}
