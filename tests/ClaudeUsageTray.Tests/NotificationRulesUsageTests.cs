using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The whole of the usage-notification behaviour: baseline, arming, hysteresis, the settings
/// fingerprint, the source switch, eviction, coalescing, and the on/off switch. No clock — every test
/// hands in its own now.</summary>
public class NotificationRulesUsageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Absolute thresholds only, so a test's percent is its colour and the clock is inert.</summary>
    private static Settings Absolute(NotifyLevel level = NotifyLevel.Red, bool enabled = true) => new()
    {
        PaceColors = false,
        UsageNotifications = new UsageNotificationSettings { Enabled = enabled, Level = level },
    };

    private static UsageSnapshot Snap(int five, DateTimeOffset at, int? seven = null,
        UsageSource source = UsageSource.ClaudeCode, params ScopedLimit[] scoped)
        => new(at, new WindowUsage(five, at.AddHours(2)), seven is { } s ? new WindowUsage(s, at.AddDays(3)) : null, scoped)
            { Source = source };

    private static DisplayChoice Fresh(UsageSnapshot s) => new(s, Stale: false);
    private static DisplayChoice Stale(UsageSnapshot s) => new(s, Stale: true);

    /// <summary>Armed by a successful fetch, then baselined at 10 %.</summary>
    private static NotificationRules ArmedAt(int five, Settings? settings = null)
    {
        var rules = new NotificationRules();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        rules.OnUsage(Fresh(Snap(five, T0)), settings ?? Absolute(), T0);
        return rules;
    }

    // ---- baseline & arming ----

    [Fact]
    public void FirstSightIsBaseline_EvenWhenAlreadyRed()
    {
        var rules = new NotificationRules();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        var outcome = rules.OnUsage(Fresh(Snap(95, T0)), Absolute(), T0);
        Assert.Null(outcome.Notification);
    }

    [Fact]
    public void UnarmedCacheReadThenFirstLiveFetchRevealingRed_NotifiesNothing()
    {
        // The startup storm: cache says 40 %, first live fetch says 91 %.
        var rules = new NotificationRules();
        Assert.Null(rules.OnUsage(Fresh(Snap(40, T0)), Absolute(), T0).Notification);
        Assert.False(rules.IsArmed);

        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        Assert.Null(rules.OnUsage(Fresh(Snap(91, T0.AddSeconds(5), seven: 10)), Absolute(), T0.AddSeconds(5)).Notification);

        // A second live reading that crosses does notify — for the key that crossed, not the one
        // that was red at the baseline.
        var crossed = rules.OnUsage(Fresh(Snap(91, T0.AddMinutes(5), seven: 90)), Absolute(), T0.AddMinutes(5));
        Assert.NotNull(crossed.Notification);
        Assert.Contains("7-day window", crossed.Notification!.Body);
        Assert.DoesNotContain("5-hour", crossed.Notification.Body);   // 5h was already red at baseline
    }

    [Theory]
    [InlineData(LiveOutcome.RateLimited)]
    [InlineData(LiveOutcome.Failed)]
    public void RateLimitOrNetworkErrorDoesNotArm(LiveOutcome first)
    {
        var rules = new NotificationRules();
        rules.OnUsage(Fresh(Snap(40, T0)), Absolute(), T0);       // cache
        Assert.Null(rules.NoteLiveOutcome(first));
        Assert.False(rules.IsArmed);
        rules.OnUsage(Fresh(Snap(40, T0.AddMinutes(1))), Absolute(), T0.AddMinutes(1));

        // The first *successful* fetch minutes later reveals red — still a baseline.
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        Assert.True(rules.IsArmed);
        Assert.Null(rules.OnUsage(Fresh(Snap(91, T0.AddMinutes(6))), Absolute(), T0.AddMinutes(6)).Notification);
    }

    [Theory]
    [InlineData(LiveOutcome.Unauthorized)]
    [InlineData(LiveOutcome.NoToken)]
    [InlineData(LiveOutcome.Snapshot)]
    public void TerminalOutcomesArmImmediately(LiveOutcome outcome)
    {
        var rules = new NotificationRules();
        Assert.NotNull(rules.NoteLiveOutcome(outcome));
        Assert.True(rules.IsArmed);
    }

    [Fact]
    public void ThirdConcludedAttemptArmsRegardless()
    {
        var rules = new NotificationRules();
        Assert.Null(rules.NoteLiveOutcome(LiveOutcome.Failed));
        Assert.Null(rules.NoteLiveOutcome(LiveOutcome.RateLimited));
        Assert.False(rules.IsArmed);
        Assert.NotNull(rules.NoteLiveOutcome(LiveOutcome.Failed));
        Assert.True(rules.IsArmed);
    }

    [Fact]
    public void ArmingIsABaseline_TheFirstEvaluationAfterItNeverNotifies()
    {
        var rules = new NotificationRules();
        rules.OnUsage(Fresh(Snap(40, T0)), Absolute(), T0);
        rules.NoteLiveOutcome(LiveOutcome.Failed);
        rules.NoteLiveOutcome(LiveOutcome.Failed);
        rules.NoteLiveOutcome(LiveOutcome.Failed);              // backstop arms
        Assert.Null(rules.OnUsage(Fresh(Snap(95, T0.AddMinutes(20))), Absolute(), T0.AddMinutes(20)).Notification);
    }

    // ---- crossings ----

    [Fact]
    public void CrossingIntoRedNotifiesOnce_ThenStaysSilentWhileRed()
    {
        var rules = ArmedAt(10);
        var first = rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(5))), Absolute(), T0.AddMinutes(5));
        Assert.NotNull(first.Notification);
        Assert.Equal(NotificationRules.UsageTag, first.Notification!.Tag);
        Assert.Equal(NotificationRules.OpenPopupArgument, first.Notification.Argument);
        Assert.Equal("5-hour window (90 %) is now red", first.Notification.Body);

        for (int i = 1; i <= 20; i++)
            Assert.Null(rules.OnUsage(Fresh(Snap(90 + i % 3, T0.AddMinutes(5 + i * 5))), Absolute(), T0.AddMinutes(5 + i * 5)).Notification);
    }

    [Fact]
    public void ARepeatedStaleReadingLogsStaleOnce()
    {
        var rules = ArmedAt(10);
        Assert.Contains(rules.OnUsage(Stale(Snap(10, T0.AddMinutes(1))), Absolute(), T0.AddMinutes(1)).Log, l => l.Contains("stale"));
        Assert.Empty(rules.OnUsage(Stale(Snap(10, T0.AddMinutes(2))), Absolute(), T0.AddMinutes(2)).Log);
    }

    [Fact]
    public void LeavingRedIsSilent_ReturningAfterGreenNotifiesAgain()
    {
        var rules = ArmedAt(90);
        var left = rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(5))), Absolute(), T0.AddMinutes(5));
        Assert.Null(left.Notification);
        Assert.True(left.RemoveUsageToast);
        Assert.NotNull(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(10))), Absolute(), T0.AddMinutes(10)).Notification);
    }

    [Fact]
    public void ACoalescedToastIsRetractedAsSoonAsAnyOfItsKeysLeavesTheLevel()
    {
        // The toast said "5-hour window and 7-day window are now red". When 5h drops back, that
        // sentence is false, so the toast goes — even though 7d is still red (the badge still says so).
        var rules = new NotificationRules();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        rules.OnUsage(Fresh(Snap(10, T0, seven: 10)), Absolute(), T0);
        Assert.NotNull(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(1), seven: 90)), Absolute(), T0.AddMinutes(1)).Notification);
        var partial = rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(2), seven: 90)), Absolute(), T0.AddMinutes(2));
        Assert.Null(partial.Notification);
        Assert.True(partial.RemoveUsageToast);
        // 7d never left red: still no second toast for it.
        Assert.Null(rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(3), seven: 91)), Absolute(), T0.AddMinutes(3)).Notification);
    }

    [Fact]
    public void Hysteresis_RedToOrangeToRedIsOneToast_RedToGreenToRedIsTwo()
    {
        var rules = ArmedAt(10);
        Assert.NotNull(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(1))), Absolute(), T0.AddMinutes(1)).Notification);
        Assert.Null(rules.OnUsage(Fresh(Snap(70, T0.AddMinutes(2))), Absolute(), T0.AddMinutes(2)).Notification);   // orange
        var latched = rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(3))), Absolute(), T0.AddMinutes(3));               // red again: silent
        Assert.Null(latched.Notification);
        Assert.Contains(latched.Log, l => l.Contains("hysteresis"));
        Assert.Null(rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(4))), Absolute(), T0.AddMinutes(4)).Notification);   // green re-arms
        Assert.NotNull(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(5))), Absolute(), T0.AddMinutes(5)).Notification);
    }

    [Fact]
    public void ClockOnlyCrossing_TheSameSnapshotTurnsRedAsTheWindowElapses()
    {
        // Pace on (50/85). 30 % used, 5-hour window resetting at T0 + 4 h 30. Twenty minutes before
        // T0 the elapsed fraction is 0.033 — inside SeverityRules' dead zone (0.10), so the absolute
        // thresholds decide: Green. Around T0 the fraction leaves the dead zone (in doubles
        // 1 - 0.9 lands a hair under 0.10, so it is minute 5 with fraction 0.117 rather than minute 0)
        // and the ratio is 30 / 11.7 ≈ 2.6, above RedRatio 1.75: Red. Nothing but the clock moved.
        // This is the case the deleted timestamp rule would have made unnotifiable. By minute 60 the
        // ratio is back to 1.0 (Green), which re-arms — still one toast in the whole run.
        var settings = new Settings();
        var snapshot = new UsageSnapshot(T0, new WindowUsage(30, T0.AddHours(4.5)), null);
        var rules = new NotificationRules();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        Assert.Null(rules.OnUsage(Fresh(snapshot), settings, T0.AddMinutes(-20)).Notification);

        int toasts = 0;
        for (int minute = -15; minute <= 60; minute += 5)
            if (rules.OnUsage(Fresh(snapshot), settings, T0.AddMinutes(minute)).Notification is not null) toasts++;
        Assert.Equal(1, toasts);
    }

    [Fact]
    public void SeveralCrossingsCoalesceIntoOneToast_ScopedLimitsNamedInTheToastButCountedInTheLog()
    {
        // Every key must exist at the baseline: a key seen for the first time is a baseline, not a crossing.
        var rules = new NotificationRules();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        rules.OnUsage(Fresh(Snap(10, T0, seven: 10, scoped: new ScopedLimit("Fable", null, 10, T0.AddDays(3), true))), Absolute(), T0);
        var fable = new ScopedLimit("Fable", null, 92, T0.AddDays(3), true);
        var outcome = rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(5), seven: 91, scoped: fable)), Absolute(), T0.AddMinutes(5));
        Assert.NotNull(outcome.Notification);
        Assert.Equal("5-hour window (90 %), 7-day window (91 %) and Fable weekly (92 %) are now red", outcome.Notification!.Body);
        var emitted = Assert.Single(outcome.Log, l => l.Contains("emitted"));
        Assert.Contains("5h", emitted);
        Assert.Contains("scoped=1", emitted);
        Assert.DoesNotContain("Fable", emitted);
    }

    [Fact]
    public void ExpiryIsTheLatestKnownReset_OrSixHours()
    {
        var rules = ArmedAt(10);
        var at = T0.AddMinutes(5);
        var withReset = rules.OnUsage(Fresh(Snap(90, at)), Absolute(), at).Notification!;
        Assert.Equal(at.AddHours(2), withReset.ExpiresAt);

        var rules2 = ArmedAt(10);
        var credits = new UsageSnapshot(at, new WindowUsage(10, at.AddHours(2)), null,
            Credits: new CreditUsage(null, null, 5, "critical", new CreditState(true, null, true)));
        rules2.OnUsage(Fresh(new UsageSnapshot(T0, new WindowUsage(10, T0.AddHours(2)), null,
            Credits: new CreditUsage(null, null, 5, "normal", new CreditState(true, null, false)))), Absolute(), T0);
        var noReset = rules2.OnUsage(Fresh(credits), Absolute(), at).Notification!;
        Assert.Equal(at + NotificationRules.DefaultUsageToastLifetime, noReset.ExpiresAt);
    }

    // ---- levels ----

    [Fact]
    public void LevelOrange_GreenToRedFiresOnce_OrangeToRedIsSilent()
    {
        var settings = Absolute(NotifyLevel.Orange);
        var rules = ArmedAt(10, settings);
        var toRed = rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(1))), settings, T0.AddMinutes(1));
        Assert.NotNull(toRed.Notification);
        Assert.EndsWith("is now red", toRed.Notification!.Body);

        var rules2 = ArmedAt(60, settings);   // baseline orange
        Assert.Null(rules2.OnUsage(Fresh(Snap(90, T0.AddMinutes(1))), settings, T0.AddMinutes(1)).Notification);
    }

    [Fact]
    public void LevelOrange_GreenToOrangeNotifies_LevelRedDoesNot()
    {
        var orange = Absolute(NotifyLevel.Orange);
        var rules = ArmedAt(10, orange);
        var outcome = rules.OnUsage(Fresh(Snap(60, T0.AddMinutes(1))), orange, T0.AddMinutes(1));
        Assert.Equal("5-hour window (60 %) is now orange", outcome.Notification!.Body);

        Assert.Null(ArmedAt(10).OnUsage(Fresh(Snap(60, T0.AddMinutes(1))), Absolute(), T0.AddMinutes(1)).Notification);
    }

    // ---- guards ----

    [Fact]
    public void StaleRecordsNothing_AndACrossingAcrossTheGapFiresOnce()
    {
        var rules = ArmedAt(10);
        Assert.Null(rules.OnUsage(Stale(Snap(90, T0.AddMinutes(1))), Absolute(), T0.AddMinutes(1)).Notification);
        Assert.Null(rules.OnUsage(Stale(Snap(90, T0.AddMinutes(2))), Absolute(), T0.AddMinutes(2)).Notification);
        Assert.NotNull(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(3))), Absolute(), T0.AddMinutes(3)).Notification);
    }

    [Fact]
    public void UnarmedStartupNeverBaselinesAgainstAStaleCache()
    {
        var rules = new NotificationRules();
        rules.OnUsage(Stale(Snap(10, T0.AddHours(-5))), Absolute(), T0);     // hours-old cache: nothing recorded
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        Assert.Null(rules.OnUsage(Fresh(Snap(95, T0)), Absolute(), T0).Notification);   // first sight → baseline
    }

    [Fact]
    public void NoSnapshotIsNotAReading()
    {
        var rules = ArmedAt(10);
        Assert.Null(rules.OnUsage(new DisplayChoice(null, false), Absolute(), T0.AddMinutes(1)).Notification);
        Assert.NotNull(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(2))), Absolute(), T0.AddMinutes(2)).Notification);
    }

    [Fact]
    public void SourceSwitchRebaselinesSilently_BothDirections()
    {
        var rules = ArmedAt(10);
        var desktopRed = Snap(90, T0.AddMinutes(1), source: UsageSource.DesktopHistory);
        var switched = rules.OnUsage(Fresh(desktopRed), Absolute(), T0.AddMinutes(1));
        Assert.Null(switched.Notification);
        Assert.Contains(switched.Log, l => l.Contains("source switch"));

        var backRed = Snap(90, T0.AddMinutes(2));
        Assert.Null(rules.OnUsage(Fresh(backRed), Absolute(), T0.AddMinutes(2)).Notification);
    }

    [Fact]
    public void KeyThatVanishesAndReturnsIsRebaselined()
    {
        var fable = new ScopedLimit("Fable", null, 10, T0.AddDays(3), true);
        var rules = ArmedAt(10);
        rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(1), scoped: fable)), Absolute(), T0.AddMinutes(1));   // Fable appears green
        rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(2))), Absolute(), T0.AddMinutes(2));                  // Fable gone → evicted
        var back = new ScopedLimit("Fable", null, 92, T0.AddDays(3), true);
        Assert.Null(rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(3), scoped: back)), Absolute(), T0.AddMinutes(3)).Notification);
    }

    [Fact]
    public void KeysCompareCaseInsensitively()
    {
        var rules = ArmedAt(10);
        rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(1), scoped: new ScopedLimit("Fable", null, 10, T0.AddDays(3), true))), Absolute(), T0.AddMinutes(1));
        var crossed = rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(2), scoped: new ScopedLimit("FABLE", null, 92, T0.AddDays(3), true))), Absolute(), T0.AddMinutes(2));
        Assert.NotNull(crossed.Notification);   // same key, so a real crossing — not a re-baseline
    }

    // ---- configuration changes ----

    [Fact]
    public void LoweringRedUnderAnExistingValueIsSilent()
    {
        var rules = ArmedAt(60);   // orange at 50/85
        var lowered = Absolute();
        lowered.Thresholds = new Thresholds { Orange = 20, Red = 50 };
        var outcome = rules.OnUsage(Fresh(Snap(60, T0.AddMinutes(1))), lowered, T0.AddMinutes(1));
        Assert.Null(outcome.Notification);
        Assert.Contains(outcome.Log, l => l.Contains("fingerprint change"));
        // …and the new baseline holds: a later real crossing still fires.
        Assert.Null(rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(2))), lowered, T0.AddMinutes(2)).Notification);
        Assert.NotNull(rules.OnUsage(Fresh(Snap(60, T0.AddMinutes(3))), lowered, T0.AddMinutes(3)).Notification);
    }

    [Fact]
    public void TogglingPaceColoursIsSilent()
    {
        var pace = new Settings();
        var rules = new NotificationRules();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        // 84 %: Green under pace late in the window (reset in 20 min of 5 h → elapsed 0.93, ratio 0.9), Orange absolute.
        var snap = new UsageSnapshot(T0, new WindowUsage(84, T0.AddMinutes(20)), null);
        rules.OnUsage(Fresh(snap), pace, T0);
        var absolute = new Settings { PaceColors = false, UsageNotifications = new() { Level = NotifyLevel.Orange } };
        Assert.Null(rules.OnUsage(Fresh(snap), absolute, T0.AddSeconds(30)).Notification);
    }

    [Fact]
    public void ChangingLevelToOrangeWhileAlreadyOrangeIsSilent()
    {
        var rules = ArmedAt(60);
        Assert.Null(rules.OnUsage(Fresh(Snap(60, T0.AddMinutes(1))), Absolute(NotifyLevel.Orange), T0.AddMinutes(1)).Notification);
    }

    [Fact]
    public void RaisingStalenessSoAnOldSnapshotBecomesFreshIsSilent()
    {
        // Stale is decided by the caller (SourceSelection); here it flips from Stale to Fresh with a
        // changed fingerprint at the same time, and the first fresh evaluation is a baseline.
        var rules = ArmedAt(10);
        var old = Snap(90, T0.AddMinutes(-30));
        rules.OnUsage(Stale(old), Absolute(), T0.AddMinutes(1));
        var wider = Absolute();
        wider.StalenessMinutes = 120;
        Assert.Null(rules.OnUsage(Fresh(old), wider, T0.AddMinutes(2)).Notification);
    }

    // ---- switch ----

    [Fact]
    public void SwitchedOff_StillEvaluates_SoSwitchingBackOnCannotReplayAMissedCrossing()
    {
        var rules = ArmedAt(10);
        var off = Absolute(enabled: false);
        var suppressed = rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(1))), off, T0.AddMinutes(1));
        Assert.Null(suppressed.Notification);
        Assert.Contains(suppressed.Log, l => l.Contains("switched off"));
        Assert.Null(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(2))), Absolute(), T0.AddMinutes(2)).Notification);
    }
}
