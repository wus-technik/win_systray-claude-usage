using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The one place the app's own name and its creator live. Window titles are asserted here
/// rather than in each form's tests so the three surfaces cannot drift apart again.</summary>
public class AppInfoTests
{
    [Fact]
    public void WindowTitlesShareOneName()
        => Assert.Equal("Claude Usage — Settings", AppInfo.Window("Settings"));

    [Fact]
    public void TheCreatorKeepsItsAmpersandInPlainText()
        => Assert.Equal("W&S Technik GmbH", AppInfo.Creator);

    [Fact]
    public void TheLabelFormDoublesTheAmpersandSoItIsNotEatenAsAMnemonic()
    {
        // A WinForms label draws a lone & as a mnemonic prefix: "WS Technik GmbH", S underlined.
        Assert.Equal("W&&S Technik GmbH", AppInfo.CreatorForLabel);
    }

    [Fact]
    public void TheAumidIsVelopackDotPackId()
        => Assert.Equal("velopack.WusTechnik.ClaudeUsageTray", AppInfo.Aumid);

    /// <summary>Velopack writes "velopack.{packId}" onto the Start Menu shortcut, and that string is
    /// the only thing that lets a toast show. Nothing else checks that the id in AppInfo and the id
    /// handed to vpk agree, and a mismatch costs every notification with no other symptom.</summary>
    [Theory]
    [InlineData("build/build-release.ps1")]
    [InlineData(".github/workflows/release.yml")]
    public void ThePackIdMatchesWhatVpkIsGiven(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        Assert.Contains($"--packId {AppInfo.PackId} ", text);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build", "build-release.ps1")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
}
