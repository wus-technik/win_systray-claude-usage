using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The invariant the settings dialog has to make unreachable, kept out of the form so it
/// can be tested: 0 ≤ orange &lt; red ≤ 100.</summary>
public class ThresholdRulesTests
{
    [Theory]
    [InlineData(50, 85)]   // the defaults
    [InlineData(0, 1)]     // orange may sit at zero
    [InlineData(0, 100)]   // widest valid pair
    [InlineData(99, 100)]  // narrowest valid pair
    public void ValidPairs(int orange, int red)
        => Assert.True(ThresholdRules.IsValidPair(orange, red));

    [Theory]
    [InlineData(50, 50)]    // red must be strictly above orange
    [InlineData(90, 50)]    // inverted
    [InlineData(-1, 85)]    // orange below zero
    [InlineData(50, 101)]   // red above the cap
    [InlineData(100, 100)]  // no room left for red
    public void InvalidPairs(int orange, int red)
        => Assert.False(ThresholdRules.IsValidPair(orange, red));

    [Theory]
    // Clamp moves as little as it can and always returns a valid pair. Orange is the anchor:
    // it is the value the user set most recently in the dialog's wiring, so red yields to it.
    [InlineData(50, 85, 50, 85)]    // already valid → untouched
    [InlineData(90, 50, 90, 91)]    // inverted → red lifted just above orange
    [InlineData(50, 50, 50, 51)]    // equal → red lifted
    [InlineData(-5, 85, 0, 85)]     // orange floored
    [InlineData(50, 140, 50, 100)]  // red capped
    [InlineData(100, 85, 99, 100)]  // orange at the cap leaves no room → orange yields one step
    [InlineData(140, 20, 99, 100)]  // both out of range
    public void ClampProducesTheNearestValidPair(int orange, int red, int expectedOrange, int expectedRed)
        => Assert.Equal((expectedOrange, expectedRed), ThresholdRules.Clamp(orange, red));

    [Fact]
    public void ClampAlwaysReturnsAValidPair()
    {
        for (int orange = -5; orange <= 105; orange++)
        for (int red = -5; red <= 105; red++)
        {
            var (o, r) = ThresholdRules.Clamp(orange, red);
            Assert.True(ThresholdRules.IsValidPair(o, r), $"Clamp({orange}, {red}) → ({o}, {r})");
        }
    }

    [Fact]
    public void DefaultsAreTheDocumentedPair()
    {
        Assert.Equal(50, ThresholdRules.DefaultOrange);
        Assert.Equal(85, ThresholdRules.DefaultRed);
        Assert.Equal(15, ThresholdRules.DefaultStalenessMinutes);
        Assert.True(ThresholdRules.IsValidPair(ThresholdRules.DefaultOrange, ThresholdRules.DefaultRed));
    }
}
