using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class FetchSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshScheduler_AllowsFetch() => Assert.True(new FetchScheduler().CanFetch(T0));

    [Fact]
    public void Floor_BlocksFor30Seconds_ThenAllows()
    {
        var s = new FetchScheduler();
        s.RecordAttempt(T0);
        Assert.False(s.CanFetch(T0.AddSeconds(29)));
        Assert.True(s.CanFetch(T0.AddSeconds(30)));
    }

    [Fact]
    public void RollingHourCap_BlocksAtLimit_UnblocksWhenOldestAgesOut()
    {
        var s = new FetchScheduler(maxPerHour: 3);
        s.RecordAttempt(T0);
        s.RecordAttempt(T0.AddMinutes(1));
        s.RecordAttempt(T0.AddMinutes(2));
        Assert.False(s.CanFetch(T0.AddMinutes(10)));           // cap reached
        Assert.False(s.CanFetch(T0.AddMinutes(59)));           // still 3 in window
        Assert.True(s.CanFetch(T0.AddMinutes(60)));            // T0 attempt aged out
    }

    [Fact]
    public void RateLimited_WithoutRetryAfter_Blocks15Minutes()
    {
        var s = new FetchScheduler();
        s.RecordAttempt(T0);
        s.RecordRateLimited(T0, retryAfter: null);
        Assert.False(s.CanFetch(T0.AddMinutes(14)));
        Assert.True(s.CanFetch(T0.AddMinutes(15)));
    }

    [Fact]
    public void RateLimited_WithLongRetryAfter_BlocksForRetryAfter()
    {
        var s = new FetchScheduler();
        s.RecordAttempt(T0);
        s.RecordRateLimited(T0, TimeSpan.FromMinutes(45));
        Assert.False(s.CanFetch(T0.AddMinutes(44)));
        Assert.True(s.CanFetch(T0.AddMinutes(45)));
    }

    [Fact]
    public void RateLimited_WithShortRetryAfter_StillBlocks15Minutes()
    {
        var s = new FetchScheduler();
        s.RecordAttempt(T0);
        s.RecordRateLimited(T0, TimeSpan.FromMinutes(2));
        Assert.False(s.CanFetch(T0.AddMinutes(14)));
        Assert.True(s.CanFetch(T0.AddMinutes(15)));
    }

    [Fact]
    public void FailureBackoff_Escalates5_10_20_AndCaps()
    {
        var s = new FetchScheduler();
        s.RecordFailure(T0);
        Assert.False(s.CanFetch(T0.AddMinutes(4)));
        Assert.True(s.CanFetch(T0.AddMinutes(5)));
        s.RecordFailure(T0.AddMinutes(5));
        Assert.False(s.CanFetch(T0.AddMinutes(14)));
        Assert.True(s.CanFetch(T0.AddMinutes(15)));            // 5 + 10
        s.RecordFailure(T0.AddMinutes(15));
        Assert.True(s.CanFetch(T0.AddMinutes(35)));            // 15 + 20
        s.RecordFailure(T0.AddMinutes(35));
        Assert.False(s.CanFetch(T0.AddMinutes(54)));           // still 20 (capped)
        Assert.True(s.CanFetch(T0.AddMinutes(55)));
    }

    [Fact]
    public void Success_ResetsFailureStreak()
    {
        var s = new FetchScheduler();
        s.RecordFailure(T0);
        s.RecordFailure(T0.AddMinutes(5));
        s.RecordSuccess();
        s.RecordFailure(T0.AddMinutes(30));
        Assert.True(s.CanFetch(T0.AddMinutes(35)));            // back to 5 min, not 20
    }

    [Fact]
    public void BudgetCap_AppliesEvenAfterFloorElapsed()
    {
        var s = new FetchScheduler(maxPerHour: 1);
        s.RecordAttempt(T0);
        Assert.False(s.CanFetch(T0.AddMinutes(30)));           // floor long past; budget blocks
        Assert.True(s.CanFetch(T0.AddMinutes(60)));
    }

    [Fact]
    public void DefaultCap_Is20PerRollingHour()
    {
        var s = new FetchScheduler(); // production default
        for (int i = 0; i < 20; i++)
            s.RecordAttempt(T0.AddSeconds(i * 31)); // spaced past the 30 s floor
        Assert.False(s.CanFetch(T0.AddMinutes(30)));            // 21st attempt blocked
        Assert.True(s.CanFetch(T0.AddMinutes(61)));             // oldest aged out
    }

    [Fact]
    public void RateLimited_WithNegativeRetryAfter_StillBlocks15Minutes()
    {
        var s = new FetchScheduler();
        s.RecordAttempt(T0);
        s.RecordRateLimited(T0, TimeSpan.FromMinutes(-5)); // HTTP-date in the past
        Assert.False(s.CanFetch(T0.AddMinutes(14)));
        Assert.True(s.CanFetch(T0.AddMinutes(15)));
    }
}
