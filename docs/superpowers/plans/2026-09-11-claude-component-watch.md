# Claude Component Watch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user pick which Claude status-page components they watch, in the settings dialog,
and make the tray badge honour that choice.

**Architecture:** Three small moves on top of machinery that already exists. (1) `PlatformStatusApi`
starts carrying *every* component name from the payload — not just the broken ones — so the dialog
can tell the user what there is to filter on. (2) `StatusMonitor.BadgeDegraded()` stops bypassing the
watch filter and routes through `StatusDetail.IsRelevant`, the same function the popup, tooltip and
toasts already use. (3) `SettingsDialog` grows a real **Platform status** group with a Claude
checkbox and components box mirroring the existing OpenAI pair, plus a greyed caption under each box
listing the page's current component names, fed from a small cache in `TrayApp`.

**Tech Stack:** .NET 10, C# 13, WinForms (`net10.0-windows10.0.19041.0`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-11-claude-component-watch-design.md`

## Global Constraints

- **Logic goes in `Core/` as a pure function and is unit-tested.** `Tray/` is WinForms only and
  decides nothing. Adding a decision to `TrayApp` or a paint method is how this codebase becomes
  untestable.
- **No clocks, no threads in `Core/`.** Every time-dependent function takes a caller-supplied
  `DateTimeOffset now`.
- **No live calls to either status page, in tests or while developing.** Canned payloads only. The
  usage endpoint in particular shares a per-token rolling-hour budget with every running
  `claude.exe`; hammering it causes real 429s for the user's own sessions.
- **Nothing in the read paths throws.** `PlatformStatusApi` swallows IO/JSON errors and returns null.
  A malformed payload degrades the display, never kills the tray.
- **Labels come from the payload**, never from a hardcoded list. A renamed or added component must
  show up with no app update.
- **`StatusSourceRegistry.Claude.DefaultComponents` stays `[]`.** Blank = all, literally: the box
  shows exactly what is stored and stores exactly what is shown. No prefill.
- Target framework for app and tests is `net10.0-windows10.0.19041.0`; nothing else changes.
- Tests run with `dotnet test`. **The local SDK emits German output** — grep for `Bestanden!` /
  `Fehler:`, not `Passed!`.
- End every commit message with the `Claude-Session:` trailer for your own session. Never add a
  Claude/AI co-author trailer.

---

### Task 1: The payload carries every component name

`PlatformStatus.Components` deliberately holds only the non-operational entries, so on a healthy day
the app cannot say what the page even lists. Add a second, parallel member that carries all of the
names — including the healthy ones — for the settings dialog's reference caption, without changing
the shape of anything a display path reads.

**Files:**
- Modify: `src/ClaudeUsageTray/Core/PlatformStatus.cs` (the `PlatformStatus` record)
- Modify: `src/ClaudeUsageTray/Core/PlatformStatusApi.cs` (`FetchAsync`, plus a new reader)
- Test: `tests/ClaudeUsageTray.Tests/PlatformStatusApiTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `PlatformStatus.ComponentNames` — `IReadOnlyList<string>`, an `init` property defaulting
  to `[]`. Read by Task 4 only.

- [ ] **Step 1: Write the failing tests**

Add to `tests/ClaudeUsageTray.Tests/PlatformStatusApiTests.cs`. The payload is modelled on the live
`status.claude.com` summary of 2026-09-11, including the group entry StatusPage puts in the same
array:

```csharp
private const string ClaudeSixComponents = """
    {
      "page": { "id": "tymt9n04zgry", "name": "Claude" },
      "status": { "indicator": "minor", "description": "Partial System Outage" },
      "components": [
        { "id": "1", "name": "claude.ai", "status": "operational", "group": false },
        { "id": "2", "name": "Claude Console (platform.claude.com)", "status": "operational" },
        { "id": "3", "name": "Claude API (api.anthropic.com)", "status": "operational" },
        { "id": "4", "name": "Claude Code", "status": "operational" },
        { "id": "5", "name": "Claude Cowork", "status": "degraded_performance" },
        { "id": "6", "name": "Claude for Government", "status": "operational" },
        { "id": "7", "name": "Products", "status": "operational", "group": true }
      ],
      "incidents": []
    }
    """;

[Fact]
public void ComponentNames_CarryEveryComponent_InArrayOrder()
{
    var (status, _) = Fetch(_ => Json(HttpStatusCode.OK, ClaudeSixComponents));
    Assert.Equal(
        ["claude.ai", "Claude Console (platform.claude.com)", "Claude API (api.anthropic.com)",
         "Claude Code", "Claude Cowork", "Claude for Government"],
        status!.ComponentNames);
}

/// <summary>A group is a heading, not a component: it never appears in Components and never in an
/// incident's component list, so offering its name as a filter token would hand the user a token
/// that matches nothing.</summary>
[Fact]
public void ComponentNames_ExcludeGroups()
{
    var (status, _) = Fetch(_ => Json(HttpStatusCode.OK, ClaudeSixComponents));
    Assert.DoesNotContain("Products", status!.ComponentNames);
}

/// <summary>Components keeps its "no caller can render a wall of healthy components" guarantee.</summary>
[Fact]
public void Components_StillCarryOnlyTheNonOperationalEntry()
{
    var (status, _) = Fetch(_ => Json(HttpStatusCode.OK, ClaudeSixComponents));
    Assert.Equal(["Claude Cowork"], status!.Components.Select(c => c.Name));
}

