using System.Windows.Forms;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The dialog is a thin shell over ThresholdRules, so these cover the shell's own promises:
/// that it never touches the live settings, that an invalid pair is unreachable through the spinners,
/// and that a failed save keeps it open.</summary>
public class SettingsDialogTests : IDisposable
{
    private readonly List<SettingsDialog> _open = [];

    public void Dispose() { foreach (var dialog in _open) dialog.Dispose(); }

    /// <summary>Shown offscreen: Button.PerformClick() is a no-op while a form has never been shown,
    /// because an unrealized control cannot take focus.</summary>
    private SettingsDialog Dialog(Settings settings, Func<Settings, bool>? save = null,
        bool runAtStartup = true, bool desktopSource = false,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? componentNames = null)
    {
        var dialog = new SettingsDialog(settings, canRunAtStartup: true, runAtStartup,
            save ?? (_ => true), TestUpdateOptions.Inert(), desktopSource,
            componentNames ?? new Dictionary<string, IReadOnlyList<string>>());
        _open.Add(dialog);
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new System.Drawing.Point(-4000, -4000);
        dialog.Show();
        return dialog;
    }

    private static NumericUpDown Spinner(SettingsDialog dialog, string name)
        => (NumericUpDown)dialog.Controls.Find(name, searchAllChildren: true).Single();

    [Fact]
    public void DraftMirrorsTheSettingsItOpenedWith()
    {
        var settings = new Settings
        {
            DisplayMode = DisplayMode.FiveHour,
            Thresholds = new Thresholds { Orange = 30, Red = 60 },
            StalenessMinutes = 5,
            PaceColors = false,
            ConfigPathOverride = @"C:\alt\.claude.json",
        };
        var draft = Dialog(settings).Draft();

        Assert.Equal(DisplayMode.FiveHour, draft.DisplayMode);
        Assert.Equal(30, draft.Thresholds.Orange);
        Assert.Equal(60, draft.Thresholds.Red);
        Assert.Equal(5, draft.StalenessMinutes);
        Assert.False(draft.PaceColors);
        // Not editable here, but it must survive the round trip rather than being dropped on Save.
        Assert.Equal(@"C:\alt\.claude.json", draft.ConfigPathOverride);
    }

    [Fact]
    public void EditingNeverTouchesTheLiveSettings()
    {
        var settings = new Settings { Thresholds = new Thresholds { Orange = 30, Red = 60 } };
        var dialog = Dialog(settings);

        Spinner(dialog, "orange").Value = 10;
        Spinner(dialog, "red").Value = 90;
        Spinner(dialog, "staleness").Value = 99;

        Assert.Equal(30, settings.Thresholds.Orange);
        Assert.Equal(60, settings.Thresholds.Red);
        Assert.Equal(15, settings.StalenessMinutes);
        // ...while the draft does carry the edits.
        Assert.Equal(10, dialog.Draft().Thresholds.Orange);
        Assert.Equal(90, dialog.Draft().Thresholds.Red);
    }

    [Fact]
    public void NeitherSpinnerCanReachTheOther()
    {
        // The invariant is enforced by the ranges, not by validating on Save: each spinner stops one
        // step short of the other, so neither the arrows nor typed text can produce an invalid pair.
        var dialog = Dialog(new Settings { Thresholds = new Thresholds { Orange = 50, Red = 85 } });
        var orange = Spinner(dialog, "orange");
        var red = Spinner(dialog, "red");

        Assert.Equal(84, orange.Maximum);
        Assert.Equal(51, red.Minimum);

        orange.Value = orange.Maximum;
        Assert.Equal(85, red.Value);
        Assert.Equal(85, red.Minimum); // red is now pinned right above orange
        Assert.True(ThresholdRules.IsValidPair(
            dialog.Draft().Thresholds.Orange, dialog.Draft().Thresholds.Red));
    }

    [Fact]
    public void TheRangesMoveWithEachEdit()
    {
        var dialog = Dialog(new Settings { Thresholds = new Thresholds { Orange = 50, Red = 85 } });
        var orange = Spinner(dialog, "orange");
        var red = Spinner(dialog, "red");

        red.Value = 60;
        Assert.Equal(59, orange.Maximum);

        orange.Value = 20;
        Assert.Equal(21, red.Minimum);
    }

