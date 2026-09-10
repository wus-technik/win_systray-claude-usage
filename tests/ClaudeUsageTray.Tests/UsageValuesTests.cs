using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class UsageValuesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static Settings NoPace() => new() { PaceColors = false };

    [Fact]
    public void AbsentValuesProduceNoRow()
    {
        var values = UsageValues.Enumerate(new UsageSnapshot(Now, null, null), new Settings(), Now);
        Assert.Empty(values);
    }

    [Fact]
    public void KeysAndLabelsAreTheDocumentedOnes()
    {
        var snapshot = new UsageSnapshot(Now,
            new WindowUsage(10, Now.AddHours(4)),
            new WindowUsage(20, Now.AddDays(6)),
            [new ScopedLimit("Fable", "claude-fable", 30, Now.AddDays(6), true)],
            new CreditUsage(null, null, 40, null, new CreditState(true, null, false)));

        var values = UsageValues.Enumerate(snapshot, NoPace(), Now);

        Assert.Equal(["5h", "7d", "Fable", "credits"], values.Select(v => v.Key));
        Assert.Equal(["5-hour window", "7-day window", "Fable weekly", "Credits"], values.Select(v => v.Label));
        Assert.Equal([10, 20, 30, 40], values.Select(v => v.Percent));
        Assert.Equal(Now.AddHours(4), values[0].ResetsAt);
        Assert.Null(values[3].ResetsAt);
    }

    [Fact]
    public void EveryScopedLimitIsEnumerated_NotJustThePopupsVisibleFour()
    {
        var limits = Enumerable.Range(0, 6)
            .Select(i => new ScopedLimit($"Model{i}", null, 10 * i, Now.AddDays(3), i % 2 == 0))
            .ToList();
        var values = UsageValues.Enumerate(new UsageSnapshot(Now, null, null, limits), NoPace(), Now);
        Assert.Equal(6, values.Count);   // PopupRows caps at four; notifications must not
    }

    [Fact]
    public void WindowSeverityFollowsSeverityRulesForSettings_WithTheWindowsOwnPeriod()
    {
        // 60 % used with 5.5 of 7 days gone is Green under pace, Orange without it.
        var usage = new WindowUsage(60, Now.AddDays(1.5));
        Assert.Equal(Severity.Green, UsageValues.WindowSeverity(usage, UsageValues.SevenDayPeriod, new Settings(), Now));
        Assert.Equal(Severity.Orange, UsageValues.WindowSeverity(usage, UsageValues.SevenDayPeriod, NoPace(), Now));
        // The same numbers measured against a 5-hour period are far ahead of pace: 60 % with
        // 1.5 days of a 5-hour window "remaining" has no valid elapsed fraction → absolute → Orange.
        Assert.Equal(Severity.Orange, UsageValues.WindowSeverity(usage, UsageValues.FiveHourPeriod, new Settings(), Now));
    }

    [Fact]
    public void CreditsPreferThePayloadSeverityOverTheThresholds()
    {
        // 10 % would be Green by every threshold; the payload says the cap is already reached.
        var credits = new CreditUsage(null, null, 10, "critical", new CreditState(true, null, true));
        Assert.Equal(Severity.Red, UsageValues.CreditSeverity(credits, new Settings()));

        var silent = new CreditUsage(null, null, 90, null, new CreditState(true, null, false));
        Assert.Equal(Severity.Red, UsageValues.CreditSeverity(silent, new Settings()));   // fallback: 90 > 85

        var unknownWord = new CreditUsage(null, null, 60, "meh", new CreditState(true, null, false));
        Assert.Equal(Severity.Orange, UsageValues.CreditSeverity(unknownWord, new Settings()));
    }

    [Theory]
    [InlineData("critical", Severity.Red)]
    [InlineData("warning", Severity.Orange)]
    [InlineData("normal", Severity.Green)]
    public void FromPayloadMapsTheThreeKnownWords(string word, Severity expected)
        => Assert.Equal(expected, SeverityRules.FromPayload(word));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CRITICAL")]   // the popup never matched case-insensitively; keep that
    public void FromPayloadIsNullForAnythingElse(string? word)
        => Assert.Null(SeverityRules.FromPayload(word));

    /// <summary>17.5 % of a 5-hour window elapsed at 55 % used: pace ratio 55/17.5 = 3.14 → Red,
    /// absolute (55, orange 50, red 85) → Orange. The two verdicts differ, which is what makes these
    /// cases meaningful. Expressed as time-until-reset so the elapsed fraction is legible.</summary>
    private static (WindowUsage Usage, DateTimeOffset Now) PacedRedAbsoluteOrange(ResetOrigin origin)
    {
        var now = new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero);
        var reset = now + TimeSpan.FromMinutes(247.5);   // 52.5 of 300 minutes gone
        return (new WindowUsage(55, reset) { Origin = origin }, now);
    }

    [Fact]
    public void Inferred_DrawsPaced_ButNotifiesAbsolute()
    {
        var (usage, now) = PacedRedAbsoluteOrange(ResetOrigin.Inferred);
        var value = UsageValues.Enumerate(
            new UsageSnapshot(now, usage, null) { Source = UsageSource.DesktopHistory },
            new Settings(), now)[0];

        Assert.Equal(Severity.Red, value.Severity);
        Assert.Equal(Severity.Orange, value.NotifySeverity);
    }

    [Theory]
    [InlineData(ResetOrigin.Reported)]
    [InlineData(ResetOrigin.Stated)]
    public void ReportedAndStated_AgreeOnBothSeverities(ResetOrigin origin)
    {
        var (usage, now) = PacedRedAbsoluteOrange(origin);
        var value = UsageValues.Enumerate(new UsageSnapshot(now, usage, null), new Settings(), now)[0];

        Assert.Equal(Severity.Red, value.Severity);
        Assert.Equal(value.Severity, value.NotifySeverity);
    }

    [Fact]
    public void Inferred_AboveRedAbove_StillNotifiesRed()
    {
        // A null elapsed fraction falls back to the absolute thresholds, and 90 % is past redAbove.
        // The inference is simply not what got it there.
        var now = new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero);
        var usage = new WindowUsage(90, now.AddHours(4)) { Origin = ResetOrigin.Inferred };
        var value = UsageValues.Enumerate(
            new UsageSnapshot(now, usage, null) { Source = UsageSource.DesktopHistory },
            new Settings(), now)[0];

        Assert.Equal(Severity.Red, value.NotifySeverity);
    }

    [Fact]
    public void ScopedLimitsAndCredits_HaveEqualSeverities()
    {
        // Neither can be Inferred: only the desktop reader assigns that, and it emits neither.
        var now = new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero);
        var snapshot = new UsageSnapshot(now, null, null,
            [new ScopedLimit("Fable", null, 92, now.AddDays(2), true)],
            new CreditUsage(null, null, 40, null, new CreditState(true, null, false)));

        foreach (var value in UsageValues.Enumerate(snapshot, new Settings(), now))
            Assert.Equal(value.Severity, value.NotifySeverity);
    }

    [Fact]
    public void StaleSnapshotPacedAgainstLiveNow_Understates()
    {
        // Same percentage, same inferred reset; the only difference is how much of the window has
        // elapsed by the time we look. A larger elapsed fraction yields a smaller ratio, so the
        // verdict can only soften — never escalate — as a snapshot ages.
        var reset = new DateTimeOffset(2026, 9, 10, 11, 0, 0, TimeSpan.Zero);
        var usage = new WindowUsage(55, reset) { Origin = ResetOrigin.Inferred };
        var settings = new Settings();

        var fresh = UsageValues.WindowSeverities(usage, UsageValues.FiveHourPeriod, settings,
            new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero)).Draw;
        var stale = UsageValues.WindowSeverities(usage, UsageValues.FiveHourPeriod, settings,
            new DateTimeOffset(2026, 9, 10, 10, 30, 0, TimeSpan.Zero)).Draw;

        Assert.Equal(Severity.Red, fresh);
        Assert.True(stale < fresh, $"expected the aged reading to soften, got {stale}");
    }

    [Fact]
    public void AboveRedAbove_StaysRedHoweverStale()
    {
        // ForPace returns Red unconditionally above redAbove: running out is running out.
        var reset = new DateTimeOffset(2026, 9, 10, 11, 0, 0, TimeSpan.Zero);
        var usage = new WindowUsage(95, reset) { Origin = ResetOrigin.Inferred };
        Assert.Equal(Severity.Red, UsageValues.WindowSeverities(usage, UsageValues.FiveHourPeriod,
            new Settings(), new DateTimeOffset(2026, 9, 10, 10, 55, 0, TimeSpan.Zero)).Draw);
    }
}
