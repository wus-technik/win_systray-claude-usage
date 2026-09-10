using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Finding 2 of the final review: an expired inferred reset must read as absent everywhere
/// it is consumed, without touching SnapshotPrecedence's adoption rule. Reported and Stated resets
/// are assertions, not guesses, and must be unaffected even when they too lie in the past.</summary>
public class ResetPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InferredReset_InTheFuture_IsUnchanged()
    {
        var usage = new WindowUsage(55, Now.AddHours(1)) { Origin = ResetOrigin.Inferred };
        Assert.Equal(usage.ResetsAt, ResetPresentation.EffectiveResetsAt(usage, Now));
    }

    [Fact]
    public void InferredReset_ExactlyAtNow_IsTreatedAsAbsent()
    {
        // Mirrors gate 2's own "now < resetsAt" boundary (RelativeTime.In already reads this instant
        // as "now"), so the two never disagree about which side of the line is expired.
        var usage = new WindowUsage(55, Now) { Origin = ResetOrigin.Inferred };
        Assert.Null(ResetPresentation.EffectiveResetsAt(usage, Now));
    }

    [Fact]
    public void InferredReset_InThePast_IsTreatedAsAbsent()
    {
        var usage = new WindowUsage(55, Now.AddMinutes(-45)) { Origin = ResetOrigin.Inferred };
        Assert.Null(ResetPresentation.EffectiveResetsAt(usage, Now));
    }

    [Fact]
    public void ReportedReset_InThePast_IsNotAffected()
    {
        var usage = new WindowUsage(55, Now.AddMinutes(-45));
        Assert.Equal(usage.ResetsAt, ResetPresentation.EffectiveResetsAt(usage, Now));
    }

    [Fact]
    public void StatedReset_InThePast_IsNotAffected()
    {
        var usage = new WindowUsage(55, Now.AddMinutes(-45)) { Origin = ResetOrigin.Stated };
        Assert.Equal(usage.ResetsAt, ResetPresentation.EffectiveResetsAt(usage, Now));
    }

    [Fact]
    public void NoReset_StaysNull()
    {
        var usage = new WindowUsage(55, null) { Origin = ResetOrigin.Inferred };
        Assert.Null(ResetPresentation.EffectiveResetsAt(usage, Now));
    }
}
