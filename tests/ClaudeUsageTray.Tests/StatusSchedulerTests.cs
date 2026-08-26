using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class StatusSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshScheduler_AllowsFetch() => Assert.True(new StatusScheduler().CanFetch(T0));

    [Fact]
    public void Floor_BlocksFor30Seconds_ThenAllows()
    {
        var s = new StatusScheduler();
        s.RecordAttempt(T0);
        Assert.False(s.CanFetch(T0.AddSeconds(29)));
        Assert.True(s.CanFetch(T0.AddSeconds(30)));
    }

    [Fact]
    public void FailureBackoff_Escalates1_5_15_AndCaps()
    {
        var s = new StatusScheduler();
        s.RecordFailure(T0);
        Assert.False(s.CanFetch(T0.AddSeconds(59)));
        Assert.True(s.CanFetch(T0.AddMinutes(1)));
        s.RecordFailure(T0.AddMinutes(1));
        Assert.False(s.CanFetch(T0.AddMinutes(5)));
        Assert.True(s.CanFetch(T0.AddMinutes(6)));            // 1 + 5
        s.RecordFailure(T0.AddMinutes(6));
        Assert.True(s.CanFetch(T0.AddMinutes(21)));           // 6 + 15
        s.RecordFailure(T0.AddMinutes(21));
        Assert.False(s.CanFetch(T0.AddMinutes(35)));          // still 15 (capped)
        Assert.True(s.CanFetch(T0.AddMinutes(36)));
    }

    [Fact]
    public void Success_ResetsFailureStreak()
    {
        var s = new StatusScheduler();
        s.RecordFailure(T0);
        s.RecordFailure(T0.AddMinutes(1));
        s.RecordSuccess();
        s.RecordFailure(T0.AddMinutes(30));
        Assert.True(s.CanFetch(T0.AddMinutes(31)));           // back to 1 min, not 15
    }
}