using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class PopupRowsTests
{
    private static ScopedLimit Limit(string label, int percent, bool active = false)
        => new(label, null, percent, null, active);

    private static List<ScopedLimit> Many(int count, bool active, string prefix = "m")
        => [.. Enumerable.Range(1, count).Select(i => Limit($"{prefix}{i}", 100 - i, active))];

    [Fact]
    public void SixInactive_ShowsFourAndHidesTwo()
    {
        var rows = PopupRows.ForScopedLimits(Many(6, active: false));

        Assert.Equal(4, rows.Visible.Count);
        Assert.Equal(2, rows.HiddenCount);
    }

    [Fact]
    public void ExactlyFour_HidesNothing()
    {
        var rows = PopupRows.ForScopedLimits(Many(4, active: false));

        Assert.Equal(4, rows.Visible.Count);
        Assert.Equal(0, rows.HiddenCount);
    }

    [Fact]
    public void ActiveRowsAreNeverHiddenByTheCap()
    {
        List<ScopedLimit> limits = [.. Many(5, active: true, "a"), .. Many(1, active: false, "i")];

        var rows = PopupRows.ForScopedLimits(limits);

        Assert.Equal(5, rows.Visible.Count);
        Assert.All(rows.Visible, l => Assert.True(l.IsActive));
        Assert.Equal(1, rows.HiddenCount);   // counts only the hidden inactive row
    }

    [Fact]
    public void ActiveRowsConsumeCapSlotsBeforeInactiveOnes()
    {
        List<ScopedLimit> limits = [Limit("active", 10, active: true), .. Many(6, active: false)];

        var rows = PopupRows.ForScopedLimits(limits);

        Assert.Equal(4, rows.Visible.Count);
        Assert.Equal("active", rows.Visible[0].Label);
        Assert.Equal(3, rows.HiddenCount);
    }

    [Fact]
    public void Empty_ShowsNothing()
    {
        var rows = PopupRows.ForScopedLimits([]);

        Assert.Empty(rows.Visible);
        Assert.Equal(0, rows.HiddenCount);
    }
}
