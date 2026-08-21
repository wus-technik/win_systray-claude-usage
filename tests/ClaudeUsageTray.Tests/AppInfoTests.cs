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
}
