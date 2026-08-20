using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class SeverityRulesTests
{
    [Theory]
    [InlineData(0, Severity.Green)]
    [InlineData(49, Severity.Green)]
    [InlineData(50, Severity.Orange)]
    [InlineData(85, Severity.Orange)]
    [InlineData(86, Severity.Red)]
    [InlineData(100, Severity.Red)]
    [InlineData(150, Severity.Red)]
    public void DefaultThresholds(int percent, Severity expected)
        => Assert.Equal(expected, SeverityRules.For(percent));

    [Theory]
    [InlineData(29, Severity.Green)]
    [InlineData(30, Severity.Orange)]
    [InlineData(60, Severity.Orange)]
    [InlineData(61, Severity.Red)]
    public void CustomThresholds(int percent, Severity expected)
        => Assert.Equal(expected, SeverityRules.For(percent, orangeAt: 30, redAbove: 60));

    // ---- pace-based colouring (usage vs. elapsed time) ----

    [Theory]
    // ratio = percent / (elapsedFraction * 100); >= 1.10 orange, >= 1.75 red.
    [InlineData(50, 0.50, Severity.Green)]   // exactly on pace
    [InlineData(54, 0.50, Severity.Green)]   // 1.08x — still inside the band
    [InlineData(55, 0.50, Severity.Orange)]  // 1.10x — over the clock
    [InlineData(69, 0.40, Severity.Orange)]  // 1.725x — just inside the red band
    [InlineData(70, 0.40, Severity.Red)]     // 1.75x — far over the clock
    [InlineData(60, 0.7857, Severity.Green)] // the issue's false alarm: 5.5 of 7 days elapsed
    [InlineData(40, 0.15, Severity.Red)]     // the issue's false calm: 45 min into 5 hours
    public void PaceBands(int percent, double elapsedFraction, Severity expected)
        => Assert.Equal(expected, SeverityRules.ForPace(percent, elapsedFraction));

    [Theory]
    // Below 10% elapsed a small absolute number produces an enormous ratio that means nothing.
    [InlineData(2, 0.003, Severity.Green)]   // one minute into a five-hour window
    [InlineData(9, 0.09, Severity.Green)]
    [InlineData(90, 0.05, Severity.Red)]     // the ceiling still fires inside the dead zone
    public void EarlyPeriodDeadZoneFallsBackToAbsolute(int percent, double elapsedFraction, Severity expected)
        => Assert.Equal(expected, SeverityRules.ForPace(percent, elapsedFraction));

    [Theory]
    // Never worse than the absolute rule below the floor, however steep the ratio.
    [InlineData(3, 0.10, Severity.Green)]    // 0.3x pace by ratio, but 3% used is nothing to warn about
    [InlineData(19, 0.11, Severity.Green)]   // 1.73x pace, 19% used — nothing to warn about
    [InlineData(20, 0.11, Severity.Red)]     // one percent higher, now past the floor
    public void AbsoluteFloorSuppressesPaceEscalation(int percent, double elapsedFraction, Severity expected)
        => Assert.Equal(expected, SeverityRules.ForPace(percent, elapsedFraction));

    [Theory]
    // Running out is running out: near the cap is red even when the pace looks calm.
    [InlineData(86, 0.99, Severity.Red)]
    [InlineData(120, 1.0, Severity.Red)]
    public void AbsoluteCeilingBeatsCalmPace(int percent, double elapsedFraction, Severity expected)
        => Assert.Equal(expected, SeverityRules.ForPace(percent, elapsedFraction));

    [Theory]
    // No trustworthy elapsed fraction (missing or stale reset time) → today's absolute thresholds.
    [InlineData(49, Severity.Green)]
    [InlineData(50, Severity.Orange)]
    [InlineData(86, Severity.Red)]
    public void NullFractionFallsBackToAbsolute(int percent, Severity expected)
        => Assert.Equal(expected, SeverityRules.ForPace(percent, null));

    [Fact]
    public void PaceRespectsCustomThresholdsInFallbackAndCeiling()
    {
        // Custom ceiling: 61% is past redAbove even though the pace is calm.
        Assert.Equal(Severity.Red, SeverityRules.ForPace(61, 0.95, orangeAt: 30, redAbove: 60));
        // Custom fallback: no fraction, 30% is orange under these thresholds.
        Assert.Equal(Severity.Orange, SeverityRules.ForPace(30, null, orangeAt: 30, redAbove: 60));
    }

    [Theory]
    // The ratio shown to the user, rounded to one decimal; null whenever pace did not decide.
    [InlineData(60, 0.5, 1.2)]
    [InlineData(60, 0.7857, 0.8)]
    public void PaceRatioMatchesWhatDecidedTheColour(int percent, double elapsedFraction, double expected)
        => Assert.Equal(expected, Math.Round(SeverityRules.PaceRatio(percent, elapsedFraction)!.Value, 1));

    [Theory]
    [InlineData(2, 0.003)]   // dead zone
    [InlineData(19, 0.5)]    // below the floor
    [InlineData(90, 0.5)]    // above the ceiling
    public void PaceRatioIsNullWhenPaceDidNotDecide(int percent, double elapsedFraction)
        => Assert.Null(SeverityRules.PaceRatio(percent, elapsedFraction));

    [Fact]
    public void PaceRatioIsNullWithoutAFraction()
        => Assert.Null(SeverityRules.PaceRatio(50, null));
}
