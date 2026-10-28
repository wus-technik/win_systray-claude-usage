using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class ScopedPeriodTests
{
    [Fact]
    public void Weekly_IsSevenDaysAndSaysWeekly()
    {
        Assert.Equal(TimeSpan.FromDays(7), ScopedPeriods.Length(ScopedPeriod.Weekly));
        Assert.Equal("weekly", ScopedPeriods.Word(ScopedPeriod.Weekly));
    }

    [Fact]
    public void Monthly_IsThirtyDaysAndSaysMonthly()
    {
        Assert.Equal(TimeSpan.FromDays(30), ScopedPeriods.Length(ScopedPeriod.Monthly));
        Assert.Equal("monthly", ScopedPeriods.Word(ScopedPeriod.Monthly));
    }

    /// <summary>Default stays weekly, so every existing construction site keeps its meaning without
    /// being edited.</summary>
    [Fact]
    public void ScopedLimit_DefaultsToWeekly()
    {
        var limit = new ScopedLimit("Fable", null, 50, null, true);
        Assert.Equal(ScopedPeriod.Weekly, limit.Period);
    }
}
