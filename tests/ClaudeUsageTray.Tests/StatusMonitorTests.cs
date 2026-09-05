using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class StatusMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly StatusSource Claude = StatusSourceRegistry.Claude;
    private static readonly StatusSource OpenAi = StatusSourceRegistry.OpenAi;

    private static StatusMonitor Both(IReadOnlyList<string>? openAiFilter = null)
        => new([(Claude, []), (OpenAi, openAiFilter ?? [])]);

    private static PlatformStatus Ok(string sourceId, DateTimeOffset at, string indicator = "none")
        => new(sourceId, at, indicator, indicator == "none" ? "All Systems Operational" : "Partial outage", [], []);

    [Fact]
    public void FreshMonitor_HasEverySourceDue()
        => Assert.Equal(["claude", "openai"], Both().TakeDue(T0).Select(s => s.Id));

    [Fact]
    public void TakenSource_IsNotDueAgainWhileInFlight()
    {
        var m = Both();
        m.TakeDue(T0);
        Assert.Empty(m.TakeDue(T0.AddMinutes(5)));
    }

    [Fact]
    public void AfterCompletion_TheThirtySecondFloorApplies()
    {
        var m = Both();
        m.TakeDue(T0);
        m.Accept("claude", Ok("claude", T0), T0);
        m.Accept("openai", Ok("openai", T0), T0);
        Assert.Empty(m.TakeDue(T0.AddSeconds(29)));
        Assert.Equal(2, m.TakeDue(T0.AddSeconds(30)).Count);
    }

    /// <summary>The isolation invariant: one source's failure must not touch the other's cadence
    /// or its last-known-good state.</summary>
    [Fact]
    public void OneSourceFailing_LeavesTheOtherAlone()
    {
        var m = Both();
        m.TakeDue(T0);
        m.Accept("claude", Ok("claude", T0), T0);
        m.Accept("openai", null, T0);

        Assert.Equal(["claude"], m.TakeDue(T0.AddSeconds(30)).Select(s => s.Id));   // openai backed off 1 min
        Assert.NotNull(m.Status("claude"));
        Assert.Null(m.Status("openai"));
    }

    [Fact]
    public void FailureKeepsTheLastKnownGoodState()
    {
        var m = Both();
        m.TakeDue(T0);
        m.Accept("claude", Ok("claude", T0, "major"), T0);
        m.TakeDue(T0.AddMinutes(1));
        m.Accept("claude", null, T0.AddMinutes(1));
        Assert.Equal("major", m.Status("claude")!.Indicator);
    }

    [Fact]
    public void ResultWithAMismatchedSourceId_IsDiscarded()
    {
        var m = Both();
        m.TakeDue(T0);
        Assert.False(m.Accept("claude", Ok("openai", T0, "major"), T0));
        Assert.Null(m.Status("claude"));
    }

    /// <summary>A discarded result still ends the fetch: the source must not stay in flight forever
    /// (TakeDue skips in-flight sources), and it is treated as a failure so a misbehaving endpoint
    /// backs off like any other.</summary>
    [Fact]
    public void MismatchedResult_DoesNotLeaveTheSourceStuckInFlight()
    {
        var m = Both();
        m.TakeDue(T0);
        m.Accept("claude", Ok("openai", T0, "major"), T0);
        Assert.Empty(m.TakeDue(T0.AddSeconds(30)).Where(s => s.Id == "claude"));        // backed off 1 min
        Assert.Contains("claude", m.TakeDue(T0.AddMinutes(1)).Select(s => s.Id));          // then due again
    }

    [Fact]
    public void CompletionForASourceDisabledMidFlight_IsDiscarded()
    {
        var m = Both();
        m.TakeDue(T0);
        m.ApplyEnabled([(Claude, [])]);
        Assert.False(m.Accept("openai", Ok("openai", T0, "major"), T0));
        Assert.Equal(["claude"], m.Sources().Select(v => v.Source.Id));
    }

    [Fact]
    public void ApplyEnabled_KeepsSurvivingSourcesAndMakesNewOnesDue()
    {
        var m = new StatusMonitor([(Claude, [])]);
        m.TakeDue(T0);
        m.Accept("claude", Ok("claude", T0, "major"), T0);

        m.ApplyEnabled([(Claude, []), (OpenAi, ["codex"])]);
        Assert.Equal("major", m.Status("claude")!.Indicator);          // not blanked
        Assert.Equal(["openai"], m.TakeDue(T0.AddSeconds(1)).Select(s => s.Id)); // claude still floored
    }

    [Fact]
    public void ApplyEnabled_UpdatesTheFilterInPlace()
    {
        var m = Both();
        m.ApplyEnabled([(Claude, []), (OpenAi, ["codex"])]);
        Assert.Equal(["codex"], m.Sources().Single(v => v.Source.Id == "openai").Filter);
    }

    [Fact]
    public void Sources_KeepRegistryOrder()
        => Assert.Equal(["claude", "openai"], Both().Sources().Select(v => v.Source.Id));

    [Fact]
    public void BadgeDegraded_IgnoresNonBadgeSourcesAndTheFilter()
    {
        var m = Both(openAiFilter: ["codex"]);
        m.TakeDue(T0);
        m.Accept("openai", Ok("openai", T0, "major"), T0);
        Assert.False(m.BadgeDegraded());

        m.Accept("claude", new PlatformStatus("claude", T0, "major", "Partial outage", [],
            [new PlatformComponent("Sora", "major_outage")]), T0);
        Assert.True(m.BadgeDegraded());
    }

    /// <summary>The spec's deliberate asymmetry: a hand-written Claude filter narrows the popup rows
    /// and the tooltip, never the badge. A README-only JSON key must not be able to disarm the
    /// tray's single most important warning.</summary>
    [Fact]
    public void BadgeDegraded_IgnoresAClaudeFilterThatExcludesTheAffectedComponent()
    {
        var m = new StatusMonitor([(Claude, ["api"])]);
        m.TakeDue(T0);
        m.Accept("claude", new PlatformStatus("claude", T0, "major", "Partial outage", [],
            [new PlatformComponent("Claude.ai", "major_outage")]), T0);
        Assert.True(m.BadgeDegraded());
    }
}
