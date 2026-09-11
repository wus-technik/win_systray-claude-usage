using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Status transitions per source: the state is IsRelevant under the watch filter, a null
/// status is not a reading, first reading is baseline, and a disabled source or a changed filter
/// re-baselines. Recovery text must never claim a page is healthy while it still reports a
/// disruption.</summary>
public class NotificationRulesStatusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly StatusSource Claude = StatusSourceRegistry.Claude;
    private static readonly StatusSource OpenAi = StatusSourceRegistry.OpenAi;

    private static PlatformStatus Ok(StatusSource s) => new(s.Id, T0, "none", "All Systems Operational", [], []);

    private static PlatformStatus Down(StatusSource s, string indicator = "major",
        PlatformIncident[]? incidents = null, PlatformComponent[]? components = null)
        => new(s.Id, T0, indicator, "Partial System Outage", incidents ?? [], components ?? []);

    private static PlatformIncident Incident(string name, params string[] components)
        => new(name, "investigating", "major", "https://stspg.io/x", T0, components);

    private static SourceView View(StatusSource s, PlatformStatus? status, params string[] filter) => new(s, status, filter);

    private static Settings WithNotify(string sourceId, bool notify)
    {
        var settings = new Settings();
        settings.StatusSources[sourceId] = new StatusSourceSettings { Enabled = true, Notify = notify, Components = [] };
        return settings;
    }

    private static Notification? Single(IReadOnlyList<NotificationOutcome> outcomes)
        => outcomes.Select(o => o.Notification).SingleOrDefault(n => n is not null);

    [Fact]
    public void NullStatusIsNotAReading_ThenDegradedIsABaseline()
    {
        var rules = new NotificationRules();
        Assert.Null(Single(rules.OnStatus([View(Claude, null)], new Settings())));
        Assert.Null(Single(rules.OnStatus([View(Claude, Down(Claude))], new Settings())));
    }

    [Fact]
    public void LaunchingIntoADegradedPageIsSilent_RecoveryThenNotifies()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(Claude, Down(Claude))], new Settings());
        var recovered = Single(rules.OnStatus([View(Claude, Ok(Claude))], new Settings()));
        Assert.NotNull(recovered);
        Assert.Equal("Claude status recovered", recovered!.Title);
        Assert.Equal("Claude status: All Systems Operational", recovered.Body);
        Assert.Equal("status:claude", recovered.Tag);
        Assert.Null(recovered.ExpiresAt);
    }

    [Fact]
    public void GoingDownNamesTheIncidentsInThePagesOwnWords()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(Claude, Ok(Claude))], new Settings());
        var down = Single(rules.OnStatus([View(Claude, Down(Claude,
            incidents: [Incident("Elevated errors on Claude Code", "Claude Code"), Incident("Login issues")]))], new Settings()));
        Assert.Equal("Claude status: Partial System Outage", down!.Title);
        Assert.Equal("Elevated errors on Claude Code, Login issues", down.Body);
    }

    [Fact]
    public void WithoutIncidents_ComponentsWithTheirStatusesAreTheBody()
    {
        // The OpenAI shape: no incidents array, only component statuses.
        var rules = new NotificationRules();
        rules.OnStatus([View(OpenAi, Ok(OpenAi), "codex")], new Settings());
        var down = Single(rules.OnStatus([View(OpenAi, Down(OpenAi, "minor",
            components: [new("Codex API", "partial_outage"), new("Sora", "major_outage")]), "codex")], new Settings()));
        Assert.Equal("Codex API — Partial outage", down!.Body);   // Sora is unwatched
    }

    [Fact]
    public void UnclassifiableDisruption_FallsBackToTheBanner()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(OpenAi, Ok(OpenAi), "codex")], new Settings());
        var down = Single(rules.OnStatus([View(OpenAi, Down(OpenAi), "codex")], new Settings()));
        Assert.NotNull(down);   // IsRelevant fails towards visible
        Assert.Equal("Partial System Outage", down!.Body);
    }

    [Fact]
    public void DegradedToDifferentlyDegradedIsSilent()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(Claude, Ok(Claude))], new Settings());
        rules.OnStatus([View(Claude, Down(Claude, incidents: [Incident("A")]))], new Settings());
        Assert.Null(Single(rules.OnStatus([View(Claude, Down(Claude, "critical", [Incident("A"), Incident("B")]))], new Settings())));
    }

    [Fact]
    public void OutsideTheWatchFilterIsSilent_InsideNotifies()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(OpenAi, Ok(OpenAi), "codex")], new Settings());
        Assert.Null(Single(rules.OnStatus([View(OpenAi, Down(OpenAi, components: [new("Sora", "major_outage")]), "codex")], new Settings())));
        Assert.NotNull(Single(rules.OnStatus([View(OpenAi, Down(OpenAi, components: [new("Sora", "major_outage"), new("Codex Web", "degraded_performance")]), "codex")], new Settings())));
    }

    [Fact]
    public void RecoveryOfTheWatchedPart_WhilePageStillDegraded_DoesNotClaimAllClear()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(OpenAi, Ok(OpenAi), "codex")], new Settings());
        rules.OnStatus([View(OpenAi, Down(OpenAi, components: [new("Codex API", "partial_outage"), new("Sora", "major_outage")]), "codex")], new Settings());
        var partial = Single(rules.OnStatus([View(OpenAi, Down(OpenAi, components: [new("Sora", "major_outage")]), "codex")], new Settings()));
        Assert.NotNull(partial);
        Assert.Equal("OpenAI status recovered", partial!.Title);
        Assert.Equal("OpenAI status: Partial System Outage · outside your watched components", partial.Body);
        Assert.DoesNotContain("operational", partial.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourcesAreIndependent()
    {
        var rules = new NotificationRules();
        var settings = new Settings();
        rules.OnStatus([View(Claude, Ok(Claude)), View(OpenAi, Ok(OpenAi))], settings);
        var outcomes = rules.OnStatus([View(Claude, Down(Claude)), View(OpenAi, Ok(OpenAi))], settings);
        var n = Single(outcomes);
        Assert.Equal("status:claude", n!.Tag);
        // Claude going down did not touch OpenAI's baseline: OpenAI going down next is its own toast.
        Assert.Equal("status:openai", Single(rules.OnStatus([View(Claude, Down(Claude)), View(OpenAi, Down(OpenAi))], settings))!.Tag);
    }

    [Fact]
    public void DisabledSourceDropsItsState_ReenablingRebaselines()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(Claude, Ok(Claude)), View(OpenAi, Ok(OpenAi))], new Settings());
        rules.OnStatus([View(Claude, Ok(Claude))], new Settings());                       // openai disabled
        Assert.Null(Single(rules.OnStatus([View(Claude, Ok(Claude)), View(OpenAi, Down(OpenAi))], new Settings())));
    }

    [Fact]
    public void WideningTheFilterOverAnUnchangedPayloadIsSilent()
    {
        var rules = new NotificationRules();
        var sora = Down(OpenAi, components: [new("Sora", "major_outage")]);
        rules.OnStatus([View(OpenAi, Ok(OpenAi), "codex")], new Settings());
        rules.OnStatus([View(OpenAi, sora, "codex")], new Settings());              // not relevant
        var widened = rules.OnStatus([View(OpenAi, sora, "codex", "sora")], new Settings());   // now relevant — but by the filter
        Assert.Null(Single(widened));
        Assert.Contains(widened.SelectMany(o => o.Log), l => l.Contains("filter change"));
    }

    [Fact]
    public void ChangingNotifyRebaselines_AndOffSuppressesOnlyTheOutput()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(Claude, Ok(Claude))], WithNotify("claude", true));
        var off = rules.OnStatus([View(Claude, Down(Claude))], WithNotify("claude", false));   // notify changed → rebaseline
        Assert.Null(Single(off));
        var recovered = rules.OnStatus([View(Claude, Ok(Claude))], WithNotify("claude", false));   // a real transition, switched off
        Assert.Null(Single(recovered));
        Assert.Contains(recovered.SelectMany(o => o.Log), l => l.Contains("switched off"));
        // Switching back on: notify changed → rebaseline, so the missed transition is not replayed.
        Assert.Null(Single(rules.OnStatus([View(Claude, Ok(Claude))], WithNotify("claude", true))));
        Assert.NotNull(Single(rules.OnStatus([View(Claude, Down(Claude))], WithNotify("claude", true))));
    }

    [Fact]
    public void Summary_PrefersWatchedIncidents_ThenWatchedComponents_ThenNothing()
    {
        var withIncident = Down(Claude, incidents: [Incident("Elevated errors", "API"), Incident("Sora slow", "Sora")],
            components: [new("API", "degraded_performance")]);
        Assert.Equal("Elevated errors", StatusDetail.Summary(withIncident, ["api"]));
        Assert.Equal("Elevated errors, Sora slow", StatusDetail.Summary(withIncident, []));

        var componentsOnly = Down(OpenAi, components: [new("Codex API", "partial_outage"), new("Sora", "major_outage")]);
        Assert.Equal("Codex API — Partial outage", StatusDetail.Summary(componentsOnly, ["codex"]));
        Assert.Equal("Codex API — Partial outage, Sora — Major outage", StatusDetail.Summary(componentsOnly, []));

        Assert.Equal("", StatusDetail.Summary(Down(OpenAi), ["codex"]));
    }

    /// <summary>A badge change caused solely by a filter edit is silent: BadgeDegraded() is
    /// recomputed from scratch on the same Render() while OnStatus rebaselines on its settings
    /// fingerprint and returns nothing. The user's own edit is not news. Same class as the
    /// paced-badge/absolute-toast divergence already recorded in the repo.</summary>
    [Fact]
    public void WideningTheFilterRebaselinesInsteadOfToasting()
    {
        var cowork = Down(Claude, "minor", components: [new PlatformComponent("Claude Cowork", "degraded_performance")]);
        var rules = new NotificationRules();
        var settings = WithNotify("claude", notify: true);

        rules.OnStatus([View(Claude, cowork, "Claude Code")], settings);          // baseline: not relevant
        var outcomes = rules.OnStatus([View(Claude, cowork)], settings);          // filter widened, same payload

        Assert.Null(Single(outcomes));
        Assert.Contains(outcomes.SelectMany(o => o.Log), l => l.Contains("rebaselining"));
    }

    /// <summary>Notify off with a matching filter: the badge is the monitor's business and still
    /// warns; only the toast is suppressed.</summary>
    [Fact]
    public void NotifyOff_WithAMatchingFilter_SuppressesOnlyTheToast()
    {
        var cowork = Down(Claude, "minor", components: [new PlatformComponent("Claude Cowork", "degraded_performance")]);
        var rules = new NotificationRules();
        var settings = WithNotify("claude", notify: false);

        rules.OnStatus([View(Claude, Ok(Claude), "Cowork")], settings);           // baseline: healthy
        Assert.Null(Single(rules.OnStatus([View(Claude, cowork, "Cowork")], settings)));

        var monitor = new StatusMonitor([(Claude, ["Cowork"])]);
        monitor.TakeDue(T0);
        monitor.Accept("claude", cowork, T0);
        Assert.True(monitor.BadgeDegraded());
    }
}
