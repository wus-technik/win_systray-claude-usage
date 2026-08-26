using System.Drawing;
using System.Windows.Forms;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The dialog's About section: the running version, what a check found, and the two controls
/// that act on it — refresh checks, Update now installs. The check and the restart confirmation are
/// injected, so these run without a feed and without a modal prompt.</summary>
public class SettingsDialogUpdateTests : IDisposable
{
    private readonly List<SettingsDialog> _open = [];
    private bool _restarted;
    private readonly List<Settings> _saved = [];
    private int _checks;
    private string? _confirmedNotes;
    private string? _confirmedQuestion;

    public void Dispose() { foreach (var dialog in _open) dialog.Dispose(); }

    private SettingsDialog Dialog(UpdateOptions updates, Func<Settings, bool>? save = null)
    {
        var dialog = new SettingsDialog(new Settings(), canRunAtStartup: true, runAtStartup: true,
            save: save ?? (s => { _saved.Add(s); return true; }), updates);
        _open.Add(dialog);
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new System.Drawing.Point(-4000, -4000);
        dialog.Show();
        return dialog;
    }

    /// <summary>Installed, nothing checked yet, and the check returns whatever the test wants.</summary>
    private UpdateOptions Options(UpdateAvailability found = UpdateAvailability.UpToDate,
        string? foundVersion = null, string? foundNotes = null, bool confirmRestart = true,
        bool isInstalled = true)
        => new(
            InstalledVersion: "0.6.0",
            IsInstalled: isInstalled,
            InitialState: isInstalled ? UpdateAvailability.Unknown : UpdateAvailability.NotInstalled,
            LatestVersion: null,
            InitialReleaseNotes: null,
            CheckNow: () => { _checks++; return Task.FromResult((found, foundVersion, foundNotes)); },
            Confirm: (question, notes) =>
            {
                _confirmedQuestion = question;
                _confirmedNotes = notes;
                return confirmRestart;
            },
            RestartToApply: () => _restarted = true);

    private static Label Label(SettingsDialog dialog, string name)
        => (Label)dialog.Controls.Find(name, searchAllChildren: true).Single();

    private static Button Button(SettingsDialog dialog, string name)
        => (Button)dialog.Controls.Find(name, searchAllChildren: true).Single();

    private static Button CheckUpdates(SettingsDialog dialog) => Button(dialog, "checkUpdates");
    private static Button UpdateNow(SettingsDialog dialog) => Button(dialog, "updateNow");

    [Fact]
    public void TheCreatorIsNamed()
    {
        // Doubled ampersand: the label would otherwise draw "WS Technik GmbH" with the S underlined.
        Assert.Equal("W&&S Technik GmbH", Label(Dialog(Options()), "creator").Text);
    }

    [Fact]
    public void TheWindowIsTitledLikeTheRestOfTheApp()
        => Assert.Equal("Claude Usage — Settings", Dialog(Options()).Text);

    [Fact]
    public void TheRunningVersionIsShown()
        => Assert.Equal("0.6.0", Label(Dialog(Options()), "installedVersion").Text);

    [Fact]
    public void BeforeAnyCheckItDoesNotClaimToBeUpToDate()
        => Assert.Equal("not checked yet", Label(Dialog(Options()), "updateStatus").Text);

    /// <summary>The glyph alone tells a screen reader nothing, and a tooltip is not read as a name.</summary>
    [Fact]
    public void TheRefreshButtonSaysWhatItDoes()
        => Assert.Equal("Check for updates", CheckUpdates(Dialog(Options())).AccessibleName);

    // ---- checking is the refresh button's job, and only its job ----

    [Fact]
    public void RefreshingNamesTheVersionItFound()
    {
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1"));
        CheckUpdates(dialog).PerformClick();

        Assert.Equal("0.6.1 ready to install", Label(dialog, "updateStatus").Text);
        Assert.Equal(1, _checks);
    }

    /// <summary>The whole point of the split: finding an update must not start installing one.</summary>
    [Fact]
    public void RefreshingNeverRestarts()
    {
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1"));
        CheckUpdates(dialog).PerformClick();

        Assert.False(_restarted);
        Assert.Null(_confirmedQuestion);
    }

    [Fact]
    public void RefreshingFindsNothingNewer()
    {
        var dialog = Dialog(Options(UpdateAvailability.UpToDate));
        CheckUpdates(dialog).PerformClick();

        Assert.Equal("up to date", Label(dialog, "updateStatus").Text);
        Assert.True(CheckUpdates(dialog).Enabled); // re-checking stays available
        Assert.False(UpdateNow(dialog).Enabled);
    }

    [Fact]
    public void AFailedCheckSaysSoAndStaysRetryable()
    {
        var dialog = Dialog(Options(UpdateAvailability.Failed));
        CheckUpdates(dialog).PerformClick();

        Assert.Equal("check failed", Label(dialog, "updateStatus").Text);
        Assert.True(CheckUpdates(dialog).Enabled);
        Assert.False(UpdateNow(dialog).Enabled);
    }

