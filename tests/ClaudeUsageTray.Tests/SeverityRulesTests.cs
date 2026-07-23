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
}
