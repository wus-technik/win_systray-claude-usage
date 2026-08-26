using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class VersionDisplayTests
{
    [Theory]
    // Build metadata is for us, not the user: the assembly carries the commit sha, the dialog does not.
    [InlineData("0.6.0+ca9fdce40af520e93e272d8d5a974dc18c0a40d4", "0.6.0")]
    [InlineData("0.5.2-local.2+f870943", "0.5.2-local.2")] // prerelease survives, metadata does not
    [InlineData("0.6.0", "0.6.0")]
    public void ShortStripsBuildMetadata(string informational, string expected)
        => Assert.Equal(expected, VersionDisplay.Short(informational));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShortFallsBackWhenThereIsNoVersion(string? informational)
        => Assert.Equal("unknown", VersionDisplay.Short(informational));

    [Theory]
    [InlineData(UpdateAvailability.Unknown, null, "not checked yet")]
    [InlineData(UpdateAvailability.Checking, null, "checking…")]
    [InlineData(UpdateAvailability.UpToDate, null, "up to date")]
    [InlineData(UpdateAvailability.Failed, null, "check failed")]
    [InlineData(UpdateAvailability.UpdateReady, "0.6.1", "0.6.1 ready to install")]
    // A staged update whose version we never learned still has to say something useful.
    [InlineData(UpdateAvailability.UpdateReady, null, "update ready to install")]
    [InlineData(UpdateAvailability.NotInstalled, null, "updates are available only in the installed app")]
    public void Describe(UpdateAvailability state, string? latest, string expected)
        => Assert.Equal(expected, VersionDisplay.Describe(state, latest));

    [Fact]
    public void DescribeIgnoresAVersionThatDoesNotBelongToTheState()
    {
        // "up to date · 0.6.1" would read as an available update rather than the one already running.
        Assert.Equal("up to date", VersionDisplay.Describe(UpdateAvailability.UpToDate, "0.6.1"));
    }

    [Theory]
    [InlineData(UpdateAvailability.Unknown, true)]
    [InlineData(UpdateAvailability.UpToDate, true)]   // re-checking is always allowed
    [InlineData(UpdateAvailability.Failed, true)]     // and retrying a failure especially so
    [InlineData(UpdateAvailability.UpdateReady, true)]
    [InlineData(UpdateAvailability.Checking, false)]  // no concurrent checks
    public void CanCheckWhileInstalled(UpdateAvailability state, bool expected)
        => Assert.Equal(expected, VersionDisplay.CanCheck(state, isInstalled: true));

    [Theory]
    [InlineData(UpdateAvailability.Unknown)]
    [InlineData(UpdateAvailability.UpToDate)]
    [InlineData(UpdateAvailability.Failed)]
    public void NeverCheckWhenNotInstalled(UpdateAvailability state)
        => Assert.False(VersionDisplay.CanCheck(state, isInstalled: false));

    /// <summary>Applying is a separate decision from checking: the Update now button must be dead
    /// until a check has actually found something, so a user cannot press it on faith.</summary>
    [Theory]
    [InlineData(UpdateAvailability.UpdateReady, true)]
    [InlineData(UpdateAvailability.Unknown, false)]
    [InlineData(UpdateAvailability.Checking, false)]
    [InlineData(UpdateAvailability.UpToDate, false)]
    [InlineData(UpdateAvailability.Failed, false)]
    [InlineData(UpdateAvailability.NotInstalled, false)]
    public void CanApplyOnlyWithAStagedUpdate(UpdateAvailability state, bool expected)
        => Assert.Equal(expected, VersionDisplay.CanApply(state, isInstalled: true));

    [Fact]
    public void AnUninstalledBuildCanNeverApply()
        => Assert.False(VersionDisplay.CanApply(UpdateAvailability.UpdateReady, isInstalled: false));
}