    // ---- Update now installs, and is dead until there is something to install ----

    [Fact]
    public void UpdateNowIsDeadBeforeAnyCheck()
    {
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1"));
        Assert.False(UpdateNow(dialog).Enabled);
        Assert.True(CheckUpdates(dialog).Enabled);
    }

    [Fact]
    public void AFoundUpdateEnablesUpdateNow()
    {
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1"));
        CheckUpdates(dialog).PerformClick();
        Assert.True(UpdateNow(dialog).Enabled);
    }

    [Fact]
    public void AStagedUpdateFromTheBackgroundLoopIsInstallableWithoutChecking()
    {
        var dialog = Dialog(Options() with
        {
            InitialState = UpdateAvailability.UpdateReady,
            LatestVersion = "0.6.1",
        });
        Assert.Equal("0.6.1 ready to install", Label(dialog, "updateStatus").Text);
        Assert.True(UpdateNow(dialog).Enabled);
    }

    [Fact]
    public void InstallingDoesNotCheckAgain()
    {
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1"));
        CheckUpdates(dialog).PerformClick();
        UpdateNow(dialog).PerformClick();

        Assert.Equal(1, _checks);
        Assert.True(_restarted);
    }

    [Fact]
    public void InstallingShowsTheVersionsReleaseNotes()
    {
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1",
            foundNotes: "### Fixed\n\n- The thing"));
        CheckUpdates(dialog).PerformClick();
        UpdateNow(dialog).PerformClick();

        Assert.Equal("### Fixed\r\n\r\n- The thing", _confirmedNotes);
        Assert.Contains("0.6.1", _confirmedQuestion);
    }

    /// <summary>Packages built before the pipeline passed --releaseNotes carry none. That is not an
    /// error, and it must not block the install.</summary>
    [Fact]
    public void AnUpdateWithoutNotesStillInstalls()
    {
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1", foundNotes: "   "));
        CheckUpdates(dialog).PerformClick();
        UpdateNow(dialog).PerformClick();

        Assert.Null(_confirmedNotes);
        Assert.True(_restarted);
    }

    [Fact]
    public void NotesFromTheBackgroundCheckSurviveToTheConfirmation()
    {
        var dialog = Dialog(Options(confirmRestart: true) with
        {
            InitialState = UpdateAvailability.UpdateReady,
            LatestVersion = "0.6.1",
            InitialReleaseNotes = "- Staged before the dialog opened",
        });
        UpdateNow(dialog).PerformClick();

        Assert.Equal("- Staged before the dialog opened", _confirmedNotes);
        Assert.Equal(0, _checks);
    }

    [Fact]
    public void DecliningTheRestartLeavesTheUpdateStaged()
    {
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1", confirmRestart: false));
        CheckUpdates(dialog).PerformClick();
        UpdateNow(dialog).PerformClick();

        Assert.False(_restarted);
        // Still named and still installable, so the menu's Restart to update is not the only way back.
        Assert.Equal("0.6.1 ready to install", Label(dialog, "updateStatus").Text);
        Assert.True(UpdateNow(dialog).Enabled);
    }

    [Fact]
    public void RestartingSavesPendingEditsFirst()
    {
        // Discarding a half-finished threshold change to install an update would be a nasty surprise.
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1", confirmRestart: true));
        ((NumericUpDown)dialog.Controls.Find("orange", searchAllChildren: true).Single()).Value = 25;

        CheckUpdates(dialog).PerformClick();
        UpdateNow(dialog).PerformClick();

        Assert.Equal(25, Assert.Single(_saved).Thresholds.Orange);
        Assert.True(_restarted);
    }

    [Fact]
    public void AFailedSaveCancelsTheRestart()
    {
        // The edits are still only in the form; restarting now would throw them away.
        var dialog = Dialog(Options(UpdateAvailability.UpdateReady, "0.6.1", confirmRestart: true),
            save: _ => false);

        CheckUpdates(dialog).PerformClick();
        UpdateNow(dialog).PerformClick();

        Assert.False(_restarted);
        Assert.True(Label(dialog, "error").Visible);
    }

    // ---- states and colours ----

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
        CheckUpdates(dialog).PerformClick();
        Assert.Equal(Color.Firebrick, Label(dialog, "updateStatus").ForeColor);
    }

    [Fact]
    public void AnUninstalledBuildCanNeitherCheckNorInstall()
    {
        var dialog = Dialog(Options(isInstalled: false));
        Assert.False(CheckUpdates(dialog).Enabled);
        Assert.False(UpdateNow(dialog).Enabled);
        Assert.Equal("updates are available only in the installed app",
            Label(dialog, "updateStatus").Text);
    }
}
