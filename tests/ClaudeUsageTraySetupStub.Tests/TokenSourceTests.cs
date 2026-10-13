using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class TokenSourceTests
{
    private static StubOptions Options(string? token, bool onCommandLine)
        => new(null, Silent: false, token, ShowVersion: false, ShowHelp: false, onCommandLine);

    [Fact]
    public void NoTokenReadsAsNone()
        => Assert.Equal("none", TokenSource.Describe(Options(null, onCommandLine: false)));

    [Fact]
    public void ArgumentTokenReadsAsArgument()
        => Assert.Equal("argument", TokenSource.Describe(Options("ghp_abc", onCommandLine: true)));

    [Fact]
    public void EnvironmentTokenNamesTheVariable()
        => Assert.Equal("GH_TOKEN", TokenSource.Describe(Options("ghp_abc", onCommandLine: false)));

    [Fact]
    public void NoDescriptionEverCarriesTheToken()
    {
        foreach (var o in new[] { Options("ghp_secret", true), Options("ghp_secret", false) })
            Assert.DoesNotContain("ghp_secret", TokenSource.Describe(o));
    }

    [Fact]
    public void TheWarningNamesTheSupportedPath()
    {
        Assert.Contains("GH_TOKEN", TokenSource.CommandLineWarning);
        Assert.Contains("--token", TokenSource.CommandLineWarning);
    }
}
