using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class SemVerTests
{
    private static SemVer V(string s) => SemVer.TryParse(s) ?? throw new Xunit.Sdk.XunitException($"'{s}' should parse");

    [Theory]
    [InlineData("0.7.2", 0, 7, 2, false)]
    [InlineData("v0.7.2", 0, 7, 2, false)]
    [InlineData("0.7.2-beta.1", 0, 7, 2, true)]
    [InlineData("1.2.3+abc123", 1, 2, 3, false)]
    public void ParsesCoreAndPrereleaseFlag(string text, int major, int minor, int patch, bool prerelease)
    {
        var v = V(text);
        Assert.Equal((major, minor, patch, prerelease), (v.Major, v.Minor, v.Patch, v.IsPrerelease));
    }

    [Theory]
    [InlineData("setup-stub")]
    [InlineData("1.0")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0.7.2-beta 1")]
    public void NonSemVerTagsDoNotParse(string? text) => Assert.Null(SemVer.TryParse(text));

    [Fact]
    public void DotNumberedPrereleasesCompareNumerically()
        // The whole reason release-ring.ps1 rejects "-beta1": beta.10 must sort above beta.9.
        => Assert.True(V("0.7.2-beta.10").CompareTo(V("0.7.2-beta.9")) > 0);

    [Fact]
    public void StableBeatsAnyPrereleaseOfTheSameVersion()
        => Assert.True(V("0.7.2").CompareTo(V("0.7.2-beta.2")) > 0);

    [Fact]
    public void ANewerPrereleaseBeatsAnOlderStable()
        => Assert.True(V("0.7.3-beta.1").CompareTo(V("0.7.2")) > 0);

    [Fact]
    public void LongerPrereleaseWinsWhenPrefixesMatch()
        => Assert.True(V("0.7.2-beta.1").CompareTo(V("0.7.2-beta")) > 0);

    [Fact]
    public void NumericIdentifiersSortBelowAlphanumericOnes()
        => Assert.True(V("0.7.2-beta").CompareTo(V("0.7.2-1")) > 0);

    [Fact]
    public void BuildMetadataIsIgnoredInOrdering()
        => Assert.Equal(0, V("0.7.2+a").CompareTo(V("0.7.2+b")));

    [Fact]
    public void ToStringDropsTheVPrefixAndBuild()
        => Assert.Equal("0.7.2-beta.1", V("v0.7.2-beta.1+sha").ToString());
}
