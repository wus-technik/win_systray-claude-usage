using System.Drawing;
using System.Windows.Forms;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The dialog's About section: the running version, what a check found, and the one button
/// that acts on it. The check and the restart confirmation are injected, so these run without a feed
/// and without a modal prompt.</summary>
public class SettingsDialogUpdateTests : IDisposable
{
    private readonly List<SettingsDialog> _open = [];
    private bool _restarted;
    private readonly List<Settings> _saved = [];

    public void Dispose() { foreach (var dialog in _open) dialog.Dispose(); }

    private SettingsDialog Dialog(UpdateOptions updates)
    {
        var dialog = new SettingsDialog(new Settings(), canRunAtStartup: true, runAtStartup: true,
            save: s => { _saved.Add(s); return true; }, updates);
        _open.Add(dialog);
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new System.Drawing.Point(-4000, -4000);
        dialog.Show();
        return dialog;
    }

    /// <summary>Installed, nothing checked yet, and the check returns whatever the test wants.</summary>
    private UpdateOptions Options(UpdateAvailability found = UpdateAvailability.UpToDate,
        string? foundVersion = null, bool confirmRestart = true, bool isInstalled = true)
        => new(
            InstalledVersion: "0.6.0",
            IsInstalled: isInstalled,
            InitialState: isInstalled ? UpdateAvailability.Unknown : UpdateAvailability.NotInstalled,
            LatestVersion: null,
            CheckNow: () => Task.FromResult((found, foundVersion)),
            Confirm: _ => confirmRestart,
            RestartToApply: () => _restarted = true);

    private static Label Label(SettingsDialog dialog, string name)
        => (Label)dialog.Controls.Find(name, searchAllChildren: true).Single();

    private static Button UpdateNow(SettingsDialog dialog)
        => (Button)dialog.Controls.Find("updateNow", searchAllChildren: true).Single();

    [Fact]
    public void TheRunningVersionIsShown()
        => Assert.Equal("0.6.0", Label(Dialog(Options()), "installedVersion").Text);

    [Fact]
    public void BeforeAnyCheckItDoesNotClaimToBeUpToDate()
        => Assert.Equal("not checked yet", Label(Dialog(Options()), "updateStatus").Text);

    [Fact]
    public void AStagedUpdateFromTheBackgroundLoopShowsBeforeChecking()
    {
        var dialog = Dialog(Options() with
        {
            InitialState = UpdateAvailability.UpdateReady,
            LatestVersion = "0.6.1",
        });
        Assert.Equal("0.6.1 ready to install", Label(dialog, "updateStatus").Text);
    }

    [Fact]
    public void CheckingFindsNothingNewer()
    {
        var dialog = Dialog(Options(UpdateAvailability.UpToDate));
        UpdateNow(dialog).PerformClick();

        Assert.Equal("up to date", Label(dialog, "updateStatus").Text);
        Assert.True(UpdateNow(dialog).Enabled); // re-checking stays available
        Assert.False(_restarted);
    }

    [Fact]
    public void AFailedCheckSaysSoAndStaysRetryable()
    {
        var dialog = Dialog(Options(UpdateAvailability.Failed));
        UpdateNow(dialog).PerformClick();

        Assert.Equal("check failed", Label(dialog, "updateStatus").Text);
        Assert.True(UpdateNow(dialog).Enabled);
    }

    [Fact]
    public void ConfirmingAFoundUpdateRestarts()
    {
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1", confirmRestart: true));
        UpdateNow(dialog).PerformClick();

        Assert.Equal("0.6.1 ready to install", Label(dialog, "updateStatus").Text);
        Assert.True(_restarted);
    }

    [Fact]
    public void DecliningTheRestartLeavesTheUpdateStaged()
    {
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1", confirmRestart: false));
        UpdateNow(dialog).PerformClick();

        Assert.False(_restarted);
        // Still named, so the menu's Restart to update is not the only way back to it.
        Assert.Equal("0.6.1 ready to install", Label(dialog, "updateStatus").Text);
    }

    [Fact]
    public void RestartingSavesPendingEditsFirst()
    {
        // Discarding a half-finished threshold change to install an update would be a nasty surprise.
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1", confirmRestart: true));
        ((NumericUpDown)dialog.Controls.Find("orange", searchAllChildren: true).Single()).Value = 25;

        UpdateNow(dialog).PerformClick();

        Assert.Equal(25, Assert.Single(_saved).Thresholds.Orange);
        Assert.True(_restarted);
    }

    [Fact]
    public void AFailedSaveCancelsTheRestart()
    {
        // The edits are still only in the form; restarting now would throw them away.
        var dialog = new SettingsDialog(new Settings(), canRunAtStartup: true, runAtStartup: true,
            save: _ => false, Options(UpdateAvailability.UpdateReady, "0.6.1", confirmRestart: true));
        _open.Add(dialog);
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new System.Drawing.Point(-4000, -4000);
        dialog.Show();

        UpdateNow(dialog).PerformClick();

        Assert.False(_restarted);
        Assert.True(Label(dialog, "error").Visible);
    }

    [Fact]
    public void AnAvailableUpdateIsNotGreyedOutLikeANeutralNote()
    {
        // Grey is for "nothing to do here". Something waiting to be installed is not that.
        var ready = Label(Dialog(Options() with
        {
            InitialState = UpdateAvailability.UpdateReady, LatestVersion = "0.6.1",
        }), "updateStatus");
        Assert.NotEqual(SystemColors.GrayText, ready.ForeColor);
    }

    [Fact]
    public void AFailedCheckIsFlaggedInRed()
    {
        var dialog = Dialog(Options(UpdateAvailability.Failed));
        UpdateNow(dialog).PerformClick();
        Assert.Equal(Color.Firebrick, Label(dialog, "updateStatus").ForeColor);
    }

    [Fact]
    public void AnUninstalledBuildCannotCheck()
    {
        var dialog = Dialog(Options(isInstalled: false));
        Assert.False(UpdateNow(dialog).Enabled);
        Assert.Equal("updates are available only in the installed app",
            Label(dialog, "updateStatus").Text);
    }
}