    [Fact]
    public void EveryReachableSpinnerPairIsValid()
    {
        var dialog = Dialog(new Settings());
        var orange = Spinner(dialog, "orange");
        var red = Spinner(dialog, "red");

        // Walk both spinners across their whole range in both directions; the pair must never break.
        foreach (var target in new[] { 100, 0, 100, 0 })
        {
            orange.Value = Math.Clamp(target, (int)orange.Minimum, (int)orange.Maximum);
            red.Value = Math.Clamp(target, (int)red.Minimum, (int)red.Maximum);
            var thresholds = dialog.Draft().Thresholds;
            Assert.True(ThresholdRules.IsValidPair(thresholds.Orange, thresholds.Red),
                $"({thresholds.Orange}, {thresholds.Red}) after driving both to {target}");
        }
    }

    [Fact]
    public void AFileLoadedInvalidPairIsClampedRatherThanShownAsIs()
    {
        // Settings.Load normalizes, but a caller could hand over anything; the spinners must still
        // open on a valid pair rather than throwing on an out-of-range assignment.
        var dialog = Dialog(new Settings { Thresholds = new Thresholds { Orange = 90, Red = 50 } });
        var thresholds = dialog.Draft().Thresholds;
        Assert.True(ThresholdRules.IsValidPair(thresholds.Orange, thresholds.Red));
    }

    [Fact]
    public void ResetRestoresTheDefaults()
    {
        var dialog = Dialog(new Settings
        {
            Thresholds = new Thresholds { Orange = 10, Red = 20 },
            StalenessMinutes = 99,
        });

        Button(dialog, "reset").PerformClick();

        var draft = dialog.Draft();
        Assert.Equal(ThresholdRules.DefaultOrange, draft.Thresholds.Orange);
        Assert.Equal(ThresholdRules.DefaultRed, draft.Thresholds.Red);
        Assert.Equal(ThresholdRules.DefaultStalenessMinutes, draft.StalenessMinutes);
    }

    [Fact]
    public void ResetLeavesTheDisplayModeAlone()
    {
        // "Reset to defaults" is about the colours; silently switching which icons are shown would be
        // a surprising side effect.
        var dialog = Dialog(new Settings { DisplayMode = DisplayMode.SevenDay });
        Button(dialog, "reset").PerformClick();
        Assert.Equal(DisplayMode.SevenDay, dialog.Draft().DisplayMode);
    }

    [Fact]
    public void SaveClosesTheDialog()
    {
        var dialog = Dialog(new Settings(), save: _ => true);
        Button(dialog, "save").PerformClick();
        Assert.True(dialog.IsDisposed || !dialog.Visible);
    }

    [Fact]
    public void AFailedSaveKeepsTheDialogOpenAndSaysSo()
    {
        var dialog = Dialog(new Settings(), save: _ => false);

        Button(dialog, "save").PerformClick();

        Assert.False(dialog.IsDisposed);
        var error = dialog.Controls.Find("error", searchAllChildren: true).Single();
        Assert.True(error.Visible);
        Assert.Contains("could not be saved", error.Text);
    }

    [Fact]
    public void SavePassesTheEditedValuesOn()
    {
        Settings? saved = null;
        var dialog = Dialog(new Settings(), save: edited => { saved = edited; return true; });

        Spinner(dialog, "orange").Value = 25;
        Button(dialog, "save").PerformClick();

        Assert.Equal(25, saved!.Thresholds.Orange);
    }

    [Fact]
    public void TheStartupCheckboxShowsTheRegistryStateNotTheSavedPreference()
    {
        // A preference the registry refused (GPO-locked HKCU) is still in the file. The checkbox has
        // to show what is actually registered, or it lies about a state it failed to reach.
        var dialog = Dialog(new Settings { RunAtStartup = true }, runAtStartup: false);
        var startup = (CheckBox)dialog.Controls.Find("startup", searchAllChildren: true).Single();
        Assert.False(startup.Checked);
        Assert.False(dialog.Draft().RunAtStartup);
    }

    private static Button Button(SettingsDialog dialog, string name)
        => (Button)dialog.Controls.Find(name, searchAllChildren: true).Single();

