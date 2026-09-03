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
    private readonly Panel _preview = new() { Name = "preview", Width = UsageBar.DefaultWidth, Height = UsageBar.DefaultHeight };
    private readonly Label _previewCaption = new() { Name = "previewCaption", AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _error = new() { Name = "error", AutoSize = true, ForeColor = Color.Firebrick, Visible = false };
    private readonly Label _creator = new() { Name = "creator", AutoSize = true };
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
    public SettingsDialog(Settings settings, bool canRunAtStartup, bool runAtStartup,
        Func<Settings, bool> save, UpdateOptions updates)
    {
        _draft = Clone(settings);
        _canRunAtStartup = canRunAtStartup;
        _save = save;
        _updates = updates;
        _updateState = updates.InitialState;
        _latestVersion = updates.LatestVersion;
        _releaseNotes = updates.InitialReleaseNotes;

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

        layout.Controls.Add(Heading("Tray icons"));
        layout.Controls.Add(Indent(_modeFive));
        layout.Controls.Add(Indent(_modeSeven));
        layout.Controls.Add(Indent(_modeBoth));

        _startup.Enabled = _canRunAtStartup;
        layout.Controls.Add(Indent(_startup));
        if (!_canRunAtStartup)
        {
            layout.Controls.Add(Indent(new Label
            {
                Text = "Available only in the installed app.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
            }));
        }

        layout.Controls.Add(Heading("Colour thresholds"));
        layout.Controls.Add(Spinners(("Orange at", _orange, "%"), ("Red above", _red, "%")));
        layout.Controls.Add(Indent(_paceColors));
        layout.Controls.Add(Indent(_preview));
        layout.Controls.Add(Indent(_previewCaption));

        layout.Controls.Add(Heading("Refresh"));
        layout.Controls.Add(Spinners(("Treat data as stale after", _staleness, "minutes")));

        layout.Controls.Add(Heading("About"));
        layout.Controls.Add(BuildAbout());

        layout.Controls.Add(_error);
        layout.Controls.Add(BuildButtons());
        return layout;
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

        // Tab reaches the controls in reading order, then the buttons.
        int order = 0;
        foreach (var control in new Control[]
                 { _modeFive, _modeSeven, _modeBoth, _startup, _orange, _red, _paceColors, _staleness,
                   reset, cancel, save })
            control.TabIndex = order++;
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
        _suspendSync = false;
        SetThresholds(source.Thresholds.Orange, source.Thresholds.Red, source.StalenessMinutes);
    }

    private void ResetThresholdsToDefaults()
    {
        _paceColors.Checked = new Settings().PaceColors;
        SetThresholds(ThresholdRules.DefaultOrange, ThresholdRules.DefaultRed,
            ThresholdRules.DefaultStalenessMinutes);
    }

    private void SetThresholds(int orange, int red, int stalenessMinutes)
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
        _suspendSync = false;
        SyncRangesAndPreview();
    }

    private void WireLiveSync()
    {
        _orange.ValueChanged += (_, _) => SyncRangesAndPreview();
        _red.ValueChanged += (_, _) => SyncRangesAndPreview();
        _paceColors.CheckedChanged += (_, _) => SyncRangesAndPreview();
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
        draft.RunAtStartup = _startup.Checked;
        return draft;
    }

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
        RunAtStartup = source.RunAtStartup,
        PaceColors = source.PaceColors,
        ConfigPathOverride = source.ConfigPathOverride,
    };
}
