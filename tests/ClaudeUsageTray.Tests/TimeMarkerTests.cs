using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class TimeMarkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThreeHoursLeftOfFiveHourWindowIsFortyPercentElapsed()
        => AssertFraction(0.4, TimeMarker.ElapsedFraction(Now.AddHours(3), TimeSpan.FromHours(5), Now));

    [Fact]
    public void TwoDaysLeftOfSevenDayWindowIsFiveSeventhsElapsed()
        => AssertFraction(5d / 7d, TimeMarker.ElapsedFraction(Now.AddDays(2), TimeSpan.FromDays(7), Now));

    [Fact]
    public void ResetDueNowIsFullyElapsed()
        => AssertFraction(1.0, TimeMarker.ElapsedFraction(Now, TimeSpan.FromHours(5), Now));

    [Fact]
    public void ResetOnePeriodOutIsNotElapsedAtAll()
        => AssertFraction(0.0, TimeMarker.ElapsedFraction(Now.AddHours(5), TimeSpan.FromHours(5), Now));

    [Fact]
    public void StaleResetInThePastIsHidden()
        => Assert.Null(TimeMarker.ElapsedFraction(Now.AddMinutes(-1), TimeSpan.FromHours(5), Now));

    [Fact]
    public void ResetBeyondOnePeriodIsHidden()
        => Assert.Null(TimeMarker.ElapsedFraction(Now.AddHours(6), TimeSpan.FromHours(5), Now));

    [Fact]
    public void MissingResetTimeIsHidden()
        => Assert.Null(TimeMarker.ElapsedFraction(null, TimeSpan.FromHours(5), Now));

    [Fact]
    public void ZeroPeriodIsHidden()
        => Assert.Null(TimeMarker.ElapsedFraction(Now.AddHours(3), TimeSpan.Zero, Now));

    [Fact]
    public void NegativePeriodIsHidden()
        => Assert.Null(TimeMarker.ElapsedFraction(Now.AddHours(3), TimeSpan.FromHours(-5), Now));

    private static void AssertFraction(double expected, double? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value, precision: 6);
    }
}
