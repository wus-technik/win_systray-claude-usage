using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The rejection paths matter more than the happy path: each is a case where the tray would
/// otherwise show a pace number the data cannot support.</summary>
public class DesktopResetInferenceTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 10, 6, 0, 0, TimeSpan.Zero);
    private const string Org = "11111111-1111-1111-1111-111111111111";

    private static DesktopSample S(double minutes, int? fh, string? org = Org)
        => new(Base.AddMinutes(minutes), org, fh, 20);

    [Fact]
    public void CleanStart_BracketsTheZeroToPositiveTransition()
    {
        // 0 at +0, 40 at +10 → start midway at +5, reset at +5 min + 5 h.
        var samples = new[] { S(0, 0), S(10, 40), S(20, 45) };
        var reset = DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(25));
        Assert.Equal(Base.AddMinutes(5) + UsageValues.FiveHourPeriod, reset);
    }

    [Fact]
    public void WorkThroughBoundary_DecreaseIsAStart()
    {
        // fh never touches 0: 88 → 12 is the boundary. Midpoint of +10 and +20 is +15.
        var samples = new[] { S(0, 80), S(10, 88), S(20, 12) };
        var reset = DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(30));
        Assert.Equal(Base.AddMinutes(15) + UsageValues.FiveHourPeriod, reset);
    }

    [Fact]
    public void LastBracketWins_NotTheFirst()
    {
        // Two clean boundaries; the newer one at +290/+300 decides.
        var samples = new[] { S(0, 0), S(10, 40), S(290, 40), S(300, 5) };
        var reset = DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(320));
        Assert.Equal(Base.AddMinutes(295) + UsageValues.FiveHourPeriod, reset);
    }

    [Fact]
    public void Gate1_BracketWiderThanTwentyMinutes_IsRejected()
    {
        // 21 min apart: not a boundary observation, a guess about a gap.
        var samples = new[] { S(0, 0), S(21, 40) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(30)));
    }

    [Fact]
    public void Gate1_AWideLaterBracket_SuppressesAnEarlierNarrowOne()
    {
        // The last boundary is the one that matters, and it is only poorly observed. Falling back to
        // the earlier narrow bracket would emit a reset for a window that has already turned over —
        // so the gate applies to the last candidate, not to each in turn.
        var samples = new[] { S(0, 0), S(10, 40), S(200, 40), S(290, 5) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(300)));
    }

    [Fact]
    public void Gate1_ExactlyTwentyMinutes_IsAccepted()
    {
        var samples = new[] { S(0, 0), S(20, 40) };
        Assert.Equal(Base.AddMinutes(10) + UsageValues.FiveHourPeriod,
            DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(30)));
    }

    [Fact]
    public void Gate2_ExpiredWindow_IsRejected()
    {
        var samples = new[] { S(0, 0), S(10, 40) };
        // now is past start + 5 h: the window said nothing about whatever started since.
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddHours(6)));
    }

    [Fact]
    public void Gate2_NowExactlyAtTheReset_IsRejected()
    {
        var samples = new[] { S(0, 0), S(10, 40) };
        Assert.Null(DesktopResetInference.FiveHourReset(
            samples, Org, Base.AddMinutes(5) + UsageValues.FiveHourPeriod));
    }

    [Fact]
    public void Gate3_NewestFhIsZero_IsRejected()
    {
        var samples = new[] { S(0, 0), S(10, 40), S(20, 0) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(25)));
    }

    [Fact]
    public void Gate3_NewestFhIsNull_IsRejected()
    {
        var samples = new[] { S(0, 0), S(10, 40), S(20, null) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(25)));
    }

    [Fact]
    public void Gate4_NoBracket_IsQuiet()
    {
        // History opens mid-window and only rises: the common fresh-install case, not an error.
        var samples = new[] { S(0, 30), S(10, 40), S(20, 55) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(25)));
    }

    [Fact]
    public void UnsortedInput_IsSortedByT()
    {
        var samples = new[] { S(20, 45), S(0, 0), S(10, 40) };
        Assert.Equal(Base.AddMinutes(5) + UsageValues.FiveHourPeriod,
            DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(25)));
    }

    [Fact]
    public void EmptyAndSingleSample_ProduceNothing()
    {
        Assert.Null(DesktopResetInference.FiveHourReset([], Org, Base));
        Assert.Null(DesktopResetInference.FiveHourReset([S(0, 40)], Org, Base.AddMinutes(5)));
    }

    [Fact]
    public void OrgRunBreak_IsNotABoundary()
    {
        // The drop from 88 to 12 coincides with the org change; a different account's counter is not
        // this account's reset. The newest run is the b-run alone, which has no bracket of its own.
        var samples = new[] { S(0, 80), S(10, 88), S(20, 12, "b"), S(30, 20, "b") };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, "b", Base.AddMinutes(35)));
    }

    [Fact]
    public void ExcursionToAnotherOrg_DoesNotPairAcrossIt()
    {
        // A plain "keep samples whose org matches the newest" filter would pair +10 (88) with +30
        // (12) and read a 20-minute bracket. The segment rule sees the newest run as +30..+40 only.
        var samples = new[] { S(0, 80), S(10, 88), S(20, 5, "b"), S(30, 12), S(40, 18) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(45)));
    }

    [Fact]
    public void MissingOrg_BreaksARunLikeAnyOtherChange()
    {
        var samples = new[] { S(0, 80), S(10, 88, null), S(20, 12), S(30, 18) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(35)));
    }

    [Fact]
    public void SampleCap_AppliesByTimestampNotArrayPosition()
    {
        // 20 000 old samples in ascending array order, then the useful bracket. Taking the first
        // MaxSamples off the array keeps only the old ones and finds nothing; taking the newest
        // MaxSamples by timestamp keeps the bracket. The array is already sorted, so the only thing
        // this can distinguish is which end the cap is applied to.
        var samples = new List<DesktopSample>();
        for (int i = 20_000; i >= 1; i--) samples.Add(S(-i * 60, 50));
        samples.Add(S(0, 0));
        samples.Add(S(10, 40));
        Assert.Equal(Base.AddMinutes(5) + UsageValues.FiveHourPeriod,
            DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(25)));
    }
}
