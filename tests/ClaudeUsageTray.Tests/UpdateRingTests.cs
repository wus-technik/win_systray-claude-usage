using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The whole ring decision, which is the one place a mistake would either strand beta users
/// or push a beta at everyone. See docs/superpowers/specs/2026-09-04-beta-release-ring-design.md.</summary>
public class UpdateRingTests
{
    [Fact]
    public void OptedOut_AsksForTheStableChannelWithoutPrereleases()
    {
        var ring = UpdateRing.For(useBetaReleases: false, installedChannel: UpdateRing.StableChannel);

        Assert.Equal("win", ring.Channel);
        Assert.False(ring.IncludePrereleases);
    }

    [Fact]
    public void OptedIn_AsksForTheBetaChannelAndPrereleases()
    {
        var ring = UpdateRing.For(useBetaReleases: true, installedChannel: UpdateRing.StableChannel);

        Assert.Equal("win-beta", ring.Channel);
        // Beta packages are uploaded with `vpk upload github --pre`, so the source has to look at
        // GitHub pre-releases or the beta index is never even fetched.
        Assert.True(ring.IncludePrereleases);
    }

    [Fact]
    public void OptingOutOfABetaInstall_AllowsTheDowngradeBackToStable()
    {
        var ring = UpdateRing.For(useBetaReleases: false, installedChannel: UpdateRing.BetaChannel);

        Assert.Equal("win", ring.Channel);
        Assert.True(ring.AllowVersionDowngrade);
    }

    [Fact]
    public void SteadyStateOnStable_NeverAllowsADowngrade()
    {
        var ring = UpdateRing.For(useBetaReleases: false, installedChannel: UpdateRing.StableChannel);

        Assert.False(ring.AllowVersionDowngrade);
    }

    [Fact]
    public void SteadyStateOnBeta_NeverAllowsADowngrade()
    {
        // A beta ring that lags behind stable must not be able to drag an opted-in user backwards,
        // and a retracted in-ring release must not either.
        var ring = UpdateRing.For(useBetaReleases: true, installedChannel: UpdateRing.BetaChannel);

        Assert.False(ring.AllowVersionDowngrade);
    }

    [Theory]
    [InlineData("WIN-BETA")]
    [InlineData("Win-Beta")]
    [InlineData(" win-beta ")]
    public void InstalledChannelComparisonIgnoresCaseAndPadding(string installedChannel)
    {
        // The channel is read back from the package manifest, not from our own constant.
        var ring = UpdateRing.For(useBetaReleases: false, installedChannel);

        Assert.True(ring.AllowVersionDowngrade);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("win")]
    [InlineData("osx")]
    public void AnythingButTheBetaChannelCountsAsStableWhenOptingOut(string? installedChannel)
    {
        // Dev runs, portable builds and any future channel: none of them justifies enabling downgrades.
        var ring = UpdateRing.For(useBetaReleases: false, installedChannel);

        Assert.False(ring.AllowVersionDowngrade);
    }

    // ---- no recorded choice: follow the channel this build was installed from ----

    [Fact]
    public void NoChoiceOnABetaInstall_StaysOnBetas()
    {
        // Someone who ran the beta installer has never touched the checkbox. Reading that as "stable"
        // makes the installer undo itself: the first check would offer stable as a downgrade.
        var ring = UpdateRing.For(useBetaReleases: null, installedChannel: UpdateRing.BetaChannel);

        Assert.Equal("win-beta", ring.Channel);
        Assert.True(ring.IncludePrereleases);
        Assert.False(ring.AllowVersionDowngrade);
    }

    [Fact]
    public void NoChoiceOnAStableInstall_StaysOnStable()
    {
        var ring = UpdateRing.For(useBetaReleases: null, installedChannel: UpdateRing.StableChannel);

        Assert.Equal("win", ring.Channel);
        Assert.False(ring.IncludePrereleases);
        Assert.False(ring.AllowVersionDowngrade);
    }

    [Fact]
    public void NoChoiceOutsideAnInstall_StaysOnStable()
        => Assert.Equal("win", UpdateRing.For(useBetaReleases: null, installedChannel: null).Channel);

    [Fact]
    public void AnExplicitOptOutBeatsABetaInstall()
    {
        // The difference between "never chose" and "chose stable": only the latter leaves the ring.
        var ring = UpdateRing.For(useBetaReleases: false, installedChannel: UpdateRing.BetaChannel);

        Assert.Equal("win", ring.Channel);
        Assert.True(ring.AllowVersionDowngrade);
    }

    [Theory]
    [InlineData("win-beta", true)]
    [InlineData("WIN-BETA", true)]
    [InlineData("win", false)]
    [InlineData(null, false)]
    public void IsBetaChannelIsWhatAnUnsetSettingFollows(string? installedChannel, bool expected)
        => Assert.Equal(expected, UpdateRing.IsBetaChannel(installedChannel));

    [Fact]
    public void TheTwoChannelNamesAreTheVelopackDefaultsForWindows()
    {
        // `vpk pack` defaults to --channel win, so the stable name must stay exactly that: changing it
        // would orphan every existing install, whose manifest records "win".
        Assert.Equal("win", UpdateRing.StableChannel);
        Assert.Equal("win-beta", UpdateRing.BetaChannel);
    }
}
