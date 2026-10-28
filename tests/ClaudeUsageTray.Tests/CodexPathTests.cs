using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class CodexPathTests
{
    [Fact]
    public void Home_DefaultsToDotCodexUnderTheUserProfile()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        Assert.Equal(expected, CodexPath.Home());
    }

    /// <summary>CODEX_HOME is honoured by the Codex CLI itself, so a relocated home must be followed
    /// or the tray reads a stale token from a directory Codex abandoned.</summary>
    [Fact]
    public void Home_PrefersAnExplicitOverride()
        => Assert.Equal(@"D:\codex", CodexPath.Home(@"D:\codex"));

    [Fact]
    public void Home_IgnoresABlankOverride()
        => Assert.Equal(CodexPath.Home(), CodexPath.Home("   "));

    [Fact]
    public void AuthFile_SitsInTheHome()
        => Assert.Equal(Path.Combine(@"D:\codex", "auth.json"), CodexPath.AuthFile(@"D:\codex"));
}
