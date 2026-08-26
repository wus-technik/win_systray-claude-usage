# OpenAI Status Source Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add OpenAI's status page as an optional second platform-status source, shown in the popup and tooltip but never on the tray badge, with a per-source component watch filter.

**Architecture:** Status sources become data (`StatusSource` records in a registry). A clock-free `StatusMonitor` in `Core/` owns one scheduler and one last-known-good result per enabled source; `TrayApp` keeps only the HTTP call and the UI-thread marshalling. All display decisions — relevance, row selection, headers, tooltip suffixes — move into pure `Core/` functions that the test project exercises directly.

**Tech Stack:** .NET 10, C# 13, WinForms (`Tray/` only), `System.Text.Json`, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-26-openai-status-source-design.md`

**Issue:** [#17](https://github.com/wus-technik/win_systray-claude-usage/issues/17) — every commit in this plan ends its body with `Refs #17` so the issue timeline picks the work up; the final commit of the last task uses `Closes #17`.

## Global Constraints

- **Pure logic goes in `src/ClaudeUsageTray/Core/`**, WinForms only in `src/ClaudeUsageTray/Tray/`. No clocks and no threads in `Core/`: every time-dependent function takes a caller-supplied `DateTimeOffset now`.
- **Nothing in a read path throws.** Parsers and `Settings.Load` swallow IO/JSON errors and return null/defaults. A malformed file degrades the display, never kills the tray.
- **The token is read-only.** No credential material is written or logged anywhere, `fetch.log` included. (Status endpoints are unauthenticated; this task touches no credentials.)
- **Labels come from the payload**, never from a hardcoded product list.
- **Absent data means no row.** Never render a placeholder `0 %`, `—`, or an empty section.
- **Badge rule:** `BadgeDegraded` is `RaisesBadge && Degraded`. It never consults the component filter, for any source.
- **Unclassifiable degradation is always relevant.** A degraded page that identifies no affected components must show coloured and must produce a tooltip suffix.
- **Test commands:** `dotnet test` (all), `dotnet test --filter FullyQualifiedName~ClassName` (one class). CI runs `dotnet test -c Release`.
- **Style:** match surrounding code. There is no linter or formatter step. XML doc comments on Core types explain *why*, not *what*.
- **Naming deviation from the spec:** the registry class is `StatusSourceRegistry`, not `StatusSources`. `Settings.StatusSources` is a property of the same name, and inside `Settings.cs` a `StatusSources.All` reference would bind to the property and fail to compile. Everything else keeps the spec's names.
- **Commits:** one per task, conventional-commit prefix (`feat:`, `refactor:`, `docs:`), and a
  `Refs #17` line in the body (`Closes #17` on the last task). No AI co-author trailer.

---

### Task 1: Status source registry

**Files:**
- Create: `src/ClaudeUsageTray/Core/StatusSource.cs`
- Test: `tests/ClaudeUsageTray.Tests/StatusSourceRegistryTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `StatusSource(string Id, string DisplayName, string SummaryUrl, string PageUrl, string PageLabel, bool RaisesBadge, IReadOnlyList<string> DefaultComponents)`; `StatusSourceRegistry.Claude`, `.OpenAi`, `.All` (`IReadOnlyList<StatusSource>`, Claude first), `.ById(string? id)` returning `StatusSource?`; `SourceView(StatusSource Source, PlatformStatus? Status, IReadOnlyList<string> Filter)`.

- [ ] **Step 1: Write the failing test**

Create `tests/ClaudeUsageTray.Tests/StatusSourceRegistryTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class StatusSourceRegistryTests
{
    [Fact]
    public void Registry_IsClaudeThenOpenAi()
    {
        Assert.Equal(["claude", "openai"], StatusSourceRegistry.All.Select(s => s.Id));
    }

    [Fact]
    public void OnlyClaude_RaisesTheBadge()
    {
        Assert.True(StatusSourceRegistry.Claude.RaisesBadge);
        Assert.False(StatusSourceRegistry.OpenAi.RaisesBadge);
    }

    [Fact]
    public void Endpoints_AreTheVerifiedSummaryUrls()
    {
        Assert.Equal("https://status.claude.com/api/v2/summary.json", StatusSourceRegistry.Claude.SummaryUrl);
        Assert.Equal("https://status.openai.com/api/v2/summary.json", StatusSourceRegistry.OpenAi.SummaryUrl);
    }

    [Fact]
    public void ClaudeWatchesEverything_OpenAiDefaultsToTheCodexSet()
    {
        Assert.Empty(StatusSourceRegistry.Claude.DefaultComponents);
        Assert.Equal(["codex", "responses", "login", "vs code extension"],
            StatusSourceRegistry.OpenAi.DefaultComponents);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("CLAUDE")]
    [InlineData("Claude")]
    public void ById_IsCaseInsensitive(string id)
        => Assert.Same(StatusSourceRegistry.Claude, StatusSourceRegistry.ById(id));

    [Theory]
    [InlineData("gemini")]
    [InlineData("")]
    [InlineData(null)]
    public void ById_ReturnsNullForUnknown(string? id) => Assert.Null(StatusSourceRegistry.ById(id));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~StatusSourceRegistryTests`
Expected: FAIL — build error, `StatusSourceRegistry` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/ClaudeUsageTray/Core/StatusSource.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>One public status page the tray may watch. Everything that differs between pages is a
/// field here rather than a branch at the call site — <see cref="RaisesBadge"/> in particular, so
/// "an OpenAI outage never marks the tray icon" is a value a test can assert.</summary>
/// <param name="Id">The settings.json token. Lower-case ASCII.</param>
/// <param name="DefaultComponents">Watch filter used when settings carry no list for this source.
/// Empty means watch every component.</param>
public sealed record StatusSource(
    string Id,
    string DisplayName,
    string SummaryUrl,
    string PageUrl,
    string PageLabel,
    bool RaisesBadge,
    IReadOnlyList<string> DefaultComponents);

/// <summary>A source paired with its current state, as the popup and the tooltip consume it, so
/// neither reaches back into <see cref="Settings"/>.</summary>
public sealed record SourceView(StatusSource Source, PlatformStatus? Status, IReadOnlyList<string> Filter);

/// <summary>The curated set of watchable pages. No user-supplied URLs: the app only ever fetches a
/// host it ships, and only payload shapes that were verified by hand. Exactly these two are
/// supported and tested; the registry is generic so the badge rule and the watch filter are data,
/// not because a third source is planned.</summary>
public static class StatusSourceRegistry
{
    public static readonly StatusSource Claude = new(
        Id: "claude",
        DisplayName: "Claude",
        SummaryUrl: "https://status.claude.com/api/v2/summary.json",
        PageUrl: "https://status.claude.com",
        PageLabel: "status.claude.com",
        RaisesBadge: true,
        // All six of Claude's components matter to this app; the field exists for symmetry.
        DefaultComponents: []);

    public static readonly StatusSource OpenAi = new(
        Id: "openai",
        DisplayName: "OpenAI",
        SummaryUrl: "https://status.openai.com/api/v2/summary.json",
        PageUrl: "https://status.openai.com",
        PageLabel: "status.openai.com",
        RaisesBadge: false,
        // 25 components span products a Codex user does not use (Sora, Ads API, FedRAMP, …).
        // "codex" alone matches Codex API, Codex Web, and Codex in ChatGPT Desktop.
        DefaultComponents: ["codex", "responses", "login", "vs code extension"]);

    public static IReadOnlyList<StatusSource> All { get; } = [Claude, OpenAi];

    /// <summary>The source with this id, or null — settings normalization depends on the null.</summary>
    public static StatusSource? ById(string? id)
        => id is null ? null : All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~StatusSourceRegistryTests`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/StatusSource.cs tests/ClaudeUsageTray.Tests/StatusSourceRegistryTests.cs
git commit -m "feat: add a curated status source registry" -m "Refs #17"
```

---

### Task 2: Parse components and stamp the source

**Files:**
- Modify: `src/ClaudeUsageTray/Core/PlatformStatus.cs`
- Modify: `src/ClaudeUsageTray/Core/PlatformStatusApi.cs`
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs` (the one `PlatformStatusApi.FetchAsync` call, ~line 205)
- Test: `tests/ClaudeUsageTray.Tests/PlatformStatusApiTests.cs`
- Test: `tests/ClaudeUsageTray.Tests/UsagePopupWidthTests.cs` (its `Degraded` helper constructs `PlatformStatus`)

**Interfaces:**
- Consumes: `StatusSource`, `StatusSourceRegistry` from Task 1.
- Produces: `PlatformComponent(string Name, string Status)`; `PlatformStatus(string SourceId, DateTimeOffset FetchedAt, string Indicator, string Description, IReadOnlyList<PlatformIncident> Incidents, IReadOnlyList<PlatformComponent> Components)`; `PlatformStatusApi.FetchAsync(HttpClient http, StatusSource source, DateTimeOffset now, CancellationToken ct)`.

- [ ] **Step 1: Write the failing test**

In `tests/ClaudeUsageTray.Tests/PlatformStatusApiTests.cs`, replace the `Fetch` helper so it takes a source, and add the OpenAI payload plus the new assertions. Keep every existing test; they now call `Fetch(respond)` which defaults to a Claude-shaped source pointed at a dummy URL.

```csharp
    /// <summary>A throwaway source, so tests exercise the same code path the app uses without a
    /// second endpoint-override overload existing purely for them.</summary>
    private static StatusSource TestSource(string id = "claude", bool raisesBadge = true)
        => new(id, id, $"https://status.{id}.test/api/v2/summary.json", $"https://status.{id}.test",
            $"status.{id}.test", raisesBadge, []);

    private const string OpenAiShape = """
        {
          "page": { "id": "01JMDK9XYNY6RXSED6SDWW50WY", "name": "OpenAI" },
          "status": { "indicator": "minor", "description": "Partial System Outage" },
          "components": [
            { "id": "1", "name": "Responses", "status": "degraded_performance" },
            { "id": "2", "name": "Sora", "status": "operational" },
            { "id": "3", "name": "Codex API", "status": "partial_outage" }
          ]
        }
        """;

    private static (PlatformStatus? Status, FakeHandler Handler) Fetch(
        Func<HttpRequestMessage, HttpResponseMessage> respond, StatusSource? source = null)
    {
        var handler = new FakeHandler(respond);
        using var http = new HttpClient(handler);
        var result = PlatformStatusApi.FetchAsync(http, source ?? TestSource(), Now, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (result, handler);
    }

    [Fact]
    public void OpenAiShape_HasNoIncidentsKey_AndStillParses()
    {
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, OpenAiShape), TestSource("openai", raisesBadge: false));
        Assert.NotNull(s);
        Assert.Equal("openai", s!.SourceId);
        Assert.Equal("minor", s.Indicator);
        Assert.True(s.Degraded);
        Assert.Empty(s.Incidents);
    }

    [Fact]
    public void Components_KeepOnlyTheNonOperational()
    {
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, OpenAiShape), TestSource("openai", raisesBadge: false));
        Assert.Equal(["Responses", "Codex API"], s!.Components.Select(c => c.Name));
        Assert.Equal(["degraded_performance", "partial_outage"], s.Components.Select(c => c.Status));
    }

    [Fact]
    public void Components_SurviveMalformedEntries()
    {
        const string ragged = """
            {
              "status": { "indicator": "minor", "description": "Partial System Outage" },
              "components": [
                "not-an-object",
                { "name": "Nameless status missing" },
                { "id": "3", "status": "major_outage" },
                { "id": "4", "name": "Codex API", "status": "major_outage" }
              ]
            }
            """;
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, ragged));
        Assert.Equal(["Codex API"], s!.Components.Select(c => c.Name));
    }

    [Fact]
    public void Request_GoesToTheSourcesOwnUrl()
    {
        var (_, h) = Fetch(_ => Json(HttpStatusCode.OK, OpenAiShape), TestSource("openai", raisesBadge: false));
        Assert.Equal("https://status.openai.test/api/v2/summary.json", h.LastRequest!.RequestUri!.ToString());
    }
```

Also update the existing `Request_HasExactUrlAndUserAgent_NoAuthorization` test to expect `https://status.claude.test/api/v2/summary.json` (the test source's URL), keeping its User-Agent and `Assert.Null(h.LastRequest.Headers.Authorization)` assertions unchanged.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~PlatformStatusApiTests`
Expected: FAIL — build error, `FetchAsync` has no overload taking a `StatusSource`, and `PlatformStatus` has no `SourceId`/`Components`.

- [ ] **Step 3: Write minimal implementation**

In `src/ClaudeUsageTray/Core/PlatformStatus.cs`, add the component record and extend the status record:

```csharp
/// <summary>One component the page reports as anything other than operational. Name and status are
/// the page's own words; a renamed or new component needs no app update.</summary>
public sealed record PlatformComponent(string Name, string Status);

/// <summary>One successful fetch of the page's overall state. Degraded is the page's own banner —
/// any indicator other than "none" — so an indicator StatusPage has not invented yet still fails
/// towards visible rather than invisible. Components carries only the non-operational entries, so
/// no caller can accidentally render a wall of healthy components.</summary>
public sealed record PlatformStatus(
    string SourceId, DateTimeOffset FetchedAt, string Indicator, string Description,
    IReadOnlyList<PlatformIncident> Incidents, IReadOnlyList<PlatformComponent> Components)
{
    public bool Degraded => Indicator != "none";
}
```

In `src/ClaudeUsageTray/Core/PlatformStatusApi.cs`: delete the `EndpointUrl` constant and both existing `FetchAsync` overloads' signatures, replacing them with one that takes the source. Keep the whole `try`/`catch` body as it is, and add the components read.

```csharp
    public static async Task<PlatformStatus?> FetchAsync(HttpClient http, StatusSource source,
        DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.SummaryUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            // ... unchanged: send, status check, parse, root/status/indicator guards, description ...

            // incidents: unchanged block

            return new PlatformStatus(source.Id, now, indicator.GetString()!.Trim(), description,
                incidents, ReadComponents(doc.RootElement));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
            or OperationCanceledException or IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Non-operational components only. status.openai.com sends no incidents at all, so this
    /// array is the only thing that can say what a disruption affects; an entry without a name or a
    /// status is dropped rather than shown as a blank row.</summary>
    private static IReadOnlyList<PlatformComponent> ReadComponents(JsonElement root)
    {
        if (!root.TryGetProperty("components", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<PlatformComponent>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (NonEmptyString(entry, "name") is not { } name) continue;
            if (NonEmptyString(entry, "status") is not { } status) continue;
            if (status == "operational") continue;
            result.Add(new PlatformComponent(name, status));
        }
        return result;
    }
```

Note the existing `ReadComponents(JsonElement entry)` helper for *incident* components stays; rename it to `ReadIncidentComponents` so the two do not collide.

In `src/ClaudeUsageTray/Tray/TrayApp.cs`, change the call inside `StartStatusFetch` to pass the source (Task 7 replaces this wholesale; this keeps the build green now):

```csharp
            var result = await PlatformStatusApi.FetchAsync(Http, StatusSourceRegistry.Claude,
                DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
```

In `tests/ClaudeUsageTray.Tests/UsagePopupWidthTests.cs`, update the `Degraded` helper to the new positional record:

```csharp
    private static PlatformStatus Degraded(string description, params PlatformIncident[] incidents)
        => new("claude", DateTimeOffset.UtcNow, "major", description, incidents, []);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test`
Expected: PASS, whole suite. Any remaining failure is a `PlatformStatus` construction site that needs the two new arguments.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "feat: parse status page components and stamp the source id" -m "Refs #17"
```

---

### Task 3: Component filter

**Files:**
- Create: `src/ClaudeUsageTray/Core/ComponentFilter.cs`
- Test: `tests/ClaudeUsageTray.Tests/ComponentFilterTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ComponentFilter.Normalize(IEnumerable<string>? tokens)`, `.Parse(string? text)`, `.Format(IReadOnlyList<string> filter)`, `.Matches(string name, IReadOnlyList<string> filter)` — all returning `IReadOnlyList<string>` / `string` / `bool` respectively.

- [ ] **Step 1: Write the failing test**

Create `tests/ClaudeUsageTray.Tests/ComponentFilterTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class ComponentFilterTests
{
    [Fact]
    public void Parse_TrimsDropsEmptiesAndDeduplicates()
    {
        Assert.Equal(["codex", "login"], ComponentFilter.Parse("  codex , , login ,CODEX,   "));
    }

    [Fact]
    public void Parse_OfNothing_IsTheEmptyFilter()
    {
        Assert.Empty(ComponentFilter.Parse(null));
        Assert.Empty(ComponentFilter.Parse("   ,  , "));
    }

    [Fact]
    public void EmptyFilter_MatchesEverything()
    {
        Assert.True(ComponentFilter.Matches("Sora", []));
    }

    [Fact]
    public void Matches_IsCaseInsensitiveSubstring()
    {
        IReadOnlyList<string> filter = ["codex"];
        Assert.True(ComponentFilter.Matches("Codex API", filter));
        Assert.True(ComponentFilter.Matches("Codex Web", filter));
        Assert.True(ComponentFilter.Matches("Codex in ChatGPT Desktop", filter));
        Assert.False(ComponentFilter.Matches("Sora", filter));
    }

    [Fact]
    public void Matches_AnyToken_IsEnough()
    {
        Assert.True(ComponentFilter.Matches("Login", ["codex", "login"]));
    }

    [Fact]
    public void Format_RoundTripsThroughParse()
    {
        var filter = ComponentFilter.Parse("codex, responses");
        Assert.Equal("codex, responses", ComponentFilter.Format(filter));
        Assert.Equal(filter, ComponentFilter.Parse(ComponentFilter.Format(filter)));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~ComponentFilterTests`
Expected: FAIL — build error, `ComponentFilter` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/ClaudeUsageTray/Core/ComponentFilter.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>The watch filter: which of a page's components the user cares about. Substring rather
/// than exact names, because these pages rename components (`Codex in ChatGPT Desktop` appeared in
/// 2026-03) and one token should keep matching. Ordinal throughout — these are US-English product
/// names, and a Turkish locale must not change what "login" matches.</summary>
public static class ComponentFilter
{
    /// <summary>Trimmed, non-empty, de-duplicated tokens. A list that normalizes to nothing is the
    /// empty filter, which watches everything.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? tokens)
    {
        if (tokens is null) return [];
        var result = new List<string>();
        foreach (var raw in tokens)
        {
            var token = raw?.Trim();
            if (string.IsNullOrEmpty(token)) continue;
            if (!result.Contains(token, StringComparer.OrdinalIgnoreCase)) result.Add(token);
        }
        return result;
    }

    /// <summary>The dialog's comma-separated text as a filter.</summary>
    public static IReadOnlyList<string> Parse(string? text) => Normalize(text?.Split(','));

    /// <summary>The filter as dialog text.</summary>
    public static string Format(IReadOnlyList<string> filter) => string.Join(", ", filter);

    /// <summary>Whether this component name is watched. An empty filter watches everything.</summary>
    public static bool Matches(string name, IReadOnlyList<string> filter)
    {
        if (filter.Count == 0) return true;
        foreach (var token in filter)
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~ComponentFilterTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/ComponentFilter.cs tests/ClaudeUsageTray.Tests/ComponentFilterTests.cs
git commit -m "feat: add the component watch filter" -m "Refs #17"
```

---

### Task 4: Relevance rules

**Files:**
- Create: `src/ClaudeUsageTray/Core/StatusDetail.cs`
- Test: `tests/ClaudeUsageTray.Tests/StatusDetailRelevanceTests.cs`

**Interfaces:**
- Consumes: `PlatformStatus`, `PlatformIncident`, `PlatformComponent` (Task 2); `ComponentFilter` (Task 3).
- Produces: `StatusDetail.IsRelevant(PlatformStatus status, IReadOnlyList<string> filter)` → `bool`; `StatusEmphasis { Muted, Warning, Alert }`; `StatusDetail.Emphasis(PlatformStatus? status, bool relevant)` → `StatusEmphasis`; internal-to-file helpers `IncidentWatched`, `Identifies`.

- [ ] **Step 1: Write the failing test**

Create `tests/ClaudeUsageTray.Tests/StatusDetailRelevanceTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class StatusDetailRelevanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<string> Codex = ["codex"];

    private static PlatformStatus Status(string indicator, PlatformComponent[]? components = null,
        PlatformIncident[]? incidents = null)
        => new("openai", Now, indicator, indicator == "none" ? "All Systems Operational" : "Partial System Outage",
            incidents ?? [], components ?? []);

    private static PlatformIncident Incident(params string[] components)
        => new("Something broke", "investigating", "minor", null, Now, components);

    [Fact]
    public void Operational_IsNeverRelevant()
        => Assert.False(StatusDetail.IsRelevant(Status("none"), Codex));

    [Fact]
    public void EmptyFilter_MakesAnyDegradationRelevant()
        => Assert.True(StatusDetail.IsRelevant(Status("minor", [new("Sora", "major_outage")]), []));

    [Fact]
    public void WatchedComponentAffected_IsRelevant()
        => Assert.True(StatusDetail.IsRelevant(Status("minor", [new("Codex API", "partial_outage")]), Codex));

    [Fact]
    public void OnlyUnwatchedComponentsAffected_IsNotRelevant()
        => Assert.False(StatusDetail.IsRelevant(Status("minor", [new("Sora", "major_outage")]), Codex));

    /// <summary>The failure this rule exists for: a page can report a disruption while every
    /// component still reads operational and no incident names one. Hiding that behind a
    /// noise-reduction filter would hide a real outage.</summary>
    [Fact]
    public void DegradedButUnclassifiable_IsAlwaysRelevant()
        => Assert.True(StatusDetail.IsRelevant(Status("major"), Codex));

    [Fact]
    public void IncidentNamingNoComponents_CountsAsWatched()
        => Assert.True(StatusDetail.IsRelevant(Status("minor", [new("Sora", "major_outage")], [Incident()]), Codex));

    [Fact]
    public void IncidentNamingOnlyUnwatchedComponents_IsNotRelevant()
        => Assert.False(StatusDetail.IsRelevant(
            Status("minor", [new("Sora", "major_outage")], [Incident("Sora")]), Codex));

    [Fact]
    public void IncidentNamingAWatchedComponent_IsRelevant()
        => Assert.True(StatusDetail.IsRelevant(Status("minor", incidents: [Incident("Codex API")]), Codex));

    [Theory]
    [InlineData("minor", StatusEmphasis.Warning)]
    [InlineData("major", StatusEmphasis.Alert)]
    [InlineData("critical", StatusEmphasis.Alert)]
    [InlineData("something_new", StatusEmphasis.Alert)]
    public void RelevantDegradation_IsEmphasised(string indicator, StatusEmphasis expected)
        => Assert.Equal(expected, StatusDetail.Emphasis(Status(indicator), relevant: true));

    [Fact]
    public void IrrelevantDegradation_AndHealth_AndNoData_AreMuted()
    {
        Assert.Equal(StatusEmphasis.Muted, StatusDetail.Emphasis(Status("minor"), relevant: false));
        Assert.Equal(StatusEmphasis.Muted, StatusDetail.Emphasis(Status("none"), relevant: false));
        Assert.Equal(StatusEmphasis.Muted, StatusDetail.Emphasis(null, relevant: false));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~StatusDetailRelevanceTests`
Expected: FAIL — build error, `StatusDetail` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/ClaudeUsageTray/Core/StatusDetail.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>How loudly a source's banner should be drawn. Muted covers health, no data, and a
/// disruption the user filtered out; the split between Warning and Alert follows the page's own
/// indicator, with anything unrecognised treated as the louder of the two.</summary>
public enum StatusEmphasis { Muted, Warning, Alert }

/// <summary>Every display decision about platform status, as pure functions: what counts as
/// relevant under a watch filter, which rows to draw, what the header and tooltip say. The WinForms
/// layer turns the results into controls and decides nothing.</summary>
public static partial class StatusDetail
{
    /// <summary>Whether a disruption is worth colour and tooltip space under this filter. True when
    /// the page is degraded and either the filter is empty, a watched component or incident is
    /// affected, or nothing in the payload identifies what is affected at all — that last case is
    /// the same "fail towards visible" rule the unknown-indicator handling uses.</summary>
    public static bool IsRelevant(PlatformStatus status, IReadOnlyList<string> filter)
    {
        if (!status.Degraded) return false;
        if (filter.Count == 0) return true;
        foreach (var incident in status.Incidents)
            if (IncidentWatched(incident, filter)) return true;
        foreach (var component in status.Components)
            if (ComponentFilter.Matches(component.Name, filter)) return true;
        return !Identifies(status);
    }

    public static StatusEmphasis Emphasis(PlatformStatus? status, bool relevant)
    {
        if (status is not { Degraded: true } || !relevant) return StatusEmphasis.Muted;
        return status.Indicator == "minor" ? StatusEmphasis.Warning : StatusEmphasis.Alert;
    }

    /// <summary>An incident naming no components counts as watched: "unclassified" must not mean
    /// "invisible".</summary>
    private static bool IncidentWatched(PlatformIncident incident, IReadOnlyList<string> filter)
    {
        if (incident.Components.Count == 0) return true;
        foreach (var name in incident.Components)
            if (ComponentFilter.Matches(name, filter)) return true;
        return false;
    }

    /// <summary>Whether the payload says anything at all about what is affected.</summary>
    private static bool Identifies(PlatformStatus status)
        => status.Components.Count > 0 || status.Incidents.Any(i => i.Components.Count > 0);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~StatusDetailRelevanceTests`
Expected: PASS, 13 test cases.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/StatusDetail.cs tests/ClaudeUsageTray.Tests/StatusDetailRelevanceTests.cs
git commit -m "feat: add status relevance rules for the watch filter" -m "Refs #17"
```

---

### Task 5: Rows and headers

**Files:**
- Create: `src/ClaudeUsageTray/Core/StatusDetailRows.cs` (the second half of the `partial class StatusDetail`)
- Test: `tests/ClaudeUsageTray.Tests/StatusDetailRowsTests.cs`

**Interfaces:**
- Consumes: everything from Task 4, plus `RelativeTime.Ago` (existing, `Core/RelativeTime.cs`).
- Produces: `StatusRow(string Text, string? Link)`; `StatusDetail.Rows(PlatformStatus status, IReadOnlyList<string> filter, DateTimeOffset now, int max)` → `IReadOnlyList<StatusRow>`; `StatusDetail.HiddenCount(PlatformStatus status, IReadOnlyList<string> filter, int max)` → `int`; `StatusDetail.Header(StatusSource source, PlatformStatus? status, bool relevant, bool stale)` → `string`.

- [ ] **Step 1: Write the failing test**

Create `tests/ClaudeUsageTray.Tests/StatusDetailRowsTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class StatusDetailRowsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<string> Codex = ["codex"];
    private static readonly StatusSource OpenAi = StatusSourceRegistry.OpenAi;

    private static PlatformStatus Status(PlatformComponent[]? components = null,
        PlatformIncident[]? incidents = null, string indicator = "minor",
        string description = "Partial System Outage")
        => new("openai", Now, indicator, description, incidents ?? [], components ?? []);

    private static PlatformIncident Incident(string name, string? shortlink = null,
        params string[] components)
        => new(name, "investigating", "minor", shortlink, Now.AddMinutes(-30), components);

    [Fact]
    public void Components_BecomeRowsWithUnfoldedStatus()
    {
        var rows = StatusDetail.Rows(
            Status([new("Codex API", "degraded_performance")]), Codex, Now, max: 3);
        Assert.Equal("Codex API — Degraded performance", Assert.Single(rows).Text);
        Assert.Null(rows[0].Link);
    }

    [Fact]
    public void ComponentRows_AreFilteredToWatchedOnes()
    {
        var rows = StatusDetail.Rows(
            Status([new("Sora", "major_outage"), new("Codex Web", "partial_outage")]), Codex, Now, max: 3);
        Assert.Equal(["Codex Web — Partial outage"], rows.Select(r => r.Text));
    }

    [Fact]
    public void Incidents_WinOverComponents_AndKeepTheirShortlink()
    {
        var rows = StatusDetail.Rows(
            Status([new("Codex API", "major_outage")], [Incident("Elevated errors", "https://stspg.io/x")]),
            Codex, Now, max: 3);
        var row = Assert.Single(rows);
        Assert.StartsWith("Elevated errors — Investigating · minor", row.Text);
        Assert.Contains("updated ", row.Text);
        Assert.Equal("https://stspg.io/x", row.Link);
    }

    /// <summary>Precedence runs after filtering: incidents that are all filtered out must not
    /// suppress the component rows that do match, or the source shows degraded with zero rows.</summary>
    [Fact]
    public void IncidentsAllFilteredOut_FallsBackToWatchedComponents()
    {
        var rows = StatusDetail.Rows(
            Status([new("Codex API", "major_outage")], [Incident("Sora is down", null, "Sora")]),
            Codex, Now, max: 3);
        Assert.Equal(["Codex API — Major outage"], rows.Select(r => r.Text));
    }

    [Fact]
    public void Rows_AreCappedAndTheRestCounted()
    {
        var status = Status([
            new("Codex API", "major_outage"), new("Codex Web", "major_outage"),
            new("Codex in ChatGPT Desktop", "major_outage"), new("Codex CLI", "major_outage")]);
        Assert.Equal(3, StatusDetail.Rows(status, Codex, Now, max: 3).Count);
        Assert.Equal(1, StatusDetail.HiddenCount(status, Codex, max: 3));
    }

    [Fact]
    public void NothingIdentified_YieldsNoRows()
    {
        Assert.Empty(StatusDetail.Rows(Status(), Codex, Now, max: 3));
        Assert.Equal(0, StatusDetail.HiddenCount(Status(), Codex, max: 3));
    }

    [Fact]
    public void Header_UsesTheSourceNameAndThePagesOwnWords()
        => Assert.Equal("OpenAI status: Partial System Outage",
            StatusDetail.Header(OpenAi, Status(), relevant: true, stale: false));

    [Fact]
    public void Header_FallsBackToTheIndicatorWhenTheBannerIsEmpty()
        => Assert.Equal("OpenAI status: minor",
            StatusDetail.Header(OpenAi, Status(description: ""), relevant: true, stale: false));

    [Fact]
    public void Header_ExplainsAFilteredOutDisruption()
        => Assert.Equal("OpenAI status: Partial System Outage · outside your watched components",
            StatusDetail.Header(OpenAi, Status(), relevant: false, stale: false));

    [Fact]
    public void Header_MarksStaleAndNoData()
    {
        Assert.Equal("OpenAI status: Partial System Outage · stale",
            StatusDetail.Header(OpenAi, Status(), relevant: true, stale: true));
        Assert.Equal("OpenAI status: unavailable",
            StatusDetail.Header(OpenAi, null, relevant: false, stale: false));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~StatusDetailRowsTests`
Expected: FAIL — build error, `StatusDetail.Rows`, `HiddenCount`, `Header` and `StatusRow` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/ClaudeUsageTray/Core/StatusDetailRows.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>One detail line under a status header. Link is the incident shortlink when the page
/// offers one; component rows have none, because status.openai.com publishes no per-incident link.</summary>
public sealed record StatusRow(string Text, string? Link);

public static partial class StatusDetail
{
    /// <summary>The detail rows for one source, at most <paramref name="max"/> of them. Selection
    /// happens after filtering, so incidents that are all filtered out fall through to the watched
    /// components rather than leaving a degraded source with an empty section.</summary>
    public static IReadOnlyList<StatusRow> Rows(PlatformStatus status, IReadOnlyList<string> filter,
        DateTimeOffset now, int max)
        => Selected(status, filter, now).Take(max).ToList();

    /// <summary>How many rows the cap left out, for the "+N more" line.</summary>
    public static int HiddenCount(PlatformStatus status, IReadOnlyList<string> filter, int max)
        => Math.Max(0, Selected(status, filter, DateTimeOffset.MinValue).Count - max);

    /// <summary>The header line: the source's name and the page's own banner text, verbatim. A
    /// disruption the filter excluded says so, so an empty section never looks like a parse
    /// failure.</summary>
    public static string Header(StatusSource source, PlatformStatus? status, bool relevant, bool stale)
    {
        if (status is null) return $"{source.DisplayName} status: unavailable";
        var text = string.IsNullOrWhiteSpace(status.Description) ? status.Indicator : status.Description;
        var header = $"{source.DisplayName} status: {text}";
        if (status.Degraded && !relevant) header += " · outside your watched components";
        if (stale) header += " · stale";
        return header;
    }

    private static List<StatusRow> Selected(PlatformStatus status, IReadOnlyList<string> filter,
        DateTimeOffset now)
    {
        var incidents = status.Incidents.Where(i => IncidentWatched(i, filter)).ToList();
        if (incidents.Count > 0)
            return incidents.Select(i => new StatusRow(DescribeIncident(i, now), i.Shortlink)).ToList();

        return status.Components
            .Where(c => ComponentFilter.Matches(c.Name, filter))
            .Select(c => new StatusRow($"{c.Name} — {Unfold(c.Status)}", null))
            .ToList();
    }

    /// <summary>One incident row: name, status with initial capital, impact when not none/missing,
    /// affected components, and age.</summary>
    private static string DescribeIncident(PlatformIncident incident, DateTimeOffset now)
    {
        var parts = new List<string> { $"{incident.Name} — {Capitalize(incident.Status)}" };
        if (!string.IsNullOrEmpty(incident.Impact) && incident.Impact != "none")
            parts.Add(incident.Impact);
        if (incident.Components.Count > 0)
            parts.Add(string.Join(", ", incident.Components));
        if (incident.UpdatedAt is { } updated)
            parts.Add($"updated {RelativeTime.Ago(updated, now)}");
        return string.Join(" · ", parts);
    }

    /// <summary>`degraded_performance` as `Degraded performance` — the page's vocabulary, made
    /// readable, never translated into ours.</summary>
    private static string Unfold(string status) => Capitalize(status.Replace('_', ' '));

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~StatusDetailRowsTests`
Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/StatusDetailRows.cs tests/ClaudeUsageTray.Tests/StatusDetailRowsTests.cs
git commit -m "feat: select status detail rows in Core" -m "Refs #17"
```

---

### Task 6: Tooltip suffixes

**Files:**
- Modify: `src/ClaudeUsageTray/Core/StatusDetailRows.cs`
- Test: `tests/ClaudeUsageTray.Tests/StatusDetailTooltipTests.cs`

**Interfaces:**
- Consumes: `SourceView` (Task 1), `StatusDetail.IsRelevant` (Task 4).
- Produces: `StatusDetail.TooltipSuffix(IReadOnlyList<SourceView> sources, DateTimeOffset now, int stalenessMinutes, int available)` → `string`.

- [ ] **Step 1: Write the failing test**

Create `tests/ClaudeUsageTray.Tests/StatusDetailTooltipTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class StatusDetailTooltipTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static SourceView View(StatusSource source, string indicator, string description,
        int ageMinutes = 0, IReadOnlyList<string>? filter = null)
        => new(source, new PlatformStatus(source.Id, Now.AddMinutes(-ageMinutes), indicator, description, [], []),
            filter ?? []);

    private static string Suffix(int available, params SourceView[] views)
        => StatusDetail.TooltipSuffix(views, Now, stalenessMinutes: 15, available);

    [Fact]
    public void HealthyAndMissingSources_AddNothing()
    {
        Assert.Equal("", Suffix(100,
            View(StatusSourceRegistry.Claude, "none", "All Systems Operational"),
            new SourceView(StatusSourceRegistry.OpenAi, null, [])));
    }

    [Fact]
    public void DegradedSource_NamesItself()
        => Assert.Equal(" · OpenAI: Partial System Outage",
            Suffix(100, View(StatusSourceRegistry.OpenAi, "minor", "Partial System Outage")));

    [Fact]
    public void StaleDegradation_IsMarked()
        => Assert.Equal(" · Claude: Partial outage (stale)",
            Suffix(100, View(StatusSourceRegistry.Claude, "major", "Partial outage", ageMinutes: 60)));

    [Fact]
    public void FilteredOutDegradation_AddsNothing()
        => Assert.Equal("", Suffix(100,
            new SourceView(StatusSourceRegistry.OpenAi,
                new PlatformStatus("openai", Now, "minor", "Partial System Outage", [],
                    [new PlatformComponent("Sora", "major_outage")]),
                ["codex"])));

    [Fact]
    public void BadgeRaisingSource_ComesFirst()
        => Assert.Equal(" · Claude: Major outage · OpenAI: Partial System Outage",
            Suffix(100,
                View(StatusSourceRegistry.OpenAi, "minor", "Partial System Outage"),
                View(StatusSourceRegistry.Claude, "major", "Major outage")));

    /// <summary>Trim order cannot protect the badge-raising suffix — TrimTooltip cuts the finished
    /// string. So the non-badge suffix is dropped whole rather than half-rendered.</summary>
    [Fact]
    public void NonBadgeSuffix_IsDroppedWholeWhenItDoesNotFit()
    {
        var claudeOnly = " · Claude: Major outage";
        Assert.Equal(claudeOnly, Suffix(claudeOnly.Length + 5,
            View(StatusSourceRegistry.Claude, "major", "Major outage"),
            View(StatusSourceRegistry.OpenAi, "minor", "Partial System Outage")));
    }

    [Fact]
    public void BadgeSuffix_IsKeptEvenWhenItAloneOverflows()
    {
        Assert.Equal(" · Claude: Major outage",
            Suffix(0, View(StatusSourceRegistry.Claude, "major", "Major outage")));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~StatusDetailTooltipTests`
Expected: FAIL — build error, `StatusDetail.TooltipSuffix` does not exist.

- [ ] **Step 3: Write minimal implementation**

Append to `src/ClaudeUsageTray/Core/StatusDetailRows.cs`, inside `partial class StatusDetail`:

```csharp
    /// <summary>The tooltip tail naming every relevant disruption, badge-raising source first.
    /// <paramref name="available"/> is how many characters the caller has left before NotifyIcon's
    /// 127-character limit: suffixes for non-badge sources are dropped **whole** when they do not
    /// fit, because a half-cut "· OpenAI: Minor serv…" is worse than none, and the badge-raising
    /// source's suffix is the text that explains the marker on the icon — it is never dropped.</summary>
    public static string TooltipSuffix(IReadOnlyList<SourceView> sources, DateTimeOffset now,
        int stalenessMinutes, int available)
    {
        var ordered = sources
            .Where(v => v.Status is { Degraded: true } s && IsRelevant(s, v.Filter))
            .OrderByDescending(v => v.Source.RaisesBadge)
            .ToList();

        var text = "";
        foreach (var view in ordered)
        {
            var status = view.Status!;
            var words = string.IsNullOrWhiteSpace(status.Description) ? status.Indicator : status.Description;
            var stale = now - status.FetchedAt > TimeSpan.FromMinutes(stalenessMinutes);
            var piece = $" · {view.Source.DisplayName}: {words}{(stale ? " (stale)" : "")}";
            if (!view.Source.RaisesBadge && text.Length + piece.Length > available) continue;
            text += piece;
        }
        return text;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~StatusDetailTooltipTests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/StatusDetailRows.cs tests/ClaudeUsageTray.Tests/StatusDetailTooltipTests.cs
git commit -m "feat: build multi-source tooltip suffixes in Core" -m "Refs #17"
```

---

### Task 7: Status monitor

**Files:**
- Create: `src/ClaudeUsageTray/Core/StatusMonitor.cs`
- Test: `tests/ClaudeUsageTray.Tests/StatusMonitorTests.cs`

**Interfaces:**
- Consumes: `StatusSource`, `SourceView`, `StatusSourceRegistry` (Task 1); `PlatformStatus` (Task 2); `StatusScheduler` (existing, `Core/StatusScheduler.cs`).
- Produces: `StatusMonitor` with `TakeDue(DateTimeOffset now)` → `IReadOnlyList<StatusSource>`; `Accept(string sourceId, PlatformStatus? result, DateTimeOffset now)` → `bool` (false when the result was discarded); `Status(string sourceId)` → `PlatformStatus?`; `Sources()` → `IReadOnlyList<SourceView>`; `BadgeDegraded()` → `bool`; `ApplyEnabled(IReadOnlyList<(StatusSource Source, IReadOnlyList<string> Filter)> enabled)`.

- [ ] **Step 1: Write the failing test**

Create `tests/ClaudeUsageTray.Tests/StatusMonitorTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~StatusMonitorTests`
Expected: FAIL — build error, `StatusMonitor` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/ClaudeUsageTray/Core/StatusMonitor.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>
/// Per-source status state: which page is due for a fetch, what each one last said, and whether the
/// badge should warn. Clock-free and thread-free — every timestamp arrives from the caller — so the
/// whole multi-source policy is unit-testable and TrayApp is left holding only the HTTP call.
///
/// Each source carries its own <see cref="StatusScheduler"/>, which is what makes the isolation
/// invariant structural rather than a promise: one page timing out cannot back off, blank, or delay
/// the other.
/// </summary>
public sealed class StatusMonitor
{
    private sealed class Entry(StatusSource source, IReadOnlyList<string> filter)
    {
        public StatusSource Source { get; } = source;
        public IReadOnlyList<string> Filter { get; set; } = filter;
        public StatusScheduler Scheduler { get; } = new();
        public PlatformStatus? Status { get; set; }
        public bool InFlight { get; set; }
    }

    private readonly List<Entry> _entries = [];

    public StatusMonitor(IReadOnlyList<(StatusSource Source, IReadOnlyList<string> Filter)> enabled)
        => ApplyEnabled(enabled);

    /// <summary>The sources whose gate is open, marked in-flight and charged an attempt. Named for
    /// the mutation: a pure query paired with a separate RecordAttempt is a call that can be
    /// forgotten, and forgetting it means hammering a public endpoint.</summary>
    public IReadOnlyList<StatusSource> TakeDue(DateTimeOffset now)
    {
        var due = new List<StatusSource>();
        foreach (var entry in _entries)
        {
            if (entry.InFlight || !entry.Scheduler.CanFetch(now)) continue;
            entry.InFlight = true;
            entry.Scheduler.RecordAttempt(now);
            due.Add(entry.Source);
        }
        return due;
    }

    /// <summary>Files a completed fetch, or a null for a failed one. Returns false when the result
    /// was discarded: the source was disabled while its fetch was outstanding, or the payload's own
    /// SourceId disagrees with the id it arrived under. Both should be unreachable — which is
    /// exactly why neither may silently file an OpenAI outage under Claude.</summary>
    public bool Accept(string sourceId, PlatformStatus? result, DateTimeOffset now)
    {
        var entry = Find(sourceId);
        if (entry is null) return false;
        if (result is not null && !string.Equals(result.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            return false;

        entry.InFlight = false;
        if (result is null)
        {
            // Keep the last-known-good state: a dead endpoint degrades to stale, never to blank.
            entry.Scheduler.RecordFailure(now);
            return true;
        }
        entry.Scheduler.RecordSuccess();
        entry.Status = result;
        return true;
    }

    public PlatformStatus? Status(string sourceId) => Find(sourceId)?.Status;

    public IReadOnlyList<SourceView> Sources()
        => _entries.Select(e => new SourceView(e.Source, e.Status, e.Filter)).ToList();

    /// <summary>Whether the tray icon should carry the warning marker. Deliberately does not consult
    /// the watch filter for any source: the Claude filter has no dialog control, and a
    /// README-only JSON key must not be able to disarm the tray's main warning.</summary>
    public bool BadgeDegraded()
        => _entries.Any(e => e.Source.RaisesBadge && e.Status is { Degraded: true });

    /// <summary>Replaces the enabled set, keeping the state of sources that stay enabled — toggling
    /// one source must not blank another's banner, and with it the badge, for a poll cycle. Entries
    /// follow registry order; a newly added source is immediately due.</summary>
    public void ApplyEnabled(IReadOnlyList<(StatusSource Source, IReadOnlyList<string> Filter)> enabled)
    {
        var kept = new List<Entry>();
        foreach (var source in StatusSourceRegistry.All)
        {
            var match = enabled.FirstOrDefault(e => e.Source.Id == source.Id);
            if (match.Source is null) continue;
            var existing = Find(source.Id);
            if (existing is not null)
            {
                existing.Filter = match.Filter;
                kept.Add(existing);
            }
            else
            {
                kept.Add(new Entry(match.Source, match.Filter));
            }
        }
        _entries.Clear();
        _entries.AddRange(kept);
    }

    private Entry? Find(string sourceId)
        => _entries.FirstOrDefault(e => string.Equals(e.Source.Id, sourceId, StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~StatusMonitorTests`
Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/StatusMonitor.cs tests/ClaudeUsageTray.Tests/StatusMonitorTests.cs
git commit -m "feat: add a per-source status monitor" -m "Refs #17"
```

---

### Task 8: Settings

**Files:**
- Modify: `src/ClaudeUsageTray/Core/Settings.cs`
- Test: `tests/ClaudeUsageTray.Tests/SettingsTests.cs`

**Interfaces:**
- Consumes: `StatusSourceRegistry`, `StatusSource` (Task 1); `ComponentFilter` (Task 3).
- Produces: `StatusSourceSettings { bool Enabled; List<string>? Components; }`; `Settings.StatusSources` (`Dictionary<string, StatusSourceSettings?>`, non-null values after load); `Settings.EnabledSources()` → `IReadOnlyList<(StatusSource Source, IReadOnlyList<string> Filter)>` in registry order.

- [ ] **Step 1: Write the failing test**

Add to `tests/ClaudeUsageTray.Tests/SettingsTests.cs`:

```csharp
    private static Settings LoadJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, json);
        try { return Settings.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NoStatusSourcesKey_KeepsTodaysBehaviour()
    {
        var s = LoadJson("""{ "displayMode": "both" }""");
        var enabled = s.EnabledSources();
        Assert.Equal(["claude"], enabled.Select(e => e.Source.Id));
        Assert.Empty(enabled[0].Filter);
    }

    [Fact]
    public void EnablingOpenAi_UsesItsDefaultFilterWhenNoneIsGiven()
    {
        var s = LoadJson("""{ "statusSources": { "openai": { "enabled": true } } }""");
        var openAi = s.EnabledSources().Single(e => e.Source.Id == "openai");
        Assert.Equal(StatusSourceRegistry.OpenAi.DefaultComponents, openAi.Filter);
    }

    [Fact]
    public void GivenComponents_AreNormalized()
    {
        var s = LoadJson(
            """{ "statusSources": { "openai": { "enabled": true, "components": [" codex ", "", "CODEX"] } } }""");
        Assert.Equal(["codex"], s.EnabledSources().Single(e => e.Source.Id == "openai").Filter);
    }

    [Fact]
    public void UnknownSourceId_IsDropped()
    {
        var s = LoadJson("""{ "statusSources": { "gemini": { "enabled": true } } }""");
        Assert.Equal(["claude"], s.EnabledSources().Select(e => e.Source.Id));
    }

    /// <summary>Per-entry fallback: a malformed source entry must not reset unrelated settings the
    /// way a whole-file JsonException would.</summary>
    [Fact]
    public void MalformedEntry_ResetsThatEntryAlone()
    {
        var s = LoadJson("""
            {
              "stalenessMinutes": 42,
              "thresholds": { "orange": 30, "red": 70 },
              "statusSources": { "openai": { "enabled": "yes please", "components": 7 } }
            }
            """);
        Assert.Equal(42, s.StalenessMinutes);
        Assert.Equal(30, s.Thresholds.Orange);
        Assert.Equal(70, s.Thresholds.Red);
        Assert.Equal(["claude"], s.EnabledSources().Select(e => e.Source.Id));   // openai back to disabled
    }

    [Fact]
    public void StatusSources_RoundTripThroughSave()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new Settings();
            s.StatusSources["openai"] = new StatusSourceSettings { Enabled = true, Components = ["codex"] };
            s.Save(path);
            var loaded = Settings.Load(path);
            Assert.Equal(["claude", "openai"], loaded.EnabledSources().Select(e => e.Source.Id));
            Assert.Equal(["codex"], loaded.EnabledSources().Single(e => e.Source.Id == "openai").Filter);
        }
        finally { File.Delete(path); }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~SettingsTests`
Expected: FAIL — build error, `StatusSourceSettings` and `Settings.EnabledSources` do not exist.

- [ ] **Step 3: Write minimal implementation**

In `src/ClaudeUsageTray/Core/Settings.cs`, add the entry type, the tolerant converter, the property, the normalization, and the accessor:

```csharp
/// <summary>One source's configuration. Components null means "use the source's default filter";
/// an empty list means watch every component.</summary>
public sealed class StatusSourceSettings
{
    public bool Enabled { get; set; }
    public List<string>? Components { get; set; }
}

/// <summary>Reads the status-source map entry by entry, so one malformed entry cannot throw and take
/// every unrelated setting down with it — Settings.Load catches JsonException and falls back to full
/// defaults, which would otherwise reset thresholds and display mode too. A bad entry arrives as
/// null and NormalizeFields replaces it with the registry default.</summary>
public sealed class TolerantStatusSourcesConverter : JsonConverter<Dictionary<string, StatusSourceSettings?>>
{
    public override Dictionary<string, StatusSourceSettings?> Read(ref Utf8JsonReader reader,
        Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new Dictionary<string, StatusSourceSettings?>(StringComparer.OrdinalIgnoreCase);
        if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return result; }

        using var doc = JsonDocument.ParseValue(ref reader);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            StatusSourceSettings? value = null;
            try { value = property.Value.Deserialize<StatusSourceSettings>(options); }
            catch (JsonException) { /* malformed entry → registry default at normalization */ }
            result[property.Name] = value;
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, StatusSourceSettings?> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, entry) in value)
        {
            if (entry is null) continue;
            writer.WritePropertyName(key);   // source ids are already the lower-case token
            JsonSerializer.Serialize(writer, entry, options);
        }
        writer.WriteEndObject();
    }
}
```

Inside `Settings`, add the property and register the converter:

```csharp
    /// <summary>Which status pages to watch, and which of their components matter. Values are
    /// non-null after Load; the nullable value type exists so the tolerant converter can mark a
    /// malformed entry for NormalizeFields to replace.</summary>
    [JsonConverter(typeof(TolerantStatusSourcesConverter))]
    public Dictionary<string, StatusSourceSettings?> StatusSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
```

Extend `NormalizeFields`:

```csharp
        StatusSources ??= new(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, StatusSourceSettings?>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in StatusSourceRegistry.All)
        {
            // Unknown ids are dropped by rebuilding from the registry; a missing or malformed entry
            // falls back to that source's default, and every other entry survives untouched.
            StatusSources.TryGetValue(source.Id, out var entry);
            sources[source.Id] = entry is null
                ? new StatusSourceSettings
                {
                    Enabled = source.RaisesBadge,           // Claude on, OpenAI off
                    Components = [.. source.DefaultComponents],
                }
                : new StatusSourceSettings
                {
                    Enabled = entry.Enabled,
                    Components = entry.Components is null
                        ? [.. source.DefaultComponents]
                        : [.. ComponentFilter.Normalize(entry.Components)],
                };
        }
        StatusSources = sources;
```

And the accessor:

```csharp
    /// <summary>The enabled sources with their watch filters, in registry order — what StatusMonitor
    /// consumes.</summary>
    public IReadOnlyList<(StatusSource Source, IReadOnlyList<string> Filter)> EnabledSources()
    {
        var result = new List<(StatusSource, IReadOnlyList<string>)>();
        foreach (var source in StatusSourceRegistry.All)
        {
            if (StatusSources.TryGetValue(source.Id, out var entry) && entry is { Enabled: true })
                result.Add((source, entry.Components ?? []));
        }
        return result;
    }
```

Add `using System.Text.Json.Serialization;` if it is not already present (it is — `JsonStringEnumConverter` is used) and `using System.Text.Json;`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter FullyQualifiedName~SettingsTests`
Expected: PASS, including all pre-existing tests.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/Settings.cs tests/ClaudeUsageTray.Tests/SettingsTests.cs
git commit -m "feat: configure status sources and watch filters in settings" -m "Refs #17"
```

---

### Task 9: Wire TrayApp to the monitor

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs` (fields ~28-37, `StartStatusFetch`/`OnStatusFetchCompleted` ~192-236, `Render`/`Apply` ~240-292, `StatusSuffix` ~316-324, popup construction ~350, dispose ~519)

**Interfaces:**
- Consumes: `StatusMonitor`, `StatusDetail`, `Settings.EnabledSources()`, `PlatformStatusApi.FetchAsync(http, source, now, ct)`.
- Produces: nothing new — this task rewires existing behaviour. `UsagePopup` still receives a single `PlatformStatus?` until Task 10.

- [ ] **Step 1: Write the failing test**

There is no test seam for `TrayApp` (it owns WinForms timers); its logic now lives in tested Core types. Verification for this task is the existing suite staying green plus a manual smoke check in Step 4. Skip to Step 2.

- [ ] **Step 2: Replace the status fields**

In `src/ClaudeUsageTray/Tray/TrayApp.cs`, replace the three status fields and their comment block:

```csharp
    // Platform status polling: 60 s steady state (StatusPage's recommended cadence). All per-source
    // budget, backoff, single-flight and last-known-good state lives in the unit-tested
    // StatusMonitor. Fully independent of the usage path: a status failure can never null, clobber,
    // or delay usage data, and one source's failure can never affect the other's.
    private readonly System.Windows.Forms.Timer _statusPoll = new() { Interval = 60_000 };
    private readonly StatusMonitor _statusMonitor;
```

Initialize it in the constructor, next to `_menu = BuildMenu();`:

```csharp
        _statusMonitor = new StatusMonitor(settings.EnabledSources());
```

- [ ] **Step 3: Rewrite the fetch pair**

```csharp
    private void StartStatusFetch()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var source in _statusMonitor.TakeDue(now))
        {
            _log.Write(now, $"status[{source.Id}]: attempt: GET summary.json");
            var captured = source;
            _ = Task.Run(async () =>
            {
                var result = await PlatformStatusApi.FetchAsync(Http, captured, DateTimeOffset.UtcNow,
                    CancellationToken.None).ConfigureAwait(false);
                try { _sync.BeginInvoke((Action)(() => OnStatusFetchCompleted(captured.Id, result))); }
                catch (InvalidOperationException) { /* app shutting down */ }
            });
        }
    }

    private void OnStatusFetchCompleted(string sourceId, PlatformStatus? result)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_statusMonitor.Accept(sourceId, result, now))
        {
            _log.Write(now, $"status[{sourceId}]: discarded: source disabled or id mismatch");
            return;
        }
        if (result is null)
        {
            // Keep the last-known-good state: a dead endpoint degrades to stale, never to blank.
            _log.Write(now, $"status[{sourceId}]: error: no usable response; backing off");
        }
        else if (result.Degraded)
        {
            var what = result.Incidents.Count > 0
                ? string.Join(", ", result.Incidents.Select(i => i.Name))
                : string.Join(", ", result.Components.Select(c => c.Name));
            _log.Write(now, $"status[{sourceId}]: degraded: indicator={result.Indicator} " +
                $"incidents={result.Incidents.Count} components={result.Components.Count}: {what}");
        }
        else
        {
            _log.Write(now, $"status[{sourceId}]: ok: indicator={result.Indicator} ({result.Description})");
        }
        Render();
    }
```

- [ ] **Step 4: Rewrite Render, the tooltip suffix, and the popup call**

In `Render`, replace the `degraded` and `statusStale` locals:

```csharp
        bool degraded = _statusMonitor.BadgeDegraded();
```

Delete the `statusStale` local and drop that parameter from `Apply` and its two call sites — staleness is now decided per source inside `StatusDetail.TooltipSuffix`. Update `Apply`'s signature to `(NotifyIcon icon, char digit, WindowUsage? usage, string label, TimeSpan period, bool clockwise, bool stale, bool degraded, DateTimeOffset now)` and change both `icon.Text` assignments:

```csharp
            icon.Text = WithStatus("No Claude usage data yet — run Claude Code.", now);
            // ...
            icon.Text = WithStatus(BuildTooltip(label, usage, elapsed, stale, now), now);
```

Replace `StatusSuffix` with:

```csharp
    /// <summary>The usage tooltip plus every relevant disruption. Assembled before trimming so the
    /// badge-raising source's suffix cannot be the part that gets cut: TooltipSuffix drops the
    /// non-badge suffixes whole when they do not fit the remaining budget.</summary>
    private string WithStatus(string text, DateTimeOffset now)
        => TrimTooltip(text + StatusDetail.TooltipSuffix(_statusMonitor.Sources(), now,
            _settings.StalenessMinutes, available: 127 - text.Length));
```

At the popup construction site, pass the source views (the `UsagePopup` signature changes in Task 10; make both edits together if the build order bites):

```csharp
        _popup = new UsagePopup(_snapshot, _settings, DateTimeOffset.UtcNow,
            _statusMonitor.Sources(), _lastFetchStatus);
```

Where settings are applied after the dialog saves (search for where `_settings` fields are copied and `ApplyDisplayMode()` is called), add:

```csharp
        _statusMonitor.ApplyEnabled(_settings.EnabledSources());
        StartStatusFetch();   // a newly enabled source is immediately due
```

- [ ] **Step 5: Run the suite and smoke-test**

Run: `dotnet test`
Expected: PASS (Task 10 updates the popup tests; if `UsagePopup` has not been changed yet, do Tasks 9 and 10 in one build).

Then: `dotnet run --project src/ClaudeUsageTray`, open the popup, confirm the Claude banner still renders and the icon still behaves. Check `%APPDATA%\ClaudeUsageTray\fetch.log` for `status[claude]: ok: …`. **Do not** trigger repeated manual refreshes against the usage endpoint while testing.

- [ ] **Step 6: Commit**

```bash
git add src/ClaudeUsageTray/Tray/TrayApp.cs
git commit -m "refactor: move tray status state into StatusMonitor" -m "Refs #17"
```

---

### Task 10: Render every source in the popup

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/UsagePopup.cs` (constructor signature ~9-30, `AddPlatformStatus` ~101-158, delete `StatusText`/`DescribeIncident`/`Capitalize` ~177-192)
- Test: `tests/ClaudeUsageTray.Tests/UsagePopupWidthTests.cs`
- Test: `tests/ClaudeUsageTray.Tests/UsagePopupStatusTests.cs` (create)

**Interfaces:**
- Consumes: `SourceView`, `StatusDetail.Header/Rows/HiddenCount/Emphasis/IsRelevant`, `StatusEmphasis`.
- Produces: `UsagePopup(UsageSnapshot? snapshot, Settings settings, DateTimeOffset now, IReadOnlyList<SourceView>? statusSources = null, string? lastFetchStatus = null)`.

- [ ] **Step 1: Write the failing test**

Create `tests/ClaudeUsageTray.Tests/UsagePopupStatusTests.cs`:

```csharp
using System.Drawing;
using System.Windows.Forms;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Rendered offscreen with CreateControl(), never Show(): UsagePopup.OnDeactivate calls
/// Close(), and with no message loop Show() disposes the form before anything can be inspected.</summary>
public class UsagePopupStatusTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly List<UsagePopup> _open = [];

    public void Dispose() { foreach (var popup in _open) popup.Dispose(); }

    private List<Label> Labels(params SourceView[] sources)
    {
        var popup = new UsagePopup(null, new Settings(), Now, sources, null);
        _open.Add(popup);
        popup.CreateControl();
        return popup.Controls.Cast<Control>()
            .SelectMany(c => c.Controls.Cast<Control>())
            .OfType<Label>()
            .Where(l => l is not LinkLabel)
            .ToList();
    }

    private static SourceView View(StatusSource source, string indicator, string description,
        PlatformComponent[]? components = null, IReadOnlyList<string>? filter = null)
        => new(source, new PlatformStatus(source.Id, Now, indicator, description, [], components ?? []),
            filter ?? []);

    [Fact]
    public void BothSources_RenderClaudeFirst()
    {
        var labels = Labels(
            View(StatusSourceRegistry.Claude, "none", "All Systems Operational"),
            View(StatusSourceRegistry.OpenAi, "none", "All Systems Operational"));
        Assert.Equal("Claude status: All Systems Operational", labels[0].Text);
        Assert.Equal("OpenAI status: All Systems Operational", labels[1].Text);
    }

    [Fact]
    public void RelevantDisruption_IsColouredAndListsWatchedComponents()
    {
        var labels = Labels(View(StatusSourceRegistry.OpenAi, "minor", "Partial System Outage",
            [new("Codex API", "partial_outage"), new("Sora", "major_outage")], ["codex"]));
        Assert.Equal("OpenAI status: Partial System Outage", labels[0].Text);
        Assert.Equal(Color.DarkOrange, labels[0].ForeColor);
        Assert.Equal("Codex API — Partial outage", labels[1].Text);
        Assert.DoesNotContain(labels, l => l.Text.Contains("Sora"));
    }

    [Fact]
    public void FilteredOutDisruption_StaysMutedAndSaysWhy()
    {
        var labels = Labels(View(StatusSourceRegistry.OpenAi, "minor", "Partial System Outage",
            [new("Sora", "major_outage")], ["codex"]));
        Assert.Equal("OpenAI status: Partial System Outage · outside your watched components", labels[0].Text);
        Assert.Equal(SystemColors.GrayText, labels[0].ForeColor);
        Assert.Single(labels);
    }

    [Fact]
    public void UnclassifiableDisruption_IsColouredWithNoRows()
    {
        var labels = Labels(View(StatusSourceRegistry.OpenAi, "major", "Service Disruption",
            components: [], filter: ["codex"]));
        Assert.Equal("OpenAI status: Service Disruption", labels[0].Text);
        Assert.Equal(Color.Firebrick, labels[0].ForeColor);
        Assert.Single(labels);
    }

    [Fact]
    public void NoStatusYet_SaysUnavailable()
    {
        var labels = Labels(new SourceView(StatusSourceRegistry.Claude, null, []));
        Assert.Equal("Claude status: unavailable", labels[0].Text);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~UsagePopupStatusTests`
Expected: FAIL — build error, `UsagePopup` takes a `PlatformStatus?`, not a source list.

- [ ] **Step 3: Write minimal implementation**

In `src/ClaudeUsageTray/Tray/UsagePopup.cs`, change the constructor parameter and the call:

```csharp
    public UsagePopup(UsageSnapshot? snapshot, Settings settings, DateTimeOffset now,
        IReadOnlyList<SourceView>? statusSources = null, string? lastFetchStatus = null)
    {
        // ...
        AddPlatformStatus(layout, statusSources, settings, now);
```

Replace `AddPlatformStatus` and delete the now-unused `StatusText`, `DescribeIncident` and `Capitalize` helpers (they live in `StatusDetail` as of Task 5):

```csharp
    /// <summary>One block per watched source, in registry order. Each banner is the page's own
    /// wording, verbatim — exactly what the user would see on the status page — so two healthy
    /// sources produce two lines rather than one merged sentence neither page wrote. Disruptions sit
    /// above the usage rows and still render in the no-data state.</summary>
    private static void AddPlatformStatus(TableLayoutPanel layout, IReadOnlyList<SourceView>? sources,
        Settings settings, DateTimeOffset now)
    {
        if (sources is null) return;
        foreach (var view in sources)
        {
            var status = view.Status;
            bool stale = status is not null
                && now - status.FetchedAt > TimeSpan.FromMinutes(settings.StalenessMinutes);
            bool relevant = status is not null && StatusDetail.IsRelevant(status, view.Filter);

            layout.Controls.Add(WrappingLabel(
                StatusDetail.Header(view.Source, status, relevant, stale),
                Colour(StatusDetail.Emphasis(status, relevant)),
                new Padding(0, 0, 0, 2)));

            if (status is null || !relevant) continue;

            const int MaxRows = 3;
            foreach (var row in StatusDetail.Rows(status, view.Filter, now, MaxRows))
            {
                layout.Controls.Add(WrappingLabel(row.Text, SystemColors.ControlText, new Padding(0)));
                if (row.Link is not { } link) continue;
                var details = new LinkLabel { Text = "Details", AutoSize = true, Margin = new Padding(0) };
                details.LinkClicked += (_, _) => OpenUrl(link);
                layout.Controls.Add(details);
            }

            int hidden = StatusDetail.HiddenCount(status, view.Filter, MaxRows);
            if (hidden > 0)
            {
                layout.Controls.Add(new Label
                {
                    Text = $"+{hidden} more",
                    AutoSize = true,
                    ForeColor = SystemColors.GrayText,
                    Margin = new Padding(0, 2, 0, 0),
                });
            }

            var page = new LinkLabel
            {
                Text = view.Source.PageLabel, AutoSize = true, Margin = new Padding(0, 2, 0, 4),
            };
            var url = view.Source.PageUrl;
            page.LinkClicked += (_, _) => OpenUrl(url);
            layout.Controls.Add(page);
        }
    }

    /// <summary>DarkOrange for a minor banner; Firebrick for major/critical and for any indicator we
    /// do not recognise, which the Degraded rule already treats as a disruption.</summary>
    private static Color Colour(StatusEmphasis emphasis) => emphasis switch
    {
        StatusEmphasis.Warning => Color.DarkOrange,
        StatusEmphasis.Alert => Color.Firebrick,
        _ => SystemColors.GrayText,
    };
```

Update `tests/ClaudeUsageTray.Tests/UsagePopupWidthTests.cs` to build a `SourceView` from its `Degraded` helper and pass a one-element list into the popup:

```csharp
    private int WidthWith(PlatformStatus? status)
    {
        IReadOnlyList<SourceView>? sources =
            status is null ? null : [new SourceView(StatusSourceRegistry.Claude, status, [])];
        // ... unchanged: construct UsagePopup with `sources`, CreateControl(), return Width
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test`
Expected: PASS, whole suite.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Tray/UsagePopup.cs tests/ClaudeUsageTray.Tests/UsagePopupStatusTests.cs tests/ClaudeUsageTray.Tests/UsagePopupWidthTests.cs
git commit -m "feat: render every watched status source in the popup" -m "Refs #17"
```

---

### Task 11: Settings dialog control

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/SettingsDialog.cs` (field block ~30-33, layout ~112, `LoadFrom` ~293, defaults ~300, wiring ~324, `Draft` ~341-352, `Clone` ~389-397)
- Test: `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`

**Interfaces:**
- Consumes: `Settings.StatusSources`, `StatusSourceSettings`, `ComponentFilter.Parse/Format`, `StatusSourceRegistry`.
- Produces: nothing new; `Draft()` now carries `StatusSources`.

- [ ] **Step 1: Write the failing test**

Add to `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`:

```csharp
    private static CheckBox WatchOpenAi(SettingsDialog d) => Find<CheckBox>(d, "watchOpenAi");
    private static TextBox OpenAiComponents(SettingsDialog d) => Find<TextBox>(d, "openAiComponents");

    private static T Find<T>(Control root, string name) where T : Control
        => root.Controls.Find(name, searchAllChildren: true).OfType<T>().Single();

    [Fact]
    public void OpenAiCheckbox_ReflectsSettings_AndDrivesTheDraft()
    {
        var settings = new Settings();
        settings.StatusSources["openai"] = new StatusSourceSettings { Enabled = true, Components = ["codex"] };
        var dialog = Dialog(settings);

        Assert.True(WatchOpenAi(dialog).Checked);
        Assert.Equal("codex", OpenAiComponents(dialog).Text);

        OpenAiComponents(dialog).Text = "codex, login";
        var draft = dialog.Draft();
        Assert.True(draft.StatusSources["openai"]!.Enabled);
        Assert.Equal(["codex", "login"], draft.StatusSources["openai"]!.Components);
    }

    [Fact]
    public void ComponentsField_IsOnlyEnabledWhileWatching()
    {
        var dialog = Dialog(new Settings());
        Assert.False(WatchOpenAi(dialog).Checked);
        Assert.False(OpenAiComponents(dialog).Enabled);

        WatchOpenAi(dialog).Checked = true;
        Assert.True(OpenAiComponents(dialog).Enabled);
    }

    [Fact]
    public void UncheckedOpenAi_KeepsTheTypedFilterForNextTime()
    {
        var dialog = Dialog(new Settings());
        WatchOpenAi(dialog).Checked = true;
        OpenAiComponents(dialog).Text = "codex";
        WatchOpenAi(dialog).Checked = false;

        var draft = dialog.Draft();
        Assert.False(draft.StatusSources["openai"]!.Enabled);
        Assert.Equal(["codex"], draft.StatusSources["openai"]!.Components);
    }

    /// <summary>The Claude filter is an advanced JSON-only key with no control here; the dialog must
    /// carry it through a save instead of resetting it to the default.</summary>
    [Fact]
    public void ClaudeFilter_SurvivesTheRoundTrip()
    {
        var settings = new Settings();
        settings.StatusSources["claude"] = new StatusSourceSettings { Enabled = true, Components = ["api"] };
        var draft = Dialog(settings).Draft();
        Assert.Equal(["api"], draft.StatusSources["claude"]!.Components);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~SettingsDialogTests`
Expected: FAIL — no control named `watchOpenAi` exists, `Find` throws `InvalidOperationException`.

- [ ] **Step 3: Write minimal implementation**

Add the fields next to `_paceColors`:

```csharp
    private readonly CheckBox _watchOpenAi = new()
        { Name = "watchOpenAi", Text = "Watch OpenAI status", AutoSize = true };
    private readonly TextBox _openAiComponents = new() { Name = "openAiComponents", Width = 240 };
    private readonly Label _openAiComponentsCaption = new()
        { Text = "Components (comma-separated, blank = all)", AutoSize = true };
```

Add them to the layout after `layout.Controls.Add(Indent(_paceColors));`:

```csharp
        layout.Controls.Add(_watchOpenAi);
        layout.Controls.Add(Indent(_openAiComponentsCaption));
        layout.Controls.Add(Indent(_openAiComponents));
```

In `LoadFrom(Settings source)` (~line 293), alongside `_paceColors.Checked = source.PaceColors;`:

```csharp
        var openAi = source.StatusSources.GetValueOrDefault("openai");
        _watchOpenAi.Checked = openAi?.Enabled ?? false;
        _openAiComponents.Text = ComponentFilter.Format(
            openAi?.Components ?? [.. StatusSourceRegistry.OpenAi.DefaultComponents]);
        _openAiComponents.Enabled = _watchOpenAi.Checked;
```

In the defaults reset (~line 300), alongside `_paceColors.Checked = new Settings().PaceColors;`:

```csharp
        _watchOpenAi.Checked = false;
        _openAiComponents.Text = ComponentFilter.Format(StatusSourceRegistry.OpenAi.DefaultComponents);
        _openAiComponents.Enabled = false;
```

In the event wiring (~line 324):

```csharp
        _watchOpenAi.CheckedChanged += (_, _) => _openAiComponents.Enabled = _watchOpenAi.Checked;
```

In `Draft()`, before `return draft;`:

```csharp
        // The typed filter is kept even when unchecked, so turning the source back on does not lose it.
        draft.StatusSources["openai"] = new StatusSourceSettings
        {
            Enabled = _watchOpenAi.Checked,
            Components = [.. ComponentFilter.Parse(_openAiComponents.Text)],
        };
```

In `Clone`, carry the map across — otherwise a save resets the JSON-only Claude filter:

```csharp
        StatusSources = source.StatusSources.ToDictionary(
            e => e.Key,
            e => e.Value is null ? null : new StatusSourceSettings
            {
                Enabled = e.Value.Enabled,
                Components = e.Value.Components is null ? null : [.. e.Value.Components],
            },
            StringComparer.OrdinalIgnoreCase),
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test`
Expected: PASS, whole suite.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Tray/SettingsDialog.cs tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs
git commit -m "feat: add the OpenAI status source to the settings dialog" -m "Refs #17"
```

---

### Task 12: Documentation

**Files:**
- Modify: `README.md` (the settings-keys and status sections)
- Modify: `CLAUDE.md` (the "Data flow" section, point 3)

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Update README.md**

In the settings-keys section, document the new key exactly as it is written and read:

```markdown
### `statusSources`

Which public status pages the tray watches, and which of their components matter.

```json
"statusSources": {
  "claude": { "enabled": true,  "components": [] },
  "openai": { "enabled": false, "components": ["codex", "responses", "login", "vs code extension"] }
}
```

- `enabled` — poll this page. Claude is on by default, OpenAI off; the OpenAI toggle and its
  component list are in **Settings → Watch OpenAI status**.
- `components` — case-insensitive substring match against the page's component names; `"codex"`
  matches `Codex API`, `Codex Web`, and `Codex in ChatGPT Desktop`. An empty list watches every
  component.

A disruption affecting none of your watched components still shows the page's banner, greyed and
marked `· outside your watched components`, and adds nothing to the tooltip. A disruption the page
cannot attribute to any component is always shown in full — a filter narrows noise, it never hides
an outage the page could not classify.

**Only Claude's status can mark the tray icon.** An OpenAI outage appears in the popup and the
tooltip and leaves the badge alone, because it says nothing about your Claude usage headroom. The
`claude` entry accepts a `components` filter too — an advanced, JSON-only setting that narrows the
popup rows and the tooltip but never the badge.
```

- [ ] **Step 2: Update CLAUDE.md**

Replace point 3 of the **Data flow** list:

```markdown
3. **Platform status** — one unauthenticated GET per enabled source every 60 s, gated per source by
   `StatusMonitor` (which owns a `StatusScheduler`, the last-known-good result, and the in-flight
   flag for each). `StatusSourceRegistry` holds the two curated sources; `RaisesBadge` is a field of
   the source, which is why only Claude can mark the tray icon. All display decisions — relevance
   under the watch filter, row selection, headers, tooltip suffixes — are pure functions in
   `StatusDetail`. Kept fully independent of the usage path, and of each other: one source's failure
   must never null, clobber, or delay the other's data or the usage data.
```

- [ ] **Step 3: Verify the docs match the code**

Run: `dotnet test`
Expected: PASS. Then re-read both edits against `Settings.NormalizeFields` and `StatusSourceRegistry` and confirm every key name, default, and behaviour claim is accurate.

- [ ] **Step 4: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: document the OpenAI status source and the watch filter" -m "Closes #17"
```

---

## Verification against the spec's success criteria

After Task 12, walk the spec's success-criteria list against a running build:

- [ ] OpenAI off → one poll per minute (`fetch.log` shows only `status[claude]`), one banner, badge unchanged.
- [ ] OpenAI on → both banners in the popup, Claude first, each verbatim from its page.
- [ ] Watched-component disruption → coloured banner, component rows, tooltip suffix, **no** badge change.
  Simulate by temporarily pointing `StatusSourceRegistry.OpenAi.SummaryUrl` at a local file server; revert afterwards.
- [ ] Unwatched-component disruption → grey banner with `· outside your watched components`, no rows, no tooltip suffix.
- [ ] Unclassifiable disruption → coloured banner, no rows, tooltip suffix present.
- [ ] Claude disruption → badge warns, incidents listed with `Details` links.
- [ ] Point one source at an unreachable host → the other keeps its 60 s cadence and its data (`fetch.log` shows the failing source backing off alone).
- [ ] An existing `settings.json` loads unchanged in behaviour and gains the `statusSources` key on the next save.
