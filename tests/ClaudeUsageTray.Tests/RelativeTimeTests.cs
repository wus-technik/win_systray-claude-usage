using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1m ago")]
    [InlineData(61, "1m ago")]                     // 61s should be 1m ago (rounds down)
    [InlineData(119, "1m ago")]                    // 119s should be 1m ago (rounds down)
    [InlineData(150, "2m ago")]                    // 150s should be 2m ago
    [InlineData(4 * 60, "4m ago")]
    [InlineData(2 * 3600 + 13 * 60, "2h 13m ago")]
    [InlineData(2 * 3600, "2h ago")]
    [InlineData(3 * 86400 + 20 * 3600, "3d 20h ago")]
    [InlineData(3 * 86400, "3d ago")]
    public void Ago(int secondsBefore, string expected)
        => Assert.Equal(expected, RelativeTime.Ago(Now.AddSeconds(-secondsBefore), Now));

    [Theory]
    [InlineData(45 * 60, "45m")]
    [InlineData(30, "1m")]                       // sub-minute rounds up to 1m
    [InlineData(90, "2m")]                       // 90s rounds up to 2m
    [InlineData(2 * 3600 + 13 * 60, "2h 13m")]
    [InlineData(3 * 3600, "3h")]
    [InlineData(3 * 86400 + 20 * 3600, "3d 20h")]
    public void In(int secondsAhead, string expected)
        => Assert.Equal(expected, RelativeTime.In(Now.AddSeconds(secondsAhead), Now));

    [Theory]
    [InlineData(0)]
    [InlineData(-3600)] // target already passed
    public void In_PastOrNow_ReturnsNow(int secondsAhead)
        => Assert.Equal("now", RelativeTime.In(Now.AddSeconds(secondsAhead), Now));
}
