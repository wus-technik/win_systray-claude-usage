using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class SourceSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Settings Defaults = new(); // 15 min, 3 h

    private static UsageSnapshot Cli(TimeSpan age)
        => new(Now - age, new WindowUsage(10, Now.AddHours(1)), new WindowUsage(20, Now.AddDays(1)));

    private static UsageSnapshot Desktop(TimeSpan age)
        => new(Now - age, new WindowUsage(11, null), new WindowUsage(21, null)) { Source = UsageSource.DesktopHistory };

    [Fact]
    public void FreshCli_BeatsFreshDesktop()
    {
        var choice = SourceSelection.Choose(Cli(TimeSpan.FromMinutes(2)), Desktop(TimeSpan.FromMinutes(1)), Now, Defaults);
        Assert.Equal(UsageSource.ClaudeCode, choice.Snapshot!.Source);
        Assert.False(choice.Stale);
    }

    [Fact]
    public void StaleCli_YieldsToFreshDesktop()
    {
        var choice = SourceSelection.Choose(Cli(TimeSpan.FromDays(7)), Desktop(TimeSpan.FromMinutes(80)), Now, Defaults);
        Assert.Equal(UsageSource.DesktopHistory, choice.Snapshot!.Source);
        Assert.False(choice.Stale);
    }

    [Fact]
    public void DesktopAllowance_IsHours_NotStalenessMinutes()
    {
        // 80 min is far past 15 min but well inside 3 h.
        var choice = SourceSelection.Choose(null, Desktop(TimeSpan.FromMinutes(80)), Now, Defaults);
        Assert.False(choice.Stale);

        var tight = new Settings { DesktopStalenessHours = 1 };
        Assert.True(SourceSelection.Choose(null, Desktop(TimeSpan.FromMinutes(80)), Now, tight).Stale);
    }

    [Fact]
    public void BothStale_NewerWins_Flagged()
    {
        var choice = SourceSelection.Choose(Cli(TimeSpan.FromHours(5)), Desktop(TimeSpan.FromHours(4)), Now, Defaults);
        Assert.Equal(UsageSource.DesktopHistory, choice.Snapshot!.Source);
        Assert.True(choice.Stale);

        choice = SourceSelection.Choose(Cli(TimeSpan.FromHours(4)), Desktop(TimeSpan.FromHours(5)), Now, Defaults);
        Assert.Equal(UsageSource.ClaudeCode, choice.Snapshot!.Source);
        Assert.True(choice.Stale);
    }

    [Fact]
    public void OnlyCli_Stale_IsShownFlagged()
    {
        var choice = SourceSelection.Choose(Cli(TimeSpan.FromHours(1)), null, Now, Defaults);
        Assert.Equal(UsageSource.ClaudeCode, choice.Snapshot!.Source);
        Assert.True(choice.Stale);
    }

    [Fact]
    public void OnlyDesktop_Stale_IsShownFlagged()
    {
        var choice = SourceSelection.Choose(null, Desktop(TimeSpan.FromDays(20)), Now, Defaults);
        Assert.Equal(UsageSource.DesktopHistory, choice.Snapshot!.Source);
        Assert.True(choice.Stale);
    }

    [Fact]
    public void Neither_IsNullNotStale()
    {
        var choice = SourceSelection.Choose(null, null, Now, Defaults);
        Assert.Null(choice.Snapshot);
        Assert.False(choice.Stale);
    }

    [Fact]
    public void ExactlyAtTheCutoff_IsStillFresh()
        => Assert.False(SourceSelection.Choose(Cli(TimeSpan.FromMinutes(15)), null, Now, Defaults).Stale);

    // ---- clock skew ----

    [Fact]
    public void FourMinutesInTheFuture_CountsAsFresh()
    {
        var choice = SourceSelection.Choose(null, Desktop(TimeSpan.FromMinutes(-4)), Now, Defaults);
        Assert.False(choice.Stale);
        Assert.Equal(TimeSpan.Zero, SourceSelection.Age(choice.Snapshot!, Now));
    }

    [Fact]
    public void AnHourInTheFuture_IsStale_AndLosesToAFreshAlternative()
    {
        var future = Desktop(TimeSpan.FromHours(-1));
        Assert.Equal(TimeSpan.MaxValue, SourceSelection.Age(future, Now));

        var choice = SourceSelection.Choose(Cli(TimeSpan.FromMinutes(10)), future, Now, Defaults);
        Assert.Equal(UsageSource.ClaudeCode, choice.Snapshot!.Source);
        Assert.False(choice.Stale);

        Assert.True(SourceSelection.Choose(null, future, Now, Defaults).Stale);
    }

    [Fact]
    public void BothStale_FutureOneLosesToAnOldRealOne()
    {
        var choice = SourceSelection.Choose(Cli(TimeSpan.FromDays(2)), Desktop(TimeSpan.FromHours(-1)), Now, Defaults);
        Assert.Equal(UsageSource.ClaudeCode, choice.Snapshot!.Source);
        Assert.True(choice.Stale);
    }
}