[Fact]
public void PayloadWithNoComponentsKey_YieldsBothEmpty()
{
    const string body = """
        { "status": { "indicator": "none", "description": "All Systems Operational" }, "incidents": [] }
        """;
    var (status, _) = Fetch(_ => Json(HttpStatusCode.OK, body));
    Assert.Empty(status!.ComponentNames);
    Assert.Empty(status.Components);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~PlatformStatusApiTests`
Expected: compile error — `PlatformStatus` does not contain a definition for `ComponentNames`.

- [ ] **Step 3: Add the member**

In `src/ClaudeUsageTray/Core/PlatformStatus.cs`, extend the record with an `init` property. It is a
property rather than a seventh positional parameter so the eight existing construction sites (one
production, seven test) stay as they are and the default is the safe one:

```csharp
public sealed record PlatformStatus(
    string SourceId, DateTimeOffset FetchedAt, string Indicator, string Description,
    IReadOnlyList<PlatformIncident> Incidents, IReadOnlyList<PlatformComponent> Components)
{
    public bool Degraded => Indicator != "none";

    /// <summary>Every component the page lists, healthy ones included, in the array's own order.
    /// Only the settings dialog reads this, as the reference caption under the watch-filter box —
    /// which is why order is the payload's rather than the page's visual grouping, and why groups
    /// are excluded: a group name is a heading no filter token could ever match.</summary>
    public IReadOnlyList<string> ComponentNames { get; init; } = [];
}
```

- [ ] **Step 4: Fill it in the same pass that builds `Components`**

In `src/ClaudeUsageTray/Core/PlatformStatusApi.cs`, change the return in `FetchAsync` from

```csharp
            return new PlatformStatus(source.Id, now, indicator.GetString()!.Trim(), description,
                incidents, ReadComponents(doc.RootElement));
```

to

```csharp
            return new PlatformStatus(source.Id, now, indicator.GetString()!.Trim(), description,
                incidents, ReadComponents(doc.RootElement))
            {
                ComponentNames = ReadComponentNames(doc.RootElement),
            };
```

and add, next to `ReadComponents`:

```csharp
    /// <summary>Every component's name, healthy ones included — the dialog's reference caption.
    /// Entries with "group": true are headings rather than components and are dropped; an entry
    /// without a name has nothing to offer and is skipped, as in ReadComponents.</summary>
    private static IReadOnlyList<string> ReadComponentNames(JsonElement root)
    {
        if (!root.TryGetProperty("components", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        var names = new List<string>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (entry.TryGetProperty("group", out var group) && group.ValueKind == JsonValueKind.True) continue;
            if (NonEmptyString(entry, "name") is { } name) names.Add(name);
        }
        return names;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~PlatformStatusApiTests`
Expected: all pass (`Bestanden!`).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: all pass. Nothing else reads `ComponentNames` yet, so nothing else can have moved.

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Core/PlatformStatus.cs src/ClaudeUsageTray/Core/PlatformStatusApi.cs tests/ClaudeUsageTray.Tests/PlatformStatusApiTests.cs
git commit -m "feat(status): carry every component name in the payload"
```

---

### Task 2: The badge honours the watch filter

`BadgeDegraded()` consults only `status.Degraded`, on purpose — the comment says the Claude filter is
a JSON-only key that must not be able to disarm the tray's main warning. Task 3 gives it a dialog
control, so that reason expires. Route the badge through `StatusDetail.IsRelevant`, the same function
the popup rows, the tooltip and the toasts already use.

`IsRelevant` returns false for a healthy page, so the `Degraded` check is subsumed rather than
dropped, and its fail-towards-visible rules are unchanged: an empty filter watches everything, an
incident naming no components counts as watched, and a degraded page whose payload identifies nothing
counts as watched. The filter can therefore only ever suppress a disruption the page itself
attributed to a component the user excluded by name.

**Files:**
- Modify: `src/ClaudeUsageTray/Core/StatusMonitor.cs:BadgeDegraded`
- Test: `tests/ClaudeUsageTray.Tests/StatusMonitorTests.cs`

**Interfaces:**
- Consumes: `StatusDetail.IsRelevant(PlatformStatus status, IReadOnlyList<string> filter) -> bool`
  (existing, unchanged).
- Produces: `StatusMonitor.BadgeDegraded() -> bool` — same signature, filtered semantics.

- [ ] **Step 1: Delete the test that locks in the old rule**

`tests/ClaudeUsageTray.Tests/StatusMonitorTests.cs:136-147` holds
`BadgeDegraded_IgnoresAClaudeFilterThatExcludesTheAffectedComponent`, whose doc comment states the
decision this task supersedes ("a README-only JSON key must not be able to disarm the tray's single
most important warning"). It asserts `Assert.True(m.BadgeDegraded())` for filter `["api"]` against a
component named `Claude.ai` — the exact inverse of the new behaviour. **Delete the test and its doc
comment.** Its replacement is `CoworkDegraded_DoesNotBadgeAClaudeCodeWatcher`, added in Step 2.

While there, rename `BadgeDegraded_IgnoresNonBadgeSourcesAndTheFilter` (line 123) to
`BadgeDegraded_IgnoresNonBadgeSources`. It still passes — its Claude filter is `[]` via `Both()` — but
"AndTheFilter" now names the opposite of the rule.

- [ ] **Step 2: Write the failing tests**

Append to `tests/ClaudeUsageTray.Tests/StatusMonitorTests.cs`. Note the existing file-level helpers
(`T0`, `Claude`, `OpenAi`, `Both`, `Ok`) — these tests add their own, since they need a filtered
monitor and a Cowork-shaped payload:

```csharp
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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~StatusMonitorTests`
Expected: FAIL — `CoworkDegraded_DoesNotBadgeAClaudeCodeWatcher` and
`WideningTheFilterLightsTheBadgeWithoutARefetch` report `True` where `False` was expected (the badge
still ignores the filter). The rest already pass.

- [ ] **Step 4: Route the badge through `IsRelevant`**

In `src/ClaudeUsageTray/Core/StatusMonitor.cs`, replace `BadgeDegraded` and its doc comment:

```csharp
    /// <summary>Whether the tray icon should carry the warning marker: a badge-raising source whose
    /// disruption is relevant under its own watch filter. The filter reaches the badge as of
    /// 2026-09-11 — before that it was deliberately bypassed, because the Claude filter had no dialog
    /// control and a JSON-only key must not disarm the tray's main warning. It has one now, and a
    /// filter the user set in the dialog that narrows the popup but not the icon is a filter that
    /// does not work. IsRelevant is false for a healthy page, so the Degraded check is subsumed
    /// rather than dropped, and its fail-towards-visible rules still apply: only a disruption the
    /// page itself attributed to an excluded component can be suppressed here.</summary>
    public bool BadgeDegraded()
        => _entries.Any(e => e.Source.RaisesBadge
                             && e.Status is { } s && StatusDetail.IsRelevant(s, e.Filter));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~StatusMonitorTests`
Expected: all pass, the deleted test included — nothing in the file still asserts the old rule.

- [ ] **Step 6: Add the badge/toast divergence pair**

The two components of the divergence live in different classes, so the pair is asserted in one test
per side. Append to `tests/ClaudeUsageTray.Tests/NotificationRulesStatusTests.cs`:

```csharp
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
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~NotificationRulesStatusTests`
Expected: all pass. Both are divergence guards placed after the implementation rather than red-phase
tests; `WideningTheFilterRebaselinesInsteadOfToasting` is the one that pins the new behaviour.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: all pass. If `StatusDetailRelevanceTests` or the popup tests moved, stop — the badge change
is not supposed to touch any other display path.

- [ ] **Step 9: Commit**

```bash
git add src/ClaudeUsageTray/Core/StatusMonitor.cs tests/ClaudeUsageTray.Tests/StatusMonitorTests.cs tests/ClaudeUsageTray.Tests/NotificationRulesStatusTests.cs
git commit -m "feat(status): the tray badge honours the watch filter"
```

---

### Task 3: A real "Platform status" group with Claude controls

There is no Claude block to mirror today, and the OpenAI one is not a block: `_watchOpenAi` and its
components box are appended to `Heading("Colour thresholds")`, between the pace-colours checkbox and
the preview swatch. Introduce the group that was missing, move the OpenAI pair into it, and add the
Claude pair above it.

The notify checkboxes stay under **Notifications**, where `notifyClaude` and `notifyOpenAi` already
sit together — moving them would split the notification settings across two groups to fix a smaller
asymmetry than it creates. `_preview` and `_previewCaption` stay under **Colour thresholds**.

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/SettingsDialog.cs` (fields, `BuildLayout`, `BuildButtons`
  TabIndex array, `LoadFrom`, `WireLiveSync`, `Draft`)
- Test: `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`

**Interfaces:**
- Consumes: `ComponentFilter.Parse(string?) -> IReadOnlyList<string>`,
  `ComponentFilter.Format(IReadOnlyList<string>) -> string` (existing).
- Produces: new named controls `watchClaude` (CheckBox) and `claudeComponents` (TextBox), findable
  via `Controls.Find(name, searchAllChildren: true)`. `Draft()` writes all three fields of
  `StatusSources["claude"]`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`, next to the OpenAI ones. Reuse the
file's existing `Dialog`, `Find<T>`, `Check` and `Button` helpers:

```csharp
    private static CheckBox WatchClaude(SettingsDialog d) => Find<CheckBox>(d, "watchClaude")!;
    private static TextBox ClaudeComponents(SettingsDialog d) => Find<TextBox>(d, "claudeComponents")!;

    /// <summary>Claude is on by default and watches everything: blank = all, literally — the box
    /// shows exactly what is stored, and DefaultComponents is empty.</summary>
    [Fact]
    public void ClaudeWatch_DefaultsToOnAndBlank()
    {
        var dialog = Dialog(new Settings());
        Assert.True(WatchClaude(dialog).Checked);
        Assert.Equal("", ClaudeComponents(dialog).Text);
        Assert.True(ClaudeComponents(dialog).Enabled);
    }

    [Fact]
    public void ClaudeCheckbox_ReflectsSettings_AndDrivesTheDraft()
    {
        var settings = new Settings();
        settings.StatusSources["claude"] = new StatusSourceSettings { Enabled = true, Components = ["Claude Code"] };
        var dialog = Dialog(settings);

        Assert.True(WatchClaude(dialog).Checked);
        Assert.Equal("Claude Code", ClaudeComponents(dialog).Text);

        ClaudeComponents(dialog).Text = "Claude Code, api.anthropic.com";
        var draft = dialog.Draft();
        Assert.True(draft.StatusSources["claude"]!.Enabled);
        Assert.Equal(["Claude Code", "api.anthropic.com"], draft.StatusSources["claude"]!.Components);
    }

    /// <summary>Unchecking disables rather than clears, so turning the page off and on again keeps
    /// both the typed filter and the notify choice.</summary>
    [Fact]
    public void UncheckedClaude_DisablesItsFieldsWithoutClearingThem()
    {
        var dialog = Dialog(new Settings());
        ClaudeComponents(dialog).Text = "Claude Code";
        Check(dialog, "notifyClaude").Checked = true;

        WatchClaude(dialog).Checked = false;
        Assert.False(ClaudeComponents(dialog).Enabled);
        Assert.False(Check(dialog, "notifyClaude").Enabled);
        Assert.Equal("Claude Code", ClaudeComponents(dialog).Text);
        Assert.True(Check(dialog, "notifyClaude").Checked);

        var draft = dialog.Draft();
        Assert.False(draft.StatusSources["claude"]!.Enabled);
        Assert.Equal(["Claude Code"], draft.StatusSources["claude"]!.Components);
        Assert.True(draft.StatusSources["claude"]!.Notify);
    }

    [Fact]
    public void ClaudeComponents_SaveExactlyTheTypedTokens()
    {
        var dialog = Dialog(new Settings());
        ClaudeComponents(dialog).Text = " Claude Code ,, Cowork ";
        Assert.Equal(["Claude Code", "Cowork"], dialog.Draft().StatusSources["claude"]!.Components);
    }

    /// <summary>A hand-written six-name filter must survive an untouched open/save round trip: the
    /// dialog is now the owner of all three fields, so a bug here silently rewrites settings.json.</summary>
    [Fact]
    public void HandWrittenClaudeFilter_SurvivesAnUntouchedRoundTrip()
    {
        string[] six =
        [
            "claude.ai", "Claude Console (platform.claude.com)", "Claude API (api.anthropic.com)",
            "Claude Code", "Claude Cowork", "Claude for Government",
        ];
        var settings = new Settings();
        settings.StatusSources["claude"] = new StatusSourceSettings
            { Enabled = true, Notify = false, Components = [.. six] };

        var draft = Dialog(settings).Draft();
        Assert.Equal(six, draft.StatusSources["claude"]!.Components);
        Assert.True(draft.StatusSources["claude"]!.Enabled);
        Assert.False(draft.StatusSources["claude"]!.Notify);
    }

    /// <summary>"Reset to defaults" is scoped to the colour thresholds and staleness. Which status
    /// pages are watched is a preference of the same kind as the display mode, not a colour.</summary>
    [Fact]
    public void ResetLeavesTheClaudeSourceAlone()
    {
        var settings = new Settings();
        settings.StatusSources["claude"] = new StatusSourceSettings { Enabled = false, Components = ["Claude Code"] };
        var dialog = Dialog(settings);
        Button(dialog, "reset").PerformClick();

        Assert.False(WatchClaude(dialog).Checked);
        Assert.Equal("Claude Code", ClaudeComponents(dialog).Text);
    }
```

Delete the now-obsolete `ClaudeFilter_SurvivesTheRoundTrip` test in the same file — its doc comment
asserts the filter is "an advanced JSON-only key with no control here", which this task makes false,
and `HandWrittenClaudeFilter_SurvivesAnUntouchedRoundTrip` covers the behaviour it protected.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SettingsDialogTests`
Expected: FAIL — `Find<CheckBox>(d, "watchClaude")` returns null, so the helpers throw
`NullReferenceException`.

- [ ] **Step 3: Add the two controls**

In `src/ClaudeUsageTray/Tray/SettingsDialog.cs`, next to the OpenAI field declarations:

```csharp
    private readonly CheckBox _watchClaude = new()
        { Name = "watchClaude", Text = "Watch Claude status", AutoSize = true };
    private readonly TextBox _claudeComponents = new() { Name = "claudeComponents", Width = 240 };
    private readonly Label _claudeComponentsCaption = new()
        { Text = "Components (comma-separated, blank = all)", AutoSize = true };
```

- [ ] **Step 4: Build the group in `BuildLayout`**

Remove these three lines from the **Colour thresholds** block:

```csharp
        layout.Controls.Add(_watchOpenAi);
        layout.Controls.Add(Indent(_openAiComponentsCaption));
        layout.Controls.Add(Indent(_openAiComponents));
```

and insert a new group between the **Colour thresholds** block (which now ends with
`layout.Controls.Add(Indent(_previewCaption));`) and `layout.Controls.Add(Heading("Notifications"));`:

```csharp
        // Both pages, each with its own watch filter. The notify checkboxes stay under
        // Notifications, where the two of them already sit together.
        layout.Controls.Add(Heading("Platform status"));
        layout.Controls.Add(Indent(_watchClaude));
        layout.Controls.Add(Indent(_claudeComponentsCaption));
        layout.Controls.Add(Indent(_claudeComponents));
        layout.Controls.Add(Indent(_watchOpenAi));
        layout.Controls.Add(Indent(_openAiComponentsCaption));
        layout.Controls.Add(Indent(_openAiComponents));
```

Note `_watchOpenAi` gains an `Indent(...)` it did not have before — it was a group heading in all but
name; under a real heading it is a member like the rest.

- [ ] **Step 5: Put the new controls in tab order**

In `BuildButtons`, insert the Claude pair before the OpenAI pair in the hand-written array so Tab
reaches them in visual order:

```csharp
        foreach (var control in new Control[]
                 { _modeFive, _modeSeven, _modeBoth, _startup, _orange, _red, _paceColors, _staleness,
                   _desktopStaleness, _weeklyAnchor, _betaReleases, _watchClaude, _claudeComponents,
                   _watchOpenAi, _openAiComponents, _notifyUsage, _notifyLevel, _notifyClaude,
                   _notifyOpenAi, reset, cancel, save })
            control.TabIndex = order++;
```

- [ ] **Step 6: Load, wire and save the Claude entry**

In `LoadFrom`, replace the single `_notifyClaude.Checked = …` line with the full block, placed just
above the OpenAI lines it mirrors:

```csharp
        var claude = source.StatusSources.GetValueOrDefault("claude");
        _watchClaude.Checked = claude?.Enabled ?? true;
        _claudeComponents.Text = ComponentFilter.Format(
            claude?.Components ?? [.. StatusSourceRegistry.Claude.DefaultComponents]);
        _claudeComponents.Enabled = _watchClaude.Checked;
        _notifyClaude.Checked = claude?.Notify ?? true;
        _notifyClaude.Enabled = _watchClaude.Checked;
```

In `WireLiveSync`, next to the `_watchOpenAi` handler:

```csharp
        _watchClaude.CheckedChanged += (_, _) =>
        {
            _claudeComponents.Enabled = _watchClaude.Checked;
            _notifyClaude.Enabled = _watchClaude.Checked;   // disabled, not unchecked: the choice survives
        };
```

In `Draft()`, replace the "Claude has no enabled/components controls" block with all three fields:

```csharp
        // All three fields are edited here now; the filter and the notify choice are kept even when
        // unchecked, so turning the source back on does not lose either.
        draft.StatusSources["claude"] = new StatusSourceSettings
        {
            Enabled = _watchClaude.Checked,
            Notify = _notifyClaude.Checked,
            Components = [.. ComponentFilter.Parse(_claudeComponents.Text)],
        };
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SettingsDialogTests`
Expected: all pass.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: all pass. `SettingsTests` in particular: a `claude` entry with `enabled: false` and a
components list must still survive a load/save round trip, and a malformed entry must still degrade to
defaults without throwing. If either is missing from `SettingsTests`, add it now — Task 3 is the first
change that lets the dialog write `enabled: false` for Claude:

These use the file's own helpers — `PathFor(name)` off the class-level temp directory (line 9) and
the static `LoadJson(json)` (line 205) — so they are drop-in:

```csharp
    /// <summary>Turning Claude off is reachable from the dialog as of 2026-09-11, so the disabled
    /// entry has to survive a save the way OpenAI's already does.</summary>
    [Fact]
    public void DisabledClaudeEntry_RoundTripsThroughSave()
    {
        var path = PathFor("claude-off.json");
        var s = new Settings();
        s.StatusSources["claude"] = new StatusSourceSettings
            { Enabled = false, Notify = true, Components = ["Claude Code"] };
        s.Save(path);

        var loaded = Settings.Load(path);
        Assert.False(loaded.StatusSources["claude"]!.Enabled);
        Assert.Equal(["Claude Code"], loaded.StatusSources["claude"]!.Components);
        Assert.Empty(loaded.EnabledSources());
    }

    [Fact]
    public void MalformedClaudeEntry_DegradesToDefaultsWithoutThrowing()
    {
        var s = LoadJson("""{ "stalenessMinutes": 42, "statusSources": { "claude": "yes please" } }""");
        Assert.Equal(42, s.StalenessMinutes);                                      // unrelated settings survive
        Assert.Equal(["claude"], s.EnabledSources().Select(e => e.Source.Id));      // back to the default, on
    }
```

- [ ] **Step 9: Commit**

```bash
git add src/ClaudeUsageTray/Tray/SettingsDialog.cs tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs tests/ClaudeUsageTray.Tests/SettingsTests.cs
git commit -m "feat(settings): a Platform status group with Claude watch controls"
```

---

### Task 4: The component-name caption

Discovery comes from a greyed caption under each box listing the page's current components, so the
user can see what there is to exclude and type or paste the ones that apply. It keeps working *after*
the user narrows the list — it still names a component Anthropic adds later, which a one-time prefill
into the box could not.

The names are frozen at open time, for the same reason `desktopSource` is: a caption that rewrites
itself under a half-made edit is worse than one briefly out of date. They come from a small cache in
`TrayApp` rather than live from `StatusMonitor`, because the monitor holds no entry for a disabled
source — so `Status("claude")` is null exactly when the user has opened the dialog to turn Claude back
on and narrow it, the moment the names are most wanted.

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/SettingsDialog.cs` (constructor, two new hint labels,
  `BuildLayout`)
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs` (`_componentNames` field,
  `OnStatusFetchCompleted`, `ShowSettings`)
- Test: `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`

**Interfaces:**
- Consumes: `PlatformStatus.ComponentNames` (Task 1); the `watchClaude` / `claudeComponents` controls
  (Task 3).
- Produces: `SettingsDialog` constructor gains a seventh parameter
  `IReadOnlyDictionary<string, IReadOnlyList<string>> componentNames`, keyed by
  `StatusSource.Id`; new named labels `claudeComponentsHint` and `openAiComponentsHint`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`:

```csharp
    private static Label Hint(SettingsDialog d, string name) => Find<Label>(d, name)!;

    [Fact]
    public void Hint_ListsTheSuppliedNames()
    {
        var dialog = Dialog(new Settings(), componentNames: new Dictionary<string, IReadOnlyList<string>>
        {
            ["claude"] = ["claude.ai", "Claude Code", "Claude Cowork"],
        });
        Assert.Equal("Page lists: claude.ai, Claude Code, Claude Cowork",
            Hint(dialog, "claudeComponentsHint").Text);
    }

    /// <summary>Nothing fetched yet — including the case the cache exists for: the user opened the
    /// dialog to re-enable a source the monitor holds no entry for. Both branches of HintFor: a
    /// missing key (openai) and a present-but-empty list (claude, as a "components": [] payload
    /// would produce).</summary>
    [Fact]
    public void Hint_FallsBackWhenNoNamesAreKnown()
    {
        var dialog = Dialog(new Settings(), componentNames: new Dictionary<string, IReadOnlyList<string>>
        {
            ["claude"] = [],
        });
        Assert.Equal("Page lists: not fetched yet", Hint(dialog, "claudeComponentsHint").Text);
        Assert.Equal("Page lists: not fetched yet", Hint(dialog, "openAiComponentsHint").Text);
    }

    /// <summary>The caption is a reference, not a prefill: it never reaches the box or the draft.
    /// Blank = all stays literally true.</summary>
    [Fact]
    public void Hint_NeverPrefillsTheBox()
    {
        var dialog = Dialog(new Settings(), componentNames: new Dictionary<string, IReadOnlyList<string>>
        {
            ["claude"] = ["claude.ai", "Claude Code"],
        });
        Assert.Equal("", ClaudeComponents(dialog).Text);
        Assert.Empty(dialog.Draft().StatusSources["claude"]!.Components!);
    }
```

Extend the file's `Dialog` helper with the new parameter:

```csharp
    private SettingsDialog Dialog(Settings settings, Func<Settings, bool>? save = null,
        bool runAtStartup = true, bool desktopSource = false,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? componentNames = null)
    {
        var dialog = new SettingsDialog(settings, canRunAtStartup: true, runAtStartup,
            save ?? (_ => true), TestUpdateOptions.Inert(), desktopSource,
            componentNames ?? new Dictionary<string, IReadOnlyList<string>>());
        _open.Add(dialog);
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new System.Drawing.Point(-4000, -4000);
        dialog.Show();
        return dialog;
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SettingsDialogTests`
Expected: compile error — `SettingsDialog` has no constructor taking seven arguments.

- [ ] **Step 3: Add the hint labels and the constructor parameter**

In `src/ClaudeUsageTray/Tray/SettingsDialog.cs`, next to the components boxes. `MaximumSize` with
`AutoSize` makes the label wrap instead of stretching the auto-sized dialog to the width of six long
component names:

```csharp
    private readonly Label _claudeComponentsHint = new()
    {
        Name = "claudeComponentsHint",
        AutoSize = true,
        MaximumSize = new Size(320, 0),
        ForeColor = SystemColors.GrayText,
    };
    private readonly Label _openAiComponentsHint = new()
    {
        Name = "openAiComponentsHint",
        AutoSize = true,
        MaximumSize = new Size(320, 0),
        ForeColor = SystemColors.GrayText,
    };
```

Extend the constructor signature and doc comment:

```csharp
    /// <param name="componentNames">Every component each page currently lists, keyed by source id —
    /// the greyed caption under each watch-filter box. Frozen at open time for the same reason
    /// <paramref name="desktopSource"/> is, and supplied from TrayApp's own cache rather than read
    /// live: StatusMonitor holds no entry for a disabled source, which is exactly the case where the
    /// user has opened this dialog to turn a page back on and narrow it. A missing or empty entry
    /// reads "not fetched yet".</param>
    public SettingsDialog(Settings settings, bool canRunAtStartup, bool runAtStartup,
        Func<Settings, bool> save, UpdateOptions updates, bool desktopSource,
        IReadOnlyDictionary<string, IReadOnlyList<string>> componentNames)
    {
```

and, in the constructor body next to `_desktopSource = desktopSource;`:

```csharp
        _claudeComponentsHint.Text = HintFor(componentNames, StatusSourceRegistry.Claude.Id);
        _openAiComponentsHint.Text = HintFor(componentNames, StatusSourceRegistry.OpenAi.Id);
```

Add the helper next to the other static helpers:

```csharp
    /// <summary>The page's own component names, as a reference caption. Never a prefill: the box
    /// shows exactly what is stored, so "blank = all" stays literally true.</summary>
    private static string HintFor(IReadOnlyDictionary<string, IReadOnlyList<string>> names, string sourceId)
        => names.TryGetValue(sourceId, out var list) && list.Count > 0
            ? "Page lists: " + string.Join(", ", list)
            : "Page lists: not fetched yet";
```

- [ ] **Step 4: Put the captions in the layout**

In `BuildLayout`, extend the **Platform status** group so each hint sits under its own box:

```csharp
        layout.Controls.Add(Heading("Platform status"));
        layout.Controls.Add(Indent(_watchClaude));
        layout.Controls.Add(Indent(_claudeComponentsCaption));
        layout.Controls.Add(Indent(_claudeComponents));
        layout.Controls.Add(Indent(_claudeComponentsHint));
        layout.Controls.Add(Indent(_watchOpenAi));
        layout.Controls.Add(Indent(_openAiComponentsCaption));
        layout.Controls.Add(Indent(_openAiComponents));
        layout.Controls.Add(Indent(_openAiComponentsHint));
```

The hints are labels and take no tab stop, so the `BuildButtons` array is unchanged.

- [ ] **Step 5: Cache the names in `TrayApp` and pass them in**

In `src/ClaudeUsageTray/Tray/TrayApp.cs`, add the field next to `_statusMonitor`:

```csharp
    // Last component names seen per source, for the settings dialog's reference caption. Kept here
    // rather than read from StatusMonitor because the monitor drops the entry for a disabled source,
    // and a user opening the dialog to re-enable one is precisely who needs the names.
    private readonly Dictionary<string, IReadOnlyList<string>> _componentNames =
        new(StringComparer.OrdinalIgnoreCase);
```

In `OnStatusFetchCompleted`, file the names whenever a fetch produced a payload. Put it **above** the
`_statusMonitor.Accept(...)` guard, as the first thing the handler does — a fetch that completes for a
source disabled mid-flight is discarded by `Accept`, and that is exactly the source whose names the
caption will be asked for when the user re-enables it. Do not put it inside the
`if (result is null)` / `else if (result.Degraded)` chain below: that would break the `else if`.

The `SourceId` comparison is not optional. `Accept` refuses a mismatched payload for the reason its
doc comment gives — one source must never file its data under another's id — and the cache owes the
same guarantee:

```csharp
    private void OnStatusFetchCompleted(string sourceId, PlatformStatus? result)
    {
        var now = DateTimeOffset.UtcNow;
        // For the settings dialog's reference caption, and ahead of Accept so a source disabled
        // mid-flight still leaves its names behind. A failed fetch keeps the previous names, the way
        // Accept keeps the previous status; a payload under the wrong id is refused here too.
        if (result is not null
            && string.Equals(result.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
        {
            _componentNames[sourceId] = result.ComponentNames;
        }
        if (!_statusMonitor.Accept(sourceId, result, now))
        {
```

In `ShowSettings`, pass the cache:

```csharp
        _settingsDialog = new SettingsDialog(_settings, _isVelopackInstalled, TryIsStartupEnabled(),
            ApplySettings, BuildUpdateOptions(),
            SourceSelection.Choose(_cliSnapshot, _desktopSnapshot, DateTimeOffset.UtcNow, _settings)
                .Snapshot?.Source == UsageSource.DesktopHistory,
            _componentNames);
```

- [ ] **Step 6: Update the third construction site**

There are exactly three `new SettingsDialog(` sites: `Tray/TrayApp.cs:503` (Step 5),
`SettingsDialogTests.cs:22` (Step 1), and `SettingsDialogUpdateTests.cs:26`, which builds its own
instance rather than sharing a helper. Until it is fixed the **whole test assembly fails to compile**
with CS7036, so no filtered test run can pass. Change it to:

```csharp
        var dialog = new SettingsDialog(settings ?? new Settings(), canRunAtStartup: true, runAtStartup: true,
            save: save ?? (s => { _saved.Add(s); return true; }), updates, desktopSource: false,
            new Dictionary<string, IReadOnlyList<string>>());
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SettingsDialog`
Expected: all pass — the filter covers both `SettingsDialogTests` and `SettingsDialogUpdateTests`.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 9: Verify the dialog draws**

The layout changed, so check it renders rather than assuming. Write a throwaway probe under
`tests/ClaudeUsageTray.Tests/`, capture with `Control.DrawToBitmap`, and **call `CreateControl()`,
never `Show()`** — with no message loop, `Show()` triggers an activate/deactivate cycle and you get a
blank render with zero controls and no error:

```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class SettingsDialogProbe
{
    [Fact]
    public void Capture()
    {
        using var dialog = new SettingsDialog(new Settings(), canRunAtStartup: true, runAtStartup: false,
            _ => true, TestUpdateOptions.Inert(), desktopSource: false,
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["claude"] = ["claude.ai", "Claude Console (platform.claude.com)",
                              "Claude API (api.anthropic.com)", "Claude Code", "Claude Cowork",
                              "Claude for Government"],
            });
        dialog.CreateControl();
        using var bmp = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        bmp.Save(Path.Combine(Path.GetTempPath(), "settings-probe.png"), ImageFormat.Png);
    }
}
```

Run: `dotnet test --filter FullyQualifiedName~SettingsDialogProbe`, then open the PNG. Confirm: a
bold **Platform status** heading; the Claude checkbox, caption, box and greyed six-name hint wrapped
over a couple of lines; the OpenAI four below it; the dialog no wider than before by more than the
320 px hint column. **Delete `SettingsDialogProbe.cs` afterwards** — it is a verification artifact,
not a test.

- [ ] **Step 10: Commit**

```bash
git add src/ClaudeUsageTray/Tray/SettingsDialog.cs src/ClaudeUsageTray/Tray/TrayApp.cs tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs tests/ClaudeUsageTray.Tests/SettingsDialogUpdateTests.cs
git commit -m "feat(settings): list the page's components under each watch filter"
```

---

### Task 5: Documentation

Three documents currently record the superseded decision. The README still calls the Claude filter
JSON-only and promises it "never the badge"; the 2026-08-26 platform-status spec rejects
per-component filtering outright; and the behaviour change needs a changelog entry, written for the
person deciding whether to install it.

**Files:**
- Modify: `README.md` (the `enabled` bullet in the `statusSources` section, the "Only Claude's status
  can mark the tray icon" paragraph, and the OpenAI-only sentence at line 315 in **Notifications**)
- Modify: `docs/superpowers/specs/2026-08-26-platform-status-design.md` (the *Warning semantics*
  bullet at ~line 72)
- Modify: `CHANGELOG.md` (the Unreleased section)

**Interfaces:** none — documentation only.

- [ ] **Step 1: Update the README `statusSources` bullets**

In the `### statusSources` section, replace the `enabled` bullet so it names both toggles:

```markdown
- `enabled` — poll this page. Claude is on by default, OpenAI off; both toggles and both component
  lists are in **Settings → Platform status**.
```

- [ ] **Step 2: Rewrite the badge paragraph**

Replace the paragraph beginning **"Only Claude's status can mark the tray icon."** with:

```markdown
**Only Claude's status can mark the tray icon**, and only for a component you watch. An OpenAI outage
appears in the popup and the tooltip and leaves the badge alone, because it says nothing about your
Claude usage headroom. A Claude disruption confined to components outside your `claude` filter is
shown in the popup, greyed, and leaves the badge alone too — the badge, the popup rows, the tooltip
and the toasts all decide relevance the same way. A disruption the page cannot attribute to any
component still marks the icon whatever your filter says.

Each components box in **Settings → Platform status** carries a greyed caption listing what that page
currently lists, so you can see what there is to exclude. It is a reference, not a prefill: the box
stores exactly what you type, a blank box watches everything including components added later, and
changing the filter re-decides the badge on the spot with no refetch — silently, since your own edit
is not news worth a toast.
```

- [ ] **Step 3: Fix the Notifications section's OpenAI-only claim**

`README.md:315` still reads *"For OpenAI only the watched components count, so a Sora outage stays
quiet while a Codex one does not."* After Task 2 that is true for Claude as well, and "For OpenAI
only" now reads as an explicit denial of the new behaviour. Replace that sentence with:

```markdown
  Only the components you watch count, for either page, so a disruption confined to the rest of the
  page stays quiet.
```

The `statusSources` table row at `README.md:269` is deliberately left as it is — "which of their
components matter" is still an accurate summary of the key.

- [ ] **Step 4: Update the superseded spec bullet**

In `docs/superpowers/specs/2026-08-26-platform-status-design.md`, replace the bullet beginning
`- A "minor" incident on any component on the page`:

```markdown
- A `"minor"` incident on any component on the page shows the badge. Rejected alternative at the
  time: per-component filtering — it would require deciding which of the six components matter to
  which user, encoding that in settings, and re-evaluating on every component rename.
  **Superseded on 2026-09-11** by
  [2026-09-11-claude-component-watch-design.md](2026-09-11-claude-component-watch-design.md): the six
  components stopped describing one product (Cowork, Government), the names now come from the payload
  rather than being encoded anywhere, and the badge routes through the same `StatusDetail.IsRelevant`
  the popup and toasts use. The banner still decides how loud a disruption is; the filter decides
  only which disruptions count.
```

- [ ] **Step 5: Add the changelog entry**

Add an `## [Unreleased]` section above `## [0.7.3-beta.4]` in `CHANGELOG.md` (or extend it if one
already exists):

```markdown
## [Unreleased]

### Added
- **Settings → Platform status**: a proper group for both status pages. Claude now has its own
  *Watch Claude status* checkbox and components filter, alongside OpenAI's, and each filter box lists
  the components that page currently reports so you can see what there is to exclude.

### Changed
- A Claude disruption affecting only components you do not watch no longer marks the tray icon. It
  still shows in the popup, greyed. A disruption the page cannot attribute to any component marks the
  icon whatever your filter says, and an empty filter — the default — still watches everything, so
  nothing changes unless you narrow the list yourself.
```

- [ ] **Step 6: Verify**

Run: `dotnet test`
Expected: all pass (documentation only, but the branch must be green before it is called done).

Re-read the three edited passages against
`docs/superpowers/specs/2026-09-11-claude-component-watch-design.md` §2 and §4. The README must not
promise anywhere that the badge ignores the filter.

- [ ] **Step 7: Commit**

```bash
git add README.md CHANGELOG.md docs/superpowers/specs/2026-08-26-platform-status-design.md
git commit -m "docs: record the filtered badge and the Platform status group"
```

---

## Notes for the reviewer

Two consistency limits are deliberate and must survive review — both are recorded in the design doc:

- **A badge change caused solely by a filter edit is silent.** `NotificationRules.Status.OnStatus`
  rebaselines and returns nothing when its settings fingerprint changes, while `BadgeDegraded()` is
  recomputed from scratch on the same `Render()`. Widening the filter against retained degraded data
  lights the icon with no toast; narrowing it clears the icon with no toast. Asserted in Task 2
  Step 5.
- **Staleness does not reach the badge.** `Accept` keeps the last-known-good status when a fetch
  fails and `BadgeDegraded()` takes no clock, so the popup marks that state `· stale` and the icon
  keeps warning. Unchanged here, and the safer direction: a real outage must not vanish because *our*
  network is down.

One narrow gap is accepted rather than fixed: `PlatformStatusApi.ReadComponents` drops entries
missing `name` or `status`, so a payload whose *only* affected component is malformed while an
unrelated one is degraded would leave `Identifies()` true with nothing matching, and the badge off.
A malformed StatusPage payload is not a case worth widening the rule for.
