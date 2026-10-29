using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class CodexNotificationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static Settings Absolute() => new()
    {
        PaceColors = false,
        UsageNotifications = new UsageNotificationSettings { Enabled = true, Level = NotifyLevel.Red },
        CodexNotifications = new UsageNotificationSettings { Enabled = true, Level = NotifyLevel.Red },
    };

    private static DisplayChoice Codex(int five, params ScopedLimit[] scoped)
        => new(new UsageSnapshot(T0, new WindowUsage(five, T0.AddHours(2)), null, scoped)
            { Source = UsageSource.Codex }, Stale: false);

    private static UsageNotificationRules ArmedCodex(Settings settings)
    {
        var rules = UsageNotificationRules.ForCodex();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        rules.OnUsage(Codex(10), settings, T0);
        return rules;
    }

    /// <summary>Review Focus 5: the two providers hold different Action Center slots, so retracting
    /// one cannot retract the other.</summary>
    [Fact]
    public void CodexAndClaude_HoldDifferentSlots()
        => Assert.NotEqual(UsageNotificationRules.ForClaude().Tag, UsageNotificationRules.ForCodex().Tag);

    [Fact]
    public void CodexCrossing_TitlesTheProvider()
    {
        var settings = Absolute();
        var outcome = ArmedCodex(settings).OnUsage(Codex(95), settings, T0);

        Assert.Contains("Codex", outcome.Notification!.Title);
        Assert.Equal(UsageNotificationRules.ForCodex().Tag, outcome.Notification.Tag);
    }

    [Fact]
    public void ClaudeCrossing_DoesNotMentionCodex()
    {
        var settings = Absolute();
        var rules = UsageNotificationRules.ForClaude();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        rules.OnUsage(new DisplayChoice(new UsageSnapshot(T0, new WindowUsage(10, T0.AddHours(2)), null), false), settings, T0);

        var outcome = rules.OnUsage(
            new DisplayChoice(new UsageSnapshot(T0, new WindowUsage(95, T0.AddHours(2)), null), false), settings, T0);

        Assert.DoesNotContain("Codex", outcome.Notification!.Title);
    }

    /// <summary>Codex's switch is its own: off here must not silence Claude, and Claude's level must
    /// not decide Codex's.</summary>
    [Fact]
    public void CodexSwitchedOff_SuppressesOnlyCodex()
    {
        var settings = Absolute();
        settings.CodexNotifications = new UsageNotificationSettings { Enabled = false, Level = NotifyLevel.Red };

        Assert.Null(ArmedCodex(settings).OnUsage(Codex(95), settings, T0).Notification);
    }

    /// <summary>"Spend control" is a fixed string this app supplies, not a payload label, so naming
    /// it in the log is safe — unlike a scoped limit whose label is an account-specific model name,
    /// which stays counted as scoped=N.</summary>
    [Fact]
    public void Log_NamesSpendControlButStillCountsOtherScopedLimits()
    {
        var settings = Absolute();
        var spend = new ScopedLimit("Spend control", null, 111, T0.AddDays(19), true)
            { Period = ScopedPeriod.Monthly };
        var model = new ScopedLimit("Some Model Name", null, 95, T0.AddDays(2), true);

        var rules = UsageNotificationRules.ForCodex();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        rules.OnUsage(Codex(10, spend with { Percent = 10 }, model with { Percent = 10 }), settings, T0);

        var outcome = rules.OnUsage(Codex(10, spend, model), settings, T0);
        var line = Assert.Single(outcome.Log, l => l.Contains("emitted"));

        Assert.Contains("spend=111%", line);
        Assert.Contains("scoped=1", line);
        Assert.DoesNotContain("Some Model Name", line);
    }
    [Fact]
    public void RetractingCodex_LeavesClaudeToastInItsOwnSlot()
    {
        var settings = Absolute();
        var claude = UsageNotificationRules.ForClaude();
        claude.NoteLiveOutcome(LiveOutcome.Snapshot);
        DisplayChoice Claude(int percent) => new(new UsageSnapshot(T0,
            new WindowUsage(percent, T0.AddHours(2)), null), false);
        claude.OnUsage(Claude(10), settings, T0);
        var claudeToast = claude.OnUsage(Claude(95), settings, T0).Notification;
        var codex = ArmedCodex(settings);
        var codexToast = codex.OnUsage(Codex(95), settings, T0).Notification;
        Assert.NotNull(claudeToast);
        Assert.NotNull(codexToast);
        var visible = new Dictionary<string, Notification>
        {
            [claudeToast.Tag] = claudeToast,
            [codexToast.Tag] = codexToast,
        };
        var retract = codex.OnUsage(Codex(10), settings, T0);
        Assert.True(retract.RemoveUsageToast);
        visible.Remove(codex.Tag);
        Assert.Same(claudeToast, Assert.Single(visible).Value);
        Assert.False(claude.OnUsage(Claude(95), settings, T0).RemoveUsageToast);
    }

    [Fact]
    public void CodexLevel_IsIndependentOfClaudeLevel()
    {
        var settings = Absolute();
        settings.CodexNotifications.Level = NotifyLevel.Orange;
        var outcome = ArmedCodex(settings).OnUsage(Codex(70), settings, T0);
        Assert.NotNull(outcome.Notification);
        Assert.Contains("orange", outcome.Notification.Title);
    }
}
