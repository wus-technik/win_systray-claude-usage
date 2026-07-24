using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class SnapshotPrecedenceTests
{
    private static UsageSnapshot At(int hour) => new(new DateTimeOffset(2026, 7, 24, hour, 0, 0, TimeSpan.Zero), null, null);

    [Fact] public void NullCandidate_IsNeverNewer() => Assert.False(SnapshotPrecedence.IsNewer(null, At(12)));
    [Fact] public void NullCandidate_AgainstNull_IsNotNewer() => Assert.False(SnapshotPrecedence.IsNewer(null, null));
    [Fact] public void Candidate_AgainstNullCurrent_IsNewer() => Assert.True(SnapshotPrecedence.IsNewer(At(12), null));
    [Fact] public void NewerCandidate_Wins() => Assert.True(SnapshotPrecedence.IsNewer(At(13), At(12)));
    [Fact] public void OlderCandidate_Loses() => Assert.False(SnapshotPrecedence.IsNewer(At(11), At(12)));
    [Fact] public void EqualTimestamp_Loses() => Assert.False(SnapshotPrecedence.IsNewer(At(12), At(12)));
}
