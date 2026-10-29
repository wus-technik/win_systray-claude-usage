using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class CodexDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot At(DateTimeOffset when)
        => new(when, new WindowUsage(42, when.AddHours(3)), null) { Source = UsageSource.Codex };

    private static Settings Settings30 => new() { StalenessMinutes = 30 };

    [Fact]
    public void Choose_WithNoSnapshot_IsEmptyAndNotStale()
    {
        var choice = CodexDisplay.Choose(null, Now, Settings30);

        Assert.Null(choice.Snapshot);
        Assert.False(choice.Stale);
    }

    [Fact]
    public void Choose_FreshSnapshot_IsNotStale()
        => Assert.False(CodexDisplay.Choose(At(Now.AddMinutes(-5)), Now, Settings30).Stale);

    [Fact]
    public void Choose_AtTheAllowance_IsNotStale()
        => Assert.False(CodexDisplay.Choose(At(Now.AddMinutes(-30)), Now, Settings30).Stale);

    /// <summary>Past the allowance it degrades to stale, never to blank: the snapshot is still the
    /// best thing known, and blanking a badge the user is watching is worse than dimming it.</summary>
    [Fact]
    public void Choose_PastTheAllowance_IsStaleButStillShown()
    {
        var choice = CodexDisplay.Choose(At(Now.AddMinutes(-31)), Now, Settings30);

        Assert.NotNull(choice.Snapshot);
        Assert.True(choice.Stale);
    }

    /// <summary>A timestamp implausibly far in the future fails every freshness test rather than
    /// reading as brand new — the same clamp SourceSelection.Age applies to Claude's slots.</summary>
    [Fact]
    public void Choose_FutureSnapshot_IsStale()
        => Assert.True(CodexDisplay.Choose(At(Now.AddHours(1)), Now, Settings30).Stale);
    [Fact]
    public void Choose_FailedFetchDimsEvenAFreshSnapshot()
    {
        var snapshot = At(Now);
        var choice = CodexDisplay.Choose(snapshot, Now, Settings30, fetchFailed: true);
        Assert.Same(snapshot, choice.Snapshot);
        Assert.True(choice.Stale);
    }
}