    [Fact]
    public void DesktopStaleness_RoundTripsThroughTheDraft()
    {
        var dialog = Dialog(new Settings { DesktopStalenessHours = 6, DesktopHistoryPathOverride = @"C:\alt\h.json" });
        Assert.Equal(6, Spinner(dialog, "desktopStaleness").Value);

        Spinner(dialog, "desktopStaleness").Value = 12;
        var draft = dialog.Draft();
        Assert.Equal(12, draft.DesktopStalenessHours);
        Assert.Equal(@"C:\alt\h.json", draft.DesktopHistoryPathOverride); // file-only, carried through untouched
    }

    [Fact]
    public void DesktopStaleness_SpinnerRangeIsOneToOneSixtyEight()
    {
        var spinner = Spinner(Dialog(new Settings()), "desktopStaleness");
        Assert.Equal(1, spinner.Minimum);
        Assert.Equal(168, spinner.Maximum);
    }

    [Fact]
    public void Reset_RestoresDesktopStaleness()
    {
        var dialog = Dialog(new Settings { DesktopStalenessHours = 48 });
        Button(dialog, "reset").PerformClick();
        Assert.Equal(ThresholdRules.DefaultDesktopStalenessHours, dialog.Draft().DesktopStalenessHours);
    }

    [Fact]
    public void DesktopStaleness_DoesNotTouchTheLiveSettings()
    {
        var live = new Settings { DesktopStalenessHours = 3 };
        Spinner(Dialog(live), "desktopStaleness").Value = 24;
        Assert.Equal(3, live.DesktopStalenessHours);
    }

    private static CheckBox WatchOpenAi(SettingsDialog d) => Find<CheckBox>(d, "watchOpenAi")!;
    private static TextBox OpenAiComponents(SettingsDialog d) => Find<TextBox>(d, "openAiComponents")!;

