using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class UsageSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Source_DefaultsToClaudeCode()
        => Assert.Equal(UsageSource.ClaudeCode, new UsageSnapshot(Now, null, null).Source);

    [Fact]
    public void Source_CanBeSetToDesktopHistory()
    {
        var s = new UsageSnapshot(Now, null, null) { Source = UsageSource.DesktopHistory };
        Assert.Equal(UsageSource.DesktopHistory, s.Source);
    }

    [Fact]
    public void WindowUsage_Origin_DefaultsToReported()
    {
        Assert.Equal(ResetOrigin.Reported, new WindowUsage(50, null).Origin);
    }

    [Fact]
    public void WindowUsage_Origin_IsInitOnlyAndNotPositional()
    {
        // Positional shape is unchanged: two-argument construction and two-part deconstruction
        // still work, so no existing call site had to be touched.
        var (percent, resetsAt) = new WindowUsage(50, null) { Origin = ResetOrigin.Inferred };
        Assert.Equal(50, percent);
        Assert.Null(resetsAt);
    }

    [Fact]
    public void WindowUsage_Origin_ParticipatesInEquality()
    {
        var reported = new WindowUsage(50, null);
        var inferred = reported with { Origin = ResetOrigin.Inferred };
        Assert.NotEqual(reported, inferred);
    }
}
