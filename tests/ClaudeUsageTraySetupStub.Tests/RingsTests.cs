using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class RingsTests
{
    [Fact]
    public void ChannelNamesComeFromTheSharedUpdateRingSource()
    {
        // The app's manifest records these; the stub must derive asset names from the same strings.
        Assert.Equal("win", Rings.Channel(Ring.Stable));
        Assert.Equal("win-beta", Rings.Channel(Ring.Beta));
    }

    [Theory]
    [InlineData("win-beta", Ring.Beta)]
    [InlineData("WIN-BETA", Ring.Beta)]
    [InlineData("win", Ring.Stable)]
    [InlineData(null, Ring.Stable)]
    public void FromChannelFollowsIsBetaChannel(string? channel, Ring expected)
        => Assert.Equal(expected, Rings.FromChannel(channel));

    [Theory]
    [InlineData("win", "WusTechnik.ClaudeUsageTray-win-Setup.exe")]
    [InlineData("win-beta", "WusTechnik.ClaudeUsageTray-win-beta-Setup.exe")]
    public void SetupAssetNameIsDerivedFromTheChannel(string channel, string expected)
        => Assert.Equal(expected, Rings.SetupAssetName(channel));

    [Fact]
    public void LatestAssetUrlUsesTheReleasesLatestRedirect()
    {
        // GitHub's redirect *is* the version independence for stable: no API call, no rate limit.
        Assert.Equal(
            "https://github.com/wus-technik/win_systray-claude-usage/releases/latest/download/WusTechnik.ClaudeUsageTray-win-beta-Setup.exe",
            Rings.LatestAssetUrl("win-beta").ToString());
    }

    [Fact]
    public void StubExitCodesCannotCollideWithSetupExeCodes()
    {
        // Setup.exe's own code is propagated verbatim, so ours live in a range it never uses.
        int[] own = [ExitCode.BadArguments, ExitCode.ResolutionFailed, ExitCode.DownloadFailed,
            ExitCode.AmbiguousRequest, ExitCode.AppControlFailed, ExitCode.Cancelled];
        Assert.Equal(own.Length, own.Distinct().Count());
        Assert.All(own, code => Assert.InRange(code, 3001, 3006));
        Assert.Equal(0, ExitCode.Converged);
    }
}