    private static T? Find<T>(Control root, string name) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T match && match.Name == name) return match;
            if (Find<T>(c, name) is { } nested) return nested;
        }
        return null;
    }

    [Fact]
    public void OpenAiCheckbox_ReflectsSettings_AndDrivesTheDraft()
    {
        var settings = new Settings();
        settings.StatusSources["openai"] = new StatusSourceSettings { Enabled = true, Components = ["codex"] };
        var dialog = Dialog(settings);

        Assert.True(WatchOpenAi(dialog).Checked);
        Assert.Equal("codex", OpenAiComponents(dialog).Text);

        OpenAiComponents(dialog).Text = "codex, login";
        var draft = dialog.Draft();
        Assert.True(draft.StatusSources["openai"]!.Enabled);
        Assert.Equal(["codex", "login"], draft.StatusSources["openai"]!.Components);
    }

    [Fact]
    public void ComponentsField_IsOnlyEnabledWhileWatching()
    {
        var dialog = Dialog(new Settings());
        Assert.False(WatchOpenAi(dialog).Checked);
        Assert.False(OpenAiComponents(dialog).Enabled);

        WatchOpenAi(dialog).Checked = true;
        Assert.True(OpenAiComponents(dialog).Enabled);
    }

    [Fact]
    public void UncheckedOpenAi_KeepsTheTypedFilterForNextTime()
    {
        var dialog = Dialog(new Settings());
        WatchOpenAi(dialog).Checked = true;
        OpenAiComponents(dialog).Text = "codex";
        WatchOpenAi(dialog).Checked = false;

        var draft = dialog.Draft();
        Assert.False(draft.StatusSources["openai"]!.Enabled);
        Assert.Equal(["codex"], draft.StatusSources["openai"]!.Components);
    }

    private static CheckBox WatchClaude(SettingsDialog d) => Find<CheckBox>(d, "watchClaude")!;
    private static TextBox ClaudeComponents(SettingsDialog d) => Find<TextBox>(d, "claudeComponents")!;

    /// <summary>Claude is on by default and watches everything: blank = all, literally — the box
    /// shows exactly what is stored, and DefaultComponents is empty.</summary>
    [Fact]
    public void ClaudeWatch_DefaultsToOnAndBlank()
    {
        var dialog = Dialog(new Settings());
        Assert.True(WatchClaude(dialog).Checked);
        Assert.Equal("", ClaudeComponents(dialog).Text);
        Assert.True(ClaudeComponents(dialog).Enabled);
    }

    [Fact]
    public void ClaudeCheckbox_ReflectsSettings_AndDrivesTheDraft()
    {
        var settings = new Settings();
        settings.StatusSources["claude"] = new StatusSourceSettings { Enabled = true, Components = ["Claude Code"] };
        var dialog = Dialog(settings);

        Assert.True(WatchClaude(dialog).Checked);
        Assert.Equal("Claude Code", ClaudeComponents(dialog).Text);

        ClaudeComponents(dialog).Text = "Claude Code, api.anthropic.com";
        var draft = dialog.Draft();
        Assert.True(draft.StatusSources["claude"]!.Enabled);
        Assert.Equal(["Claude Code", "api.anthropic.com"], draft.StatusSources["claude"]!.Components);
    }

    /// <summary>Unchecking disables rather than clears, so turning the page off and on again keeps
    /// both the typed filter and the notify choice.</summary>
    [Fact]
    public void UncheckedClaude_DisablesItsFieldsWithoutClearingThem()
    {
        var dialog = Dialog(new Settings());
        ClaudeComponents(dialog).Text = "Claude Code";
        Check(dialog, "notifyClaude").Checked = true;

        WatchClaude(dialog).Checked = false;
        Assert.False(ClaudeComponents(dialog).Enabled);
        Assert.False(Check(dialog, "notifyClaude").Enabled);
        Assert.Equal("Claude Code", ClaudeComponents(dialog).Text);
        Assert.True(Check(dialog, "notifyClaude").Checked);

        var draft = dialog.Draft();
        Assert.False(draft.StatusSources["claude"]!.Enabled);
        Assert.Equal(["Claude Code"], draft.StatusSources["claude"]!.Components);
        Assert.True(draft.StatusSources["claude"]!.Notify);
    }

    [Fact]
    public void ClaudeComponents_SaveExactlyTheTypedTokens()
    {
        var dialog = Dialog(new Settings());
        ClaudeComponents(dialog).Text = " Claude Code ,, Cowork ";
        Assert.Equal(["Claude Code", "Cowork"], dialog.Draft().StatusSources["claude"]!.Components);
    }

    /// <summary>A hand-written six-name filter must survive an untouched open/save round trip: the
    /// dialog is now the owner of all three fields, so a bug here silently rewrites settings.json.</summary>
    [Fact]
    public void HandWrittenClaudeFilter_SurvivesAnUntouchedRoundTrip()
    {
        string[] six =
        [
            "claude.ai", "Claude Console (platform.claude.com)", "Claude API (api.anthropic.com)",
            "Claude Code", "Claude Cowork", "Claude for Government",
        ];
        var settings = new Settings();
        settings.StatusSources["claude"] = new StatusSourceSettings
            { Enabled = true, Notify = false, Components = [.. six] };

        var draft = Dialog(settings).Draft();
        Assert.Equal(six, draft.StatusSources["claude"]!.Components);
        Assert.True(draft.StatusSources["claude"]!.Enabled);
        Assert.False(draft.StatusSources["claude"]!.Notify);
    }

    /// <summary>"Reset to defaults" is scoped to the colour thresholds and staleness. Which status
    /// pages are watched is a preference of the same kind as the display mode, not a colour.</summary>
    [Fact]
    public void ResetLeavesTheClaudeSourceAlone()
    {
        var settings = new Settings();
        settings.StatusSources["claude"] = new StatusSourceSettings { Enabled = false, Components = ["Claude Code"] };
        var dialog = Dialog(settings);
        Button(dialog, "reset").PerformClick();

        Assert.False(WatchClaude(dialog).Checked);
        Assert.Equal("Claude Code", ClaudeComponents(dialog).Text);
    }

    /// <summary>"Reset to defaults" is scoped to the colour thresholds and staleness (see
    /// ResetLeavesTheDisplayModeAlone). Which status pages are watched is a preference of the same
    /// kind as the display mode, not a colour.</summary>
    [Fact]
    public void ResetLeavesTheOpenAiSourceAlone()
    {
        var settings = new Settings();
        settings.StatusSources["openai"] = new StatusSourceSettings { Enabled = true, Components = ["codex"] };
        var dialog = Dialog(settings);
        Button(dialog, "reset").PerformClick();

        Assert.True(WatchOpenAi(dialog).Checked);
        Assert.Equal("codex", OpenAiComponents(dialog).Text);
    }

    private static ComboBox Combo(SettingsDialog d, string name) => Find<ComboBox>(d, name)!;
    private static CheckBox Check(SettingsDialog d, string name) => Find<CheckBox>(d, name)!;

    [Fact]
    public void Notifications_ReflectSettings_AndDriveTheDraft()
    {
        var settings = new Settings { UsageNotifications = new UsageNotificationSettings { Enabled = false, Level = NotifyLevel.Orange } };
        settings.StatusSources["claude"] = new StatusSourceSettings { Enabled = true, Notify = false, Components = [] };
        settings.StatusSources["openai"] = new StatusSourceSettings { Enabled = true, Notify = false, Components = ["codex"] };
        var dialog = Dialog(settings);

        Assert.False(Check(dialog, "notifyUsage").Checked);
        Assert.Equal("Orange and red", Combo(dialog, "notifyLevel").SelectedItem);
        Assert.False(Check(dialog, "notifyClaude").Checked);
        Assert.False(Check(dialog, "notifyOpenAi").Checked);

        Check(dialog, "notifyUsage").Checked = true;
        Combo(dialog, "notifyLevel").SelectedIndex = 0;   // "Red only"
        Check(dialog, "notifyClaude").Checked = true;
        Check(dialog, "notifyOpenAi").Checked = true;

        var draft = dialog.Draft();
        Assert.True(draft.UsageNotifications.Enabled);
        Assert.Equal(NotifyLevel.Red, draft.UsageNotifications.Level);
        Assert.True(draft.StatusSources["claude"]!.Notify);
        Assert.True(draft.StatusSources["openai"]!.Notify);
        Assert.Equal(["codex"], draft.StatusSources["openai"]!.Components);   // untouched by the notify edit

        // The live settings were never touched.
        Assert.False(settings.UsageNotifications.Enabled);
        Assert.False(settings.StatusSources["openai"]!.Notify);
    }

    [Fact]
    public void Notifications_DefaultsRoundTripUnchanged()
    {
        // The three copy paths (Draft rebuilds openai, Clone copies field by field, ApplySettings
        // copies across) each silently drop a field they were not taught about.
        var draft = Dialog(new Settings()).Draft();
        Assert.True(draft.UsageNotifications.Enabled);
        Assert.Equal(NotifyLevel.Red, draft.UsageNotifications.Level);
        Assert.True(draft.StatusSources["claude"]!.Notify);
        Assert.True(draft.StatusSources["openai"]!.Notify);
    }

    [Fact]
    public void OpenAiNotify_IsOnlyEnabledWhileWatching_ButKeepsItsValue()
    {
        var dialog = Dialog(new Settings());
        Assert.False(Check(dialog, "notifyOpenAi").Enabled);
        WatchOpenAi(dialog).Checked = true;
        Assert.True(Check(dialog, "notifyOpenAi").Enabled);
        Check(dialog, "notifyOpenAi").Checked = false;
        WatchOpenAi(dialog).Checked = false;
        Assert.False(dialog.Draft().StatusSources["openai"]!.Notify);   // the choice survives the off cycle
    }

    [Fact]
    public void LevelIsOnlyEnabledWhileUsageNotificationsAreOn()
    {
        var dialog = Dialog(new Settings());
        Assert.True(Combo(dialog, "notifyLevel").Enabled);
        Check(dialog, "notifyUsage").Checked = false;
        Assert.False(Combo(dialog, "notifyLevel").Enabled);
    }

    [Fact]
    public void ResetLeavesNotificationsAlone()
    {
        var dialog = Dialog(new Settings { UsageNotifications = new UsageNotificationSettings { Enabled = false, Level = NotifyLevel.Orange } });
        Button(dialog, "reset").PerformClick();
        Assert.False(dialog.Draft().UsageNotifications.Enabled);
        Assert.Equal(NotifyLevel.Orange, dialog.Draft().UsageNotifications.Level);
    }

    [Fact]
    public void WithoutADesktopSource_TheClaudeDesktopGroupIsAbsent()
    {
        // The invariant: a Claude Code user is never shown a setting that does nothing for them.
        var dialog = Dialog(new Settings());
        Assert.Null(Find<TextBox>(dialog, "weeklyAnchor"));
    }

    [Fact]
    public void WithADesktopSource_TheAnchorFieldShowsTheCanonicalValue()
    {
        var dialog = Dialog(new Settings { WeeklyResetAnchor = "Thu 03:00" }, desktopSource: true);
        Assert.Equal("Thu 03:00", Find<TextBox>(dialog, "weeklyAnchor")!.Text);
    }

    [Fact]
    public void ValidAnchor_ReachesTheDraftCanonicalised()
    {
        var dialog = Dialog(new Settings(), desktopSource: true);
        Find<TextBox>(dialog, "weeklyAnchor")!.Text = "thu 3:00";
        Assert.Equal("Thu 03:00", dialog.Draft().WeeklyResetAnchor);
    }

    [Fact]
    public void BlankAnchor_ClearsTheSetting()
    {
        var dialog = Dialog(new Settings { WeeklyResetAnchor = "Thu 03:00" }, desktopSource: true);
        Find<TextBox>(dialog, "weeklyAnchor")!.Text = "  ";
        Assert.Null(dialog.Draft().WeeklyResetAnchor);
    }

    [Fact]
    public void UnparseableAnchor_ShowsAnErrorAndKeepsTheStoredValue()
    {
        // Silently nulling what they typed would look like the field simply does not work.
        var dialog = Dialog(new Settings { WeeklyResetAnchor = "Thu 03:00" }, desktopSource: true);
        Find<TextBox>(dialog, "weeklyAnchor")!.Text = "Donnerstag";
        Assert.True(Find<Label>(dialog, "weeklyAnchorError")!.Visible);
        Assert.Equal("Thu 03:00", dialog.Draft().WeeklyResetAnchor);
    }

    [Fact]
    public void AnchorSurvivesADialogOpenedWithoutADesktopSource()
    {
        // A CLI-source dialog must not wipe an anchor the user set while on desktop data.
        var dialog = Dialog(new Settings { WeeklyResetAnchor = "Thu 03:00" });
        Assert.Equal("Thu 03:00", dialog.Draft().WeeklyResetAnchor);
    }

    private static Label Hint(SettingsDialog d, string name) => Find<Label>(d, name)!;

    [Fact]
    public void Hint_ListsTheSuppliedNames()
    {
        var dialog = Dialog(new Settings(), componentNames: new Dictionary<string, IReadOnlyList<string>>
        {
            ["claude"] = ["claude.ai", "Claude Code", "Claude Cowork"],
        });
        Assert.Equal("Page lists: claude.ai, Claude Code, Claude Cowork",
            Hint(dialog, "claudeComponentsHint").Text);
    }

    /// <summary>Nothing fetched yet — including the case the cache exists for: the user opened the
    /// dialog to re-enable a source the monitor holds no entry for. Both branches of HintFor: a
    /// missing key (openai) and a present-but-empty list (claude, as a "components": [] payload
    /// would produce).</summary>
    [Fact]
    public void Hint_FallsBackWhenNoNamesAreKnown()
    {
        var dialog = Dialog(new Settings(), componentNames: new Dictionary<string, IReadOnlyList<string>>
        {
            ["claude"] = [],
        });
        Assert.Equal("Page lists: not fetched yet", Hint(dialog, "claudeComponentsHint").Text);
        Assert.Equal("Page lists: not fetched yet", Hint(dialog, "openAiComponentsHint").Text);
    }

    /// <summary>The caption is a reference, not a prefill: it never reaches the box or the draft.
    /// Blank = all stays literally true.</summary>
    [Fact]
    public void Hint_NeverPrefillsTheBox()
    {
        var dialog = Dialog(new Settings(), componentNames: new Dictionary<string, IReadOnlyList<string>>
        {
            ["claude"] = ["claude.ai", "Claude Code"],
        });
        Assert.Equal("", ClaudeComponents(dialog).Text);
        Assert.Empty(dialog.Draft().StatusSources["claude"]!.Components!);
    }
}
