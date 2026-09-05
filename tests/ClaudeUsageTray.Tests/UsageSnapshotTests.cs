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
}
