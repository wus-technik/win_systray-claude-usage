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
        bool runAtStartup = true)
    {
        var dialog = new SettingsDialog(settings, canRunAtStartup: true, runAtStartup,
            save ?? (_ => true), TestUpdateOptions.Inert());
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
}
