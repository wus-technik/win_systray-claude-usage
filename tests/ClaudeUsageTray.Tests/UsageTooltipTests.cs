using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Finding 1 of the final review: nothing exercised the composed tooltip string before this
/// branch, and the "estimated from Claude Desktop history" fragment overflowed the 127-character
/// NotifyIcon budget in exactly the default configuration (paceColors on) the feature creates. These
/// tests hold UsageTooltip.Build — and its interaction with StatusDetail.ComposeTooltip's budget
/// reservation — to the wording that replaced it.</summary>
public class UsageTooltipTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static string Build(string label, WindowUsage usage, TimeSpan period, bool paceColors,
        bool stale, UsageSource? source, DateTimeOffset? fetchedAt)
    {
        var elapsed = TimeMarker.ElapsedFraction(ResetPresentation.EffectiveResetsAt(usage, Now), period, Now);
        return UsageTooltip.Build(label, usage, elapsed, paceColors, redThreshold: 85, stale, source, fetchedAt, Now);
    }

    [Fact]
    public void InferredReset_WithPaceColours_StaysInsideTheBudget_WithTheMarkingIntact()
    {
        // 55 % with 17.7 % of the 5-hour window elapsed -> ~3.1x pace, the shape the review flagged:
        // paceColors on (the default) is exactly what gives the 5-hour row a pace gloss to begin with.
        var usage = new WindowUsage(55, Now.AddHours(4).AddMinutes(7)) { Origin = ResetOrigin.Inferred };
        var text = Build("5h", usage, UsageValues.FiveHourPeriod, paceColors: true, stale: false,
            UsageSource.DesktopHistory, Now.AddMinutes(-12));

        Assert.Equal("5h · 55% · 3.1× pace · resets in ~4h 7m (estimate) · Claude Desktop history · updated 12m ago",
            text);
        Assert.True(text.Length <= StatusDetail.TooltipLimit,
            $"tooltip is {text.Length} chars, over the {StatusDetail.TooltipLimit}-char NotifyIcon budget");
        Assert.Contains("(estimate)", text);
    }

    [Fact]
    public void InferredReset_WithAStatusDisruption_TheMarkingSurvivesComposeTooltip()
    {
        // ComposeTooltip reserves the badge-raising suffix first and shortens the usage head from the
        // right when the two do not both fit. Because the estimate now sits next to "resets in"
        // instead of trailing the whole string, it must survive that shortening.
        var usage = new WindowUsage(55, Now.AddHours(4).AddMinutes(7)) { Origin = ResetOrigin.Inferred };
        var usageText = Build("5h", usage, UsageValues.FiveHourPeriod, paceColors: true, stale: false,
            UsageSource.DesktopHistory, Now.AddMinutes(-12));

        var claude = StatusSourceRegistry.Claude;
        var status = new PlatformStatus(claude.Id, Now, "major", "Major outage", [], []);
        var sources = new[] { new SourceView(claude, status, []) };

        var composed = StatusDetail.ComposeTooltip(usageText, sources, Now, stalenessMinutes: 15);

        Assert.True(composed.Length <= StatusDetail.TooltipLimit);
        Assert.Contains("(estimate)", composed);
        Assert.Contains("Claude: Major outage", composed);
    }

    [Fact]
    public void StatedAnchor_CarriesNoEstimateMarking()
    {
        var usage = new WindowUsage(65, Now.AddDays(5).AddHours(18)) { Origin = ResetOrigin.Stated };
        var text = Build("7d", usage, UsageValues.SevenDayPeriod, paceColors: true, stale: false,
            UsageSource.DesktopHistory, Now.AddMinutes(-5));

        Assert.Contains("resets in 5d 18h", text);
        Assert.DoesNotContain("~", text);
        Assert.DoesNotContain("(estimate)", text);
    }

    [Fact]
    public void ExpiredInferredReset_ReadsAsNoResetTime_NotAsResetsInNow()
    {
        // Finding 2: the held snapshot keeps its expired inferred reset for up to
        // desktopStalenessHours because SnapshotPrecedence only adopts a strictly newer re-read. The
        // tooltip must not say "resets in now" for the stretch in between.
        var usage = new WindowUsage(55, Now.AddMinutes(-45)) { Origin = ResetOrigin.Inferred };
        var text = Build("5h", usage, UsageValues.FiveHourPeriod, paceColors: true, stale: false,
            UsageSource.DesktopHistory, Now.AddMinutes(-45));

        Assert.DoesNotContain("resets in", text);
        Assert.Contains("no reset time", text);
    }

    [Fact]
    public void ClaudeCodeSnapshot_TooltipIsByteForByteWhatItIsToday()
    {
        var usage = new WindowUsage(55, Now.AddHours(4).AddMinutes(7));
        var text = Build("5h", usage, UsageValues.FiveHourPeriod, paceColors: true, stale: false,
            UsageSource.ClaudeCode, Now.AddMinutes(-2));

        // No desktop-only fragment ever reaches a Claude Code tooltip: no tilde, no estimate, no "no
        // reset time" note, and no "Claude Desktop history" mention.
        Assert.Equal("5h · 55% · 3.1× pace · resets in 4h 7m", text);
    }

    [Fact]
    public void ClaudeCodeSnapshot_InferredOriginIsStillNeverMarked()
    {
        // Regression, mirroring UsagePopupSourceTests: the estimate marking is gated on
        // `desktop && Origin == Inferred`, not on Origin alone. Origin == Inferred should never
        // reach a Claude Code snapshot in practice, but the gate is proven, not assumed.
        var usage = new WindowUsage(55, Now.AddHours(4)) { Origin = ResetOrigin.Inferred };
        var text = Build("5h", usage, UsageValues.FiveHourPeriod, paceColors: false, stale: false,
            UsageSource.ClaudeCode, Now.AddMinutes(-2));

        Assert.DoesNotContain("~", text);
        Assert.DoesNotContain("estimate", text);
    }
}
