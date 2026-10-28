using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class CodexIconTests
{
    [Theory]
    [InlineData(CodexState.Unknown, DisplayMode.Both, false, false)]
    [InlineData(CodexState.Unavailable, DisplayMode.Both, false, false)]
    [InlineData(CodexState.Available, DisplayMode.Both, true, true)]
    [InlineData(CodexState.Available, DisplayMode.FiveHour, true, false)]
    [InlineData(CodexState.Available, DisplayMode.SevenDay, false, true)]
    public void WantedIcons_RequireBothAvailabilityAndTheDisplayMode(
        CodexState state, DisplayMode mode, bool five, bool seven)
    {
        var wanted = IconPresence.ForCodex(state, mode);

        Assert.Equal(five, wanted.Five);
        Assert.Equal(seven, wanted.Seven);
    }

    /// <summary>Claude's pair does not depend on availability: its icons exist from startup and show
    /// the no-data badge until data arrives. Only the second provider appears and disappears.</summary>
    [Theory]
    [InlineData(DisplayMode.Both, true, true)]
    [InlineData(DisplayMode.FiveHour, true, false)]
    [InlineData(DisplayMode.SevenDay, false, true)]
    public void ClaudeIcons_FollowTheDisplayModeAlone(DisplayMode mode, bool five, bool seven)
    {
        var wanted = IconPresence.ForClaude(mode);

        Assert.Equal(five, wanted.Five);
        Assert.Equal(seven, wanted.Seven);
    }
}
