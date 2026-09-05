using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class NoDataReasonTests
{
    private static string Describe(ConfigStatus config, CredentialStatus creds, DesktopHistoryStatus desktop)
        => NoDataReason.Describe(new NoDataFacts(config, creds, desktop));

    [Fact]
    public void DesktopNoSamples_NamesTheDesktopFile()
        => Assert.Equal("Claude Desktop history found, but no samples yet.",
            Describe(ConfigStatus.Missing, CredentialStatus.Missing, DesktopHistoryStatus.NoSamples));

    [Fact]
    public void DesktopUnreadable_NamesTheDesktopFile()
        => Assert.Equal("Claude Desktop history found, but it could not be read.",
            Describe(ConfigStatus.Missing, CredentialStatus.Missing, DesktopHistoryStatus.Unreadable));

    [Fact]
    public void NothingAnywhere_TellsTheUserWhatToOpen()
    {
        var text = Describe(ConfigStatus.Missing, CredentialStatus.Missing, DesktopHistoryStatus.NotFound);
        Assert.Equal("No usage data yet — open Claude Code or Claude Desktop.", text);
        Assert.Equal(NoDataReason.Default, text);
    }

    [Fact]
    public void NoKey_NoCredentials_IsTheDesktopBundledCliCase()
        => Assert.Equal("Claude Code has not cached usage data, and there is no credentials file for a live fetch.",
            Describe(ConfigStatus.NoUsageKey, CredentialStatus.Missing, DesktopHistoryStatus.NotFound));

    [Fact]
    public void NoKey_UnusableCredentials()
        => Assert.Equal("Claude Code has not cached usage data, and its credentials are not usable for a live fetch.",
            Describe(ConfigStatus.NoUsageKey, CredentialStatus.Unusable, DesktopHistoryStatus.NotFound));

    [Fact]
    public void NoKey_ValidCredentials_IsTransient()
        => Assert.Equal("Claude Code has not cached usage data yet — waiting for the first live fetch.",
            Describe(ConfigStatus.NoUsageKey, CredentialStatus.Valid, DesktopHistoryStatus.NotFound));

    [Fact]
    public void KeyPresentButUnparsable()
        => Assert.Equal("Claude Code's cached usage data could not be read.",
            Describe(ConfigStatus.Unreadable, CredentialStatus.Valid, DesktopHistoryStatus.NotFound));

    /// <summary>The desktop rows come first: a found-but-empty desktop file is the more specific fact.</summary>
    [Fact]
    public void DesktopFacts_TakePrecedenceOverConfigFacts()
        => Assert.StartsWith("Claude Desktop history found",
            Describe(ConfigStatus.NoUsageKey, CredentialStatus.Missing, DesktopHistoryStatus.NoSamples));

    /// <summary>Only one state tells the user to run anything.</summary>
    [Theory]
    [InlineData(ConfigStatus.NoUsageKey, CredentialStatus.Missing, DesktopHistoryStatus.NotFound)]
    [InlineData(ConfigStatus.NoUsageKey, CredentialStatus.Valid, DesktopHistoryStatus.NotFound)]
    [InlineData(ConfigStatus.Unreadable, CredentialStatus.Missing, DesktopHistoryStatus.NotFound)]
    [InlineData(ConfigStatus.Missing, CredentialStatus.Missing, DesktopHistoryStatus.NoSamples)]
    public void OtherStates_DoNotSayOpen(ConfigStatus c, CredentialStatus k, DesktopHistoryStatus d)
        => Assert.DoesNotContain("open ", Describe(c, k, d));
}
