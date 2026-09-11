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
    public void BadgeDegraded_IgnoresNonBadgeSources()
    {
        var m = Both(openAiFilter: ["codex"]);
        m.TakeDue(T0);
        m.Accept("openai", Ok("openai", T0, "major"), T0);
        Assert.False(m.BadgeDegraded());

        m.Accept("claude", new PlatformStatus("claude", T0, "major", "Partial outage", [],
            [new PlatformComponent("Sora", "major_outage")]), T0);
        Assert.True(m.BadgeDegraded());
    }

    // ---- the badge under a watch filter (2026-09-11) ----

    private static StatusMonitor Claudes(params string[] filter) => new([(Claude, filter)]);

    /// <summary>The live 2026-09-11 shape: the banner reads minor because one component the user may
    /// never have opened is degraded.</summary>
    private static PlatformStatus CoworkDegraded(DateTimeOffset at)
        => new("claude", at, "minor", "Partial System Outage", [],
            [new PlatformComponent("Claude Cowork", "degraded_performance")]);

    private static bool BadgeAfter(StatusMonitor m, PlatformStatus status)
    {
        m.TakeDue(T0);
        m.Accept(status.SourceId, status, T0);
        return m.BadgeDegraded();
    }

    [Fact]
    public void CoworkDegraded_DoesNotBadgeAClaudeCodeWatcher()
        => Assert.False(BadgeAfter(Claudes("Claude Code"), CoworkDegraded(T0)));

    [Fact]
    public void CoworkDegraded_BadgesTheEmptyFilter()
        => Assert.True(BadgeAfter(Claudes(), CoworkDegraded(T0)));

    [Fact]
    public void CoworkDegraded_BadgesAWatcherOfCowork()
        => Assert.True(BadgeAfter(Claudes("Cowork"), CoworkDegraded(T0)));

    [Fact]
    public void HealthyPage_NeverBadges()
        => Assert.False(BadgeAfter(Claudes(), Ok("claude", T0)));

    /// <summary>RaisesBadge is untouched: an OpenAI outage still never marks the icon, under any
    /// filter, for the unrelated reason that it says nothing about Claude usage headroom.</summary>
    [Fact]
    public void OpenAiOutage_NeverBadges_UnderAnyFilter()
    {
        var m = new StatusMonitor([(OpenAi, [])]);
        var down = new PlatformStatus("openai", T0, "major", "Major Outage", [],
            [new PlatformComponent("Responses", "major_outage")]);
        Assert.False(BadgeAfter(m, down));
    }

    /// <summary>Watch Claude status off: the source is simply not in EnabledSources, so a full outage
    /// produces no badge at all. That is the user's explicit choice, made in a labelled checkbox.</summary>
    [Fact]
    public void SourceNotEnabled_NeverBadges()
    {
        var m = new StatusMonitor([(OpenAi, [])]);
        m.TakeDue(T0);
        m.Accept("claude", new PlatformStatus("claude", T0, "critical", "Major Outage", [], []), T0);
        Assert.False(m.BadgeDegraded());
    }

    /// <summary>A disabled and re-enabled source comes back with Status = null, so the badge is off
    /// until the next fetch lands — up to one poll cycle. Accepted: the alternative is resurrecting
    /// a status of unknown age.</summary>
    [Fact]
    public void ReEnabledSource_DoesNotBadgeUntilAFetchIsAccepted()
    {
        var m = Claudes();
        BadgeAfter(m, CoworkDegraded(T0));
        m.ApplyEnabled([]);
        m.ApplyEnabled([(Claude, [])]);
        Assert.False(m.BadgeDegraded());

        m.TakeDue(T0.AddMinutes(1));
        m.Accept("claude", CoworkDegraded(T0.AddMinutes(1)), T0.AddMinutes(1));
        Assert.True(m.BadgeDegraded());
    }

    /// <summary>Fail towards visible: a degraded page whose payload identifies nothing at all still
    /// badges, however narrow the filter. A filter narrows noise; it never hides an outage the page
    /// could not classify.</summary>
    [Fact]
    public void DegradedWithNothingIdentified_BadgesUnderANarrowFilter()
    {
        var blank = new PlatformStatus("claude", T0, "major", "Major Outage", [], []);
        Assert.True(BadgeAfter(Claudes("Claude Code"), blank));
    }

    /// <summary>Same rule one level down: an incident naming no components counts as watched.</summary>
    [Fact]
    public void IncidentNamingNoComponents_BadgesUnderANarrowFilter()
    {
        var incident = new PlatformIncident("Elevated errors", "investigating", "major",
            "https://stspg.io/x", T0, []);
        var status = new PlatformStatus("claude", T0, "major", "Major Outage", [incident], []);
        Assert.True(BadgeAfter(Claudes("Claude Code"), status));
    }

    /// <summary>Editing the filter re-evaluates the badge against the retained payload, with no
    /// refetch: ApplyEnabled keeps a surviving source's Status while replacing its Filter.</summary>
    [Fact]
    public void WideningTheFilterLightsTheBadgeWithoutARefetch()
    {
        var m = Claudes("Claude Code");
        Assert.False(BadgeAfter(m, CoworkDegraded(T0)));
        m.ApplyEnabled([(Claude, [])]);
        Assert.True(m.BadgeDegraded());
    }
}
