using System.Text.Json;
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class ReleaseSelectionTests
{
    private static List<GitHubRelease> Parse(string json)
        => JsonSerializer.Deserialize(json, GitHubJsonContext.Default.ListGitHubRelease)!;

    private static string Release(string tag, bool prerelease, params string[] assets)
    {
        var list = string.Join(",", assets.Select(a =>
            "{ \"name\": \"" + a + "\", \"browser_download_url\": \"https://github.com/x/y/releases/download/" + tag + "/" + a + "\", \"digest\": \"sha256:0011\" }"));
        return "{ \"tag_name\": \"" + tag + "\", \"draft\": false, \"prerelease\": " + (prerelease ? "true" : "false") + ", \"assets\": [" + list + "] }";
    }

    private const string StableAsset = "WusTechnik.ClaudeUsageTray-win-Setup.exe";
    private const string BetaAsset = "WusTechnik.ClaudeUsageTray-win-beta-Setup.exe";

    [Fact]
    public void PicksTheHighestSemVerByTagNotByListOrder()
    {
        // The API lists newest-published first; a hotfix beta published after a later beta would win by date.
        var releases = Parse($"[{Release("v0.7.3-beta.2", true, BetaAsset)}, {Release("v0.7.3-beta.1", true, BetaAsset)}]");
        releases.Reverse();

        var build = ReleaseSelection.Select(releases, Ring.Beta)!;

        Assert.Equal("0.7.3-beta.2", build.Version!.ToString());
        Assert.Equal(ResolvedVia.Api, build.Via);
        Assert.Equal("win-beta", build.Channel);
        Assert.EndsWith("/v0.7.3-beta.2/" + BetaAsset, build.Url.ToString());
        Assert.Equal("sha256:0011", build.Digest);
    }

    [Fact]
    public void ReleasesWithoutTheChannelAssetAreSkipped()
    {
        var releases = Parse($"[{Release("v0.8.0", false, StableAsset)}, {Release("v0.7.2", false, StableAsset, BetaAsset)}]");

        Assert.Equal("0.7.2", ReleaseSelection.Select(releases, Ring.Beta)!.Version!.ToString());
    }

    [Fact]
    public void NonSemVerTagsAreSkippedNotFatal()
    {
        // The permanent setup-stub release carries no installer and has no version; it must be invisible.
        var releases = Parse($"[{Release("setup-stub", false, "ClaudeUsageTraySetup.exe")}, {Release("v0.7.2", false, BetaAsset)}]");

        Assert.Equal("0.7.2", ReleaseSelection.Select(releases, Ring.Beta)!.Version!.ToString());
    }

    [Fact]
    public void DraftsAreSkipped()
    {
        var json = Release("v9.9.9", true, BetaAsset).Replace("\"draft\": false", "\"draft\": true");
        var releases = Parse($"[{json}, {Release("v0.7.2", false, BetaAsset)}]");

        Assert.Equal("0.7.2", ReleaseSelection.Select(releases, Ring.Beta)!.Version!.ToString());
    }

    [Fact]
    public void TheStableMirrorCountsForTheBetaRing()
    {
        // A stable release newer than every beta is what the beta ring should get — it carries the
        // win-beta mirror precisely so beta users never fall behind.
        var releases = Parse($"[{Release("v0.7.2-beta.2", true, BetaAsset)}, {Release("v0.7.2", false, StableAsset, BetaAsset)}]");

        var build = ReleaseSelection.Select(releases, Ring.Beta)!;
        Assert.Equal("0.7.2", build.Version!.ToString());
        Assert.False(build.Version.IsPrerelease);
    }

    [Fact]
    public void HttpAssetUrlsAreSkipped()
    {
        // The API response is the only input choosing what gets executed on the operator's machine.
        var httpOnly = Release("v0.8.0", false, BetaAsset).Replace("https://", "http://");
        var releases = Parse($"[{httpOnly}, {Release("v0.7.2", false, BetaAsset)}]");

        Assert.Equal("0.7.2", ReleaseSelection.Select(releases, Ring.Beta)!.Version!.ToString());
    }

    [Fact]
    public void NothingUsableYieldsNull()
    {
        Assert.Null(ReleaseSelection.Select([], Ring.Beta));
        Assert.Null(ReleaseSelection.Select(Parse($"[{Release("v0.7.2", false, StableAsset)}]"), Ring.Beta));
    }

    [Fact]
    public void AssetNameMatchIsCaseInsensitive()
    {
        var releases = Parse($"[{Release("v0.7.2", false, BetaAsset.ToUpperInvariant())}]");
        Assert.NotNull(ReleaseSelection.Select(releases, Ring.Beta));
    }

    [Fact]
    public void LatestOnChannelHasNoVersionOrDigest()
    {
        var build = ResolvedBuild.LatestOnChannel(Ring.Beta);
        Assert.Null(build.Version);
        Assert.Null(build.Digest);
        Assert.Equal(ResolvedVia.LatestRedirect, build.Via);
        Assert.Equal(Rings.LatestAssetUrl("win-beta"), build.Url);
    }
}
