using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class CliArgsTests
{
    private static StubOptions Ok(params string[] args)
    {
        var result = CliArgs.Parse(args, environmentToken: null);
        Assert.Null(result.Error);
        return result.Options!;
    }

    [Fact]
    public void NoArgumentsMeansInteractiveWithNoRingChosen()
    {
        var o = Ok();
        Assert.Null(o.Ring);
        Assert.False(o.Silent);
        Assert.Null(o.Token);
        Assert.False(o.ShowVersion);
        Assert.False(o.ShowHelp);
    }

    [Theory]
    [InlineData("--ring", "beta", Ring.Beta)]
    [InlineData("--ring", "Stable", Ring.Stable)]
    [InlineData("--RING", "BETA", Ring.Beta)]
    public void RingIsParsedCaseInsensitively(string flag, string value, Ring expected)
        => Assert.Equal(expected, Ok(flag, value).Ring);

    [Fact]
    public void RingAcceptsTheEqualsForm() => Assert.Equal(Ring.Beta, Ok("--ring=beta").Ring);

    [Fact]
    public void UnknownRingIsAnError()
    {
        var r = CliArgs.Parse(["--ring", "nightly"], null);
        Assert.Null(r.Options);
        Assert.Contains("nightly", r.Error);
    }

    [Fact]
    public void RingWithoutAValueIsAnError() => Assert.NotNull(CliArgs.Parse(["--ring"], null).Error);

    [Fact]
    public void UnknownFlagIsAnError()
    {
        var r = CliArgs.Parse(["--installto", "C:\\x"], null);
        Assert.Contains("--installto", r.Error);
    }

    [Fact]
    public void SilentAndTokenAreParsed()
    {
        var o = Ok("--silent", "--token", "ghp_abc");
        Assert.True(o.Silent);
        Assert.Equal("ghp_abc", o.Token);
    }

    [Fact]
    public void EnvironmentTokenIsUsedWhenNoFlagGiven()
        => Assert.Equal("env-token", CliArgs.Parse([], "env-token").Options!.Token);

    [Fact]
    public void TokenFlagBeatsTheEnvironment()
        => Assert.Equal("flag", CliArgs.Parse(["--token", "flag"], "env-token").Options!.Token);

    [Fact]
    public void BlankEnvironmentTokenCountsAsAbsent()
        => Assert.Null(CliArgs.Parse([], "   ").Options!.Token);

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void HelpFlags(string flag) => Assert.True(Ok(flag).ShowHelp);

    [Fact]
    public void VersionFlag() => Assert.True(Ok("--version").ShowVersion);

    [Fact]
    public void SilentWithoutRingParsesFine()
    {
        // Whether that is allowed depends on the install state, which is Decision's job, not the parser's.
        var o = Ok("--silent");
        Assert.True(o.Silent);
        Assert.Null(o.Ring);
    }

    [Fact]
    public void UsageNamesEveryFlag()
    {
        foreach (var flag in new[] { "--ring", "--silent", "--token", "--version", "--help" })
            Assert.Contains(flag, CliArgs.Usage);
    }
}
