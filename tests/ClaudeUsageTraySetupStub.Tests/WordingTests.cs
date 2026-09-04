using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class WordingTests
{
    private static ResolvedBuild Api(Ring ring, string version) => new(ring, Rings.Channel(ring), SemVer.TryParse(version),
        new Uri("https://example.test/x.exe"), "sha256:00", ResolvedVia.Api);

    [Fact]
    public void StableNamesTheLatestStableRelease()
        => Assert.Contains("latest stable", ResolvedBuild.LatestOnChannel(Ring.Stable).Describe());

    [Fact]
    public void BetaPrereleaseNamesTheVersionAndSaysPreRelease()
    {
        var text = Api(Ring.Beta, "0.7.3-beta.1").Describe();
        Assert.Contains("0.7.3-beta.1", text);
        Assert.Contains("pre-release build", text);
    }

    [Fact]
    public void BetaOnTheStableMirrorSaysSoPlainly()
    {
        // The user asked for beta and is getting stable content; hiding that is the failure the spec forbids.
        var text = Api(Ring.Beta, "0.7.2").Describe();
        Assert.Contains("0.7.2", text);
        Assert.Contains("stable build", text);
        Assert.DoesNotContain("pre-release build", text);
    }

    [Fact]
    public void BetaViaFallbackSaysTheApiWasUnavailable()
    {
        var text = ResolvedBuild.LatestOnChannel(Ring.Beta).Describe();
        Assert.Contains("could not be read", text);
        Assert.Contains("stable build", text);
        Assert.Contains("newer pre-release may exist", text);
    }

    [Fact]
    public void SwitchToBetaPromisesStagingNotACompletedMove()
    {
        var text = Wording.SwitchStaged(Ring.Beta);
        Assert.StartsWith("Beta releases enabled.", text);
        Assert.Contains("Restart to update", text);
        Assert.Contains("in the background", text);
    }

    [Fact]
    public void SwitchToStableWarnsAboutTheDowngrade()
    {
        var text = Wording.SwitchStaged(Ring.Stable);
        Assert.StartsWith("Beta releases disabled.", text);
        Assert.Contains("older version", text);
        Assert.Contains("Restart to update", text);
    }

    [Fact]
    public void InstalledWordingNamesTheRing()
    {
        Assert.Contains("beta ring", Wording.Installed(Api(Ring.Beta, "0.7.3-beta.1")));
        Assert.Contains("stable ring", Wording.Installed(ResolvedBuild.LatestOnChannel(Ring.Stable)));
    }
}
