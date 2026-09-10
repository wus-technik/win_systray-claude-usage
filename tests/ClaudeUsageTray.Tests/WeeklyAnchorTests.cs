using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Every case supplies an explicit TimeZoneInfo, so these run identically on any machine
/// and in CI — which is the whole reason Core never reads TimeZoneInfo.Local.</summary>
public class WeeklyAnchorTests
{
    // Central European Time: +01:00 winter, +02:00 summer, transitions on the last Sunday in
    // March (02:00 → 03:00) and October (03:00 → 02:00).
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Theory]
    [InlineData("Thu 03:00", DayOfWeek.Thursday, 3, 0)]
    [InlineData("thu 3:00", DayOfWeek.Thursday, 3, 0)]
    [InlineData("  WEDNESDAY   15:05  ", DayOfWeek.Wednesday, 15, 5)]
    [InlineData("Sun 00:00", DayOfWeek.Sunday, 0, 0)]
    [InlineData("Mon 23:59", DayOfWeek.Monday, 23, 59)]
    public void Parses(string text, DayOfWeek day, int hour, int minute)
    {
        var anchor = WeeklyAnchor.TryParse(text);
        Assert.NotNull(anchor);
        Assert.Equal(day, anchor.Day);
        Assert.Equal(new TimeOnly(hour, minute), anchor.TimeOfDay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Thu")]
    [InlineData("03:00")]
    [InlineData("Donnerstag 03:00")]
    [InlineData("Thu 25:00")]
    [InlineData("Thu 03:60")]
    [InlineData("Thu 03:00:00")]
    [InlineData("Xyz 03:00")]
    [InlineData("Thu 3pm")]
    public void JunkAndOutOfRange_ProduceNull(string? text) => Assert.Null(WeeklyAnchor.TryParse(text));

    [Fact]
    public void FormatIsCanonicalAndRoundTrips()
    {
        var anchor = WeeklyAnchor.TryParse("wednesday 3:05")!;
        Assert.Equal("Wed 03:05", anchor.Format());
        Assert.Equal(anchor, WeeklyAnchor.TryParse(anchor.Format()));
    }

    [Fact]
    public void NextReset_IsStrictlyAfterNow()
    {
        // now sits exactly on the anchor: the answer is a week later, not this instant.
        var anchor = WeeklyAnchor.TryParse("Thu 03:00")!;
        var now = new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero);   // a Thursday
        Assert.Equal(now.AddDays(7), WeeklyAnchor.NextReset(anchor, now, Utc));
    }

    [Fact]
    public void NextReset_LaterThisWeek()
    {
        var anchor = WeeklyAnchor.TryParse("Thu 03:00")!;
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);   // Tuesday
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero),
            WeeklyAnchor.NextReset(anchor, now, Utc));
    }

    [Fact]
    public void NextReset_UsesTheZonesOffsetNotUtc()
    {
        var anchor = WeeklyAnchor.TryParse("Thu 03:00")!;
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        // 2026-09-10 03:00 +02:00 (Berlin summer time) == 01:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero),
            WeeklyAnchor.NextReset(anchor, now, Berlin).ToUniversalTime());
    }

    [Fact]
    public void NextReset_SpringForwardGap_UsesTheFirstInstantAfterTheGap()
    {
        // 2027-03-28 is the last Sunday in March; 02:30 local does not exist. The first instant
        // after the gap is 03:00 +02:00 == 01:00 UTC.
        var anchor = WeeklyAnchor.TryParse("Sun 02:30")!;
        var now = new DateTimeOffset(2027, 3, 26, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero),
            WeeklyAnchor.NextReset(anchor, now, Berlin).ToUniversalTime());
    }

    [Fact]
    public void NextReset_FallBackAmbiguity_UsesTheEarlierOccurrence()
    {
        // 2026-10-25, 02:30 local happens twice. The earlier is +02:00 == 00:30 UTC; the later
        // would be +01:00 == 01:30 UTC.
        var anchor = WeeklyAnchor.TryParse("Sun 02:30")!;
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero),
            WeeklyAnchor.NextReset(anchor, now, Berlin).ToUniversalTime());
    }
}
