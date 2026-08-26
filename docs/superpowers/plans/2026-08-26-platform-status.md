# Platform Status and Taskbar Outage Indicator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Poll the public Claude status page once a minute; while the page's own banner says anything other than "All Systems Operational", carry a warning badge on every visible tray icon (including the neutral no-data icon), name the disruption in the tooltip, and show a status/incident block at the top of the popup.

**Architecture:** Three new pure Core units: the `PlatformStatus`/`PlatformIncident` records, `PlatformStatusApi` (one unauthenticated `GET` of `summary.json`, tolerant parsing, null on anything unusable — mirrors `UsageApiClient`), and `StatusScheduler` (30 s floor + 1/5/15-minute failure backoff, same pure clock-injected shape as `FetchScheduler`). `TrayApp` drives a 60 s timer through the existing single-flight/`BeginInvoke` pattern; `IconRenderer` gains a `warning` flag that draws a white exclamation badge last, over the rim, never dimmed; `UsagePopup` gains a `PlatformStatus?` parameter and renders the status block above the usage rows. Status state shares no state with the usage path.

**Tech Stack:** C# on .NET 10 (`net10.0-windows`), WinForms with `System.Drawing` GDI+ painting, `System.Text.Json`, xUnit for tests.

**Spec:** `docs/superpowers/specs/2026-08-26-platform-status-design.md` — read it before starting.
**Issue:** [#12](https://github.com/wus-technik/win_systray-claude-usage/issues/12)

## Global Constraints

- Build: `dotnet build ClaudeUsageTray.sln`. Test: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`.
- Target framework `net10.0-windows`, `Nullable` enabled, `ImplicitUsings` enabled, `LangVersion` latest — all set in `Directory.Build.props`; do not add per-project overrides.
- Decision logic lives in `src/ClaudeUsageTray/Core/` as pure, unit-tested types. Drawing code in `src/ClaudeUsageTray/Tray/` is not unit-tested beyond `IconRenderer` size/blankness smoke tests. Follow this split.
- `now` is always passed in as a `DateTimeOffset` parameter. Never call `DateTimeOffset.Now`/`UtcNow` inside `Core` types.
- The status path is fully independent of the usage path: no shared mutable state. A status failure can never null, clobber, or delay usage data, icons, or anything else in the app.
- Reuse the existing static `HttpClient` in `TrayApp` (5 s timeout). No new client, no auth headers, no cookies.
- Log lines go to the existing `FetchLog` with the spec's exact `status: …` prefix; the same skip/attempt/outcome discipline as the usage fetch.
- Badge geometry, per the spec, all relative to icon size `s`: diameter `d = 0.45s`, circle centred at `(s − d/2, s − d/2)`, white fill, 1 px rim `Color.FromArgb(30, 30, 30)`, shape-drawn exclamation (stem 0.22d wide × 0.40d tall, dot 0.22d) in the rim colour. The badge is drawn **last** (over the rim) and is **never dimmed**.
- The tooltip is hard-limited by the existing `TrimTooltip` (127 chars); longer degraded lines are expected and handled.
- Comments explain *why*, not *what*, matching the existing density in `UsageApiClient.cs`, `FetchScheduler.cs`, and `UsagePopup.cs`. Do not add narration comments.
- All produced text (code comments, commit messages, docs) in English.
- Do not add a `Co-Authored-By` trailer to commits.

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `src/ClaudeUsageTray/Core/PlatformStatus.cs` | Create | `PlatformIncident` and `PlatformStatus` records; the `Degraded` rule. |
| `src/ClaudeUsageTray/Core/PlatformStatusApi.cs` | Create | Unauthenticated fetch + tolerant parse of `summary.json`; returns null on anything unusable, never throws. |
| `src/ClaudeUsageTray/Properties/AssemblyInfo.cs` | Create | Friend assembly for the status API's test-only endpoint overload. |
| `src/ClaudeUsageTray/Core/StatusScheduler.cs` | Create | 30 s floor + 1/5/15-minute failure backoff. Pure, clock-injected. |
| `tests/ClaudeUsageTray.Tests/PlatformStatusApiTests.cs` | Create | Parse, failure, and request-shape tests against a fake `HttpMessageHandler`. |
| `tests/ClaudeUsageTray.Tests/StatusSchedulerTests.cs` | Create | Floor and backoff tests. |
| `src/ClaudeUsageTray/Tray/IconRenderer.cs` | Modify | `warning` parameter on `Render`/`RenderNeutral`, plumbed through `Draw`; badge drawing. |
| `tests/ClaudeUsageTray.Tests/IconRendererTests.cs` | Modify | Warning-badge smoke rows (size + not-blank). |
| `docs/icon-preview.html` | Modify | True-size warning-badge variants for 1:1 verification. |
| `src/ClaudeUsageTray/Tray/UsagePopup.cs` | Modify | Status block at the top of the popup, all states. |
| `src/ClaudeUsageTray/Tray/TrayApp.cs` | Modify | Timer, single-flight, completion handler, tooltip suffix, menu item, dispose. |
| `README.md` | Modify | Feature row, data-source bullet, design-doc table entry. |
| `src/ClaudeUsageTray/ClaudeUsageTray.csproj` | Modify | Version `0.7.0`. |

Six tasks. Tasks 1–2 deliver the tested pure logic; Task 3 the badge (with its 1:1 verification); Task 4 the popup block; Task 5 the TrayApp glue that makes the feature live; Task 6 the ride-along docs and version bump. The split keeps every commit reviewable on its own: a reviewer could reject the badge while accepting the parsing, and the wiring is separable from both.

---

### Task 1: `PlatformStatus` records and `PlatformStatusApi`

**Files:**
- Create: `src/ClaudeUsageTray/Core/PlatformStatus.cs`
- Create: `src/ClaudeUsageTray/Core/PlatformStatusApi.cs`
- Create: `src/ClaudeUsageTray/Properties/AssemblyInfo.cs`
- Test: `tests/ClaudeUsageTray.Tests/PlatformStatusApiTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `public sealed record ClaudeUsageTray.Core.PlatformIncident(string Name, string Status, string? Impact, string? Shortlink, DateTimeOffset? UpdatedAt, IReadOnlyList<string> Components)`
  - `public sealed record ClaudeUsageTray.Core.PlatformStatus(DateTimeOffset FetchedAt, string Indicator, string Description, IReadOnlyList<PlatformIncident> Incidents)` with `public bool Degraded => Indicator != "none"`
  - `public static Task<PlatformStatus?> ClaudeUsageTray.Core.PlatformStatusApi.FetchAsync(HttpClient http, DateTimeOffset now, CancellationToken ct)` — null on anything unusable, never throws.
  - `internal static Task<PlatformStatus?> ClaudeUsageTray.Core.PlatformStatusApi.FetchAsync(HttpClient http, DateTimeOffset now, CancellationToken ct, string endpointUrl)` — test-only endpoint override; production calls stay pinned to `EndpointUrl`.
  Tasks 4 and 5 consume the records; Task 5 consumes `FetchAsync`.

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/PlatformStatusApiTests.cs`:

```csharp
using System.IO;
using System.Net;
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class PlatformStatusApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Captures the outgoing request and returns a canned response.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private const string AllOperational = """
        {
          "page": { "id": "tymt9n04zgry", "name": "Claude" },
          "status": { "indicator": "none", "description": "All Systems Operational" },
          "components": [ { "id": "rwppv331jlwc", "name": "claude.ai", "status": "operational" } ],
          "incidents": [],
          "scheduled_maintenances": []
        }
        """;

    private static (PlatformStatus? Status, FakeHandler Handler) Fetch(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        using var http = new HttpClient(handler);
        var result = PlatformStatusApi.FetchAsync(http, Now, CancellationToken.None).GetAwaiter().GetResult();
        return (result, handler);
    }

    [Fact]
    public void AllOperational_Parses_AndIsNotDegraded()
    {
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, AllOperational));
        Assert.NotNull(s);
        Assert.Equal(Now, s!.FetchedAt);
        Assert.Equal("none", s.Indicator);
        Assert.Equal("All Systems Operational", s.Description);
        Assert.Empty(s.Incidents);
        Assert.False(s.Degraded);
    }

    [Fact]
    public void Request_HasExactUrlAndUserAgent_NoAuthorization()
    {
        var (_, h) = Fetch(_ => Json(HttpStatusCode.OK, AllOperational));
        Assert.Equal("https://status.claude.com/api/v2/summary.json", h.LastRequest!.RequestUri!.ToString());
        Assert.StartsWith("ClaudeUsageTray/", h.LastRequest.Headers.UserAgent.ToString());
        Assert.Null(h.LastRequest.Headers.Authorization);
    }

    [Fact]
    public void TestOnlyEndpointOverride_UsesProvidedUrl()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, AllOperational));
        using var http = new HttpClient(handler);
        var result = PlatformStatusApi.FetchAsync(http, Now, CancellationToken.None,
            "http://localhost:8080/summary.json").GetAwaiter().GetResult();
        Assert.NotNull(result);
        Assert.Equal("http://localhost:8080/summary.json", handler.LastRequest!.RequestUri!.ToString());
    }


    [Fact]
    public void Degraded_ParsesIncidents_WithAndWithoutComponents()
    {
        const string body = """
            {
              "status": { "indicator": "major", "description": "Major outage" },
              "incidents": [
                { "name": "Elevated error rates on claude.ai", "status": "investigating",
                  "impact": "major", "shortlink": "https://sta.us/abcd",
                  "updated_at": "2026-08-26T11:56:00Z",
                  "components": [ { "name": "claude.ai" } ] },
                { "name": "Slow API responses", "status": "monitoring" }
              ]
            }
            """;
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, body));
        Assert.NotNull(s);
        Assert.True(s!.Degraded);
        Assert.Equal("Major outage", s.Description);

        var first = s.Incidents[0];
        Assert.Equal("Elevated error rates on claude.ai", first.Name);
        Assert.Equal("investigating", first.Status);
        Assert.Equal("major", first.Impact);
        Assert.Equal("https://sta.us/abcd", first.Shortlink);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 11, 56, 0, TimeSpan.Zero), first.UpdatedAt);
        Assert.Equal(new[] { "claude.ai" }, first.Components);

        var second = s.Incidents[1];
        Assert.Equal("Slow API responses", second.Name);
        Assert.Equal("monitoring", second.Status);
        Assert.Null(second.Impact);
        Assert.Null(second.Shortlink);
        Assert.Null(second.UpdatedAt);
        Assert.Empty(second.Components);
    }

    [Fact]
    public void Incident_MissingName_IsSkipped_OthersKept()
    {
        const string body = """
            { "status": { "indicator": "minor", "description": "Minor outage" },
              "incidents": [
                { "status": "monitoring", "impact": "minor" },
                { "name": "Slow responses", "status": "monitoring" } ] }
            """;
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, body));
        var incident = Assert.Single(s!.Incidents);
        Assert.Equal("Slow responses", incident.Name);
    }

    [Fact]
    public void Incident_MissingStatus_FallsBackToUnknown()
    {
        const string body = """
            { "status": { "indicator": "minor", "description": "Minor outage" },
              "incidents": [ { "name": "Something is off" } ] }
            """;
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, body));
        Assert.Equal("unknown", Assert.Single(s!.Incidents).Status);
    }

    [Fact]
    public void Incident_MalformedUpdatedAt_IsKeptWithNullTimestamp()
    {
        const string body = """
            { "status": { "indicator": "minor", "description": "Minor outage" },
              "incidents": [ { "name": "Something is off", "status": "monitoring",
                               "updated_at": "yesterday" } ] }
            """;
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK, body));
        var incident = Assert.Single(s!.Incidents);
        Assert.Equal("Something is off", incident.Name);
        Assert.Null(incident.UpdatedAt);
    }

    [Fact]
    public void MissingStatusObject_ReturnsNull()
        => Assert.Null(Fetch(_ => Json(HttpStatusCode.OK, """{ "incidents": [] }""")).Status);

    [Fact]
    public void MissingIndicator_ReturnsNull()
        => Assert.Null(Fetch(_ => Json(HttpStatusCode.OK,
            """{ "status": { "description": "All Systems Operational" } }""")).Status);

    [Theory]
    [InlineData("[1]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public void NonObjectRoot_ReturnsNull(string body)
        => Assert.Null(Fetch(_ => Json(HttpStatusCode.OK, body)).Status);

    [Fact]
    public void MalformedBody_ReturnsNull()
        => Assert.Null(Fetch(_ => Json(HttpStatusCode.OK, "{ not json")).Status);

    [Fact]
    public void UnknownIndicator_IsDegraded()
    {
        var (s, _) = Fetch(_ => Json(HttpStatusCode.OK,
            """{ "status": { "indicator": "weird-new-value", "description": "Whatever" } }"""));
        Assert.NotNull(s);
        Assert.True(s!.Degraded);
        Assert.Equal("weird-new-value", s.Indicator);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void NonSuccess_ReturnsNull(HttpStatusCode status)
        => Assert.Null(Fetch(_ => Json(status, AllOperational)).Status);

    [Fact]
    public void Timeout_ReturnsNull_NeverThrows()
    {
        // HttpClient surfaces a timeout as a TaskCanceledException.
        var handler = new FakeHandler(_ => throw new TaskCanceledException());
        using var http = new HttpClient(handler);
        var r = PlatformStatusApi.FetchAsync(http, Now, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Null(r);
    }

    [Fact]
    public void NetworkException_ReturnsNull_NeverThrows()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("boom"));
        using var http = new HttpClient(handler);
        var r = PlatformStatusApi.FetchAsync(http, Now, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Null(r);
    }

    [Fact]
    public void IOException_ReturnsNull_NeverThrows()
    {
        var handler = new FakeHandler(_ => throw new IOException("stream died"));
        using var http = new HttpClient(handler);
        var r = PlatformStatusApi.FetchAsync(http, Now, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Null(r);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj --filter PlatformStatusApi`
Expected: FAIL at compile time — `error CS0103: The name 'PlatformStatusApi' does not exist in the current context` (or `CS0246`). A compile failure is the correct red state here.

- [ ] **Step 3: Write the records**

Create `src/ClaudeUsageTray/Core/PlatformStatus.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>One currently unresolved incident from the Claude status page. Status is
/// investigating/identified/monitoring, or "unknown" when the page omits it; Impact is
/// none/minor/major/severe/critical, or null when the page omits it. Component names are
/// the page's own, shown as-is.</summary>
public sealed record PlatformIncident(
    string Name, string Status, string? Impact, string? Shortlink,
    DateTimeOffset? UpdatedAt, IReadOnlyList<string> Components);

/// <summary>One successful fetch of the page's overall state. Degraded is the page's own
/// banner — any indicator other than "none" — so an indicator StatusPage has not invented
/// yet still fails towards visible rather than invisible.</summary>
public sealed record PlatformStatus(
    DateTimeOffset FetchedAt, string Indicator, string Description,
    IReadOnlyList<PlatformIncident> Incidents)
{
    public bool Degraded => Indicator != "none";
}
```

- [ ] **Step 4: Add test-only friend access**

Create `src/ClaudeUsageTray/Properties/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ClaudeUsageTray.Tests")]
```

- [ ] **Step 5: Write the client**

Create `src/ClaudeUsageTray/Core/PlatformStatusApi.cs`:

```csharp
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Read-only client for the public Claude status page (StatusPage v2). One
/// unauthenticated GET per refresh, no token, no cookies. Never throws; returns null on
/// timeout, network error, non-2xx, non-object root, or a missing/invalid status.indicator —
/// the caller then keeps its last-known-good state and backs off.</summary>
public static class PlatformStatusApi
{
    public const string EndpointUrl = "https://status.claude.com/api/v2/summary.json";

    private static readonly string UserAgent =
        $"ClaudeUsageTray/{typeof(PlatformStatusApi).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    public static async Task<PlatformStatus?> FetchAsync(HttpClient http, DateTimeOffset now, CancellationToken ct)
        => await FetchAsync(http, now, ct, EndpointUrl).ConfigureAwait(false);

    internal static async Task<PlatformStatus?> FetchAsync(HttpClient http, DateTimeOffset now,
        CancellationToken ct, string endpointUrl)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpointUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            if (!doc.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object)
                return null;
            if (!status.TryGetProperty("indicator", out var indicator) || indicator.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(indicator.GetString()))
                return null;

            // The banner text is the page's own wording and is shown verbatim; an empty banner
            // falls back to the indicator name at display time, not here.
            var description = status.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? ""
                : "";

            var incidents = new List<PlatformIncident>();
            if (doc.RootElement.TryGetProperty("incidents", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    // An incident with no name has nothing to show; the rest of the page survives.
                    if (NonEmptyString(entry, "name") is not { } name) continue;
                    incidents.Add(new PlatformIncident(
                        Name: name,
                        Status: NonEmptyString(entry, "status") ?? "unknown",
                        Impact: NonEmptyString(entry, "impact"),
                        Shortlink: NonEmptyString(entry, "shortlink"),
                        UpdatedAt: ReadTimestamp(entry, "updated_at"),
                        Components: ReadComponents(entry)));
                }
            }

            return new PlatformStatus(now, indicator.GetString()!.Trim(), description, incidents);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
            or OperationCanceledException or IOException or JsonException)
        {
            return null;
        }
    }

    private static string? NonEmptyString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>ISO-8601 timestamp normalised to UTC; null when absent or unparseable — a bad
    /// timestamp must not drop the incident that carries it.</summary>
    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static IReadOnlyList<string> ReadComponents(JsonElement entry)
    {
        if (!entry.TryGetProperty("components", out var comps) || comps.ValueKind != JsonValueKind.Array)
            return [];
        var names = new List<string>();
        foreach (var c in comps.EnumerateArray())
            if (c.ValueKind == JsonValueKind.Object && NonEmptyString(c, "name") is { } name)
                names.Add(name);
        return names;
    }
}
```

`NonEmptyString` and `ReadTimestamp` are deliberately local rather than reusing `UsageJson`'s: the status payload is a different schema owned by a different service, and `PlatformStatusApi` stays self-contained the way `UsageApiClient` is.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj --filter PlatformStatusApi`
Expected: PASS, 19 test cases (14 facts + 3 non-object-root cases + 2 non-success cases).

- [ ] **Step 7: Run the full suite**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 8: Commit**

```bash
git add src/ClaudeUsageTray/Core/PlatformStatus.cs src/ClaudeUsageTray/Core/PlatformStatusApi.cs src/ClaudeUsageTray/Properties/AssemblyInfo.cs tests/ClaudeUsageTray.Tests/PlatformStatusApiTests.cs
git commit -m "feat: parse Claude platform status from the public status page"
```

---

### Task 2: `StatusScheduler`

**Files:**
- Create: `src/ClaudeUsageTray/Core/StatusScheduler.cs`
- Test: `tests/ClaudeUsageTray.Tests/StatusSchedulerTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `public sealed class ClaudeUsageTray.Core.StatusScheduler` with `public bool CanFetch(DateTimeOffset now)`, `public void RecordAttempt(DateTimeOffset now)`, `public void RecordSuccess()`, `public void RecordFailure(DateTimeOffset now)`. Task 5 owns and drives one instance.

`FetchScheduler` is deliberately **not** reused or subclassed: its rolling-hour cap (20/h) is tuned to the Anthropic per-token budget and would block a 60/h status poll, and its 429/`Retry-After` bookkeeping is irrelevant to a public endpoint with no per-client budget.

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/StatusSchedulerTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class StatusSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshScheduler_AllowsFetch() => Assert.True(new StatusScheduler().CanFetch(T0));

    [Fact]
    public void Floor_BlocksFor30Seconds_ThenAllows()
    {
        var s = new StatusScheduler();
        s.RecordAttempt(T0);
        Assert.False(s.CanFetch(T0.AddSeconds(29)));
        Assert.True(s.CanFetch(T0.AddSeconds(30)));
    }

    [Fact]
    public void FailureBackoff_Escalates1_5_15_AndCaps()
    {
        var s = new StatusScheduler();
        s.RecordFailure(T0);
        Assert.False(s.CanFetch(T0.AddSeconds(59)));
        Assert.True(s.CanFetch(T0.AddMinutes(1)));
        s.RecordFailure(T0.AddMinutes(1));
        Assert.False(s.CanFetch(T0.AddMinutes(5)));
        Assert.True(s.CanFetch(T0.AddMinutes(6)));            // 1 + 5
        s.RecordFailure(T0.AddMinutes(6));
        Assert.True(s.CanFetch(T0.AddMinutes(21)));           // 6 + 15
        s.RecordFailure(T0.AddMinutes(21));
        Assert.False(s.CanFetch(T0.AddMinutes(35)));          // still 15 (capped)
        Assert.True(s.CanFetch(T0.AddMinutes(36)));
    }

    [Fact]
    public void Success_ResetsFailureStreak()
    {
        var s = new StatusScheduler();
        s.RecordFailure(T0);
        s.RecordFailure(T0.AddMinutes(1));
        s.RecordSuccess();
        s.RecordFailure(T0.AddMinutes(30));
        Assert.True(s.CanFetch(T0.AddMinutes(31)));           // back to 1 min, not 15
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj --filter StatusScheduler`
Expected: FAIL at compile time — `error CS0246: The type or namespace name 'StatusScheduler' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/ClaudeUsageTray/Core/StatusScheduler.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>
/// Budget gate for status-page polls: a 30 s floor between attempts (so a manual "Refresh
/// now" cannot spam the public endpoint) and a 1/5/15-minute network-failure backoff, capped
/// at 15. Pure state machine driven by caller-supplied timestamps — no clocks, no threads,
/// fully unit-testable. Deliberately not FetchScheduler: its rolling-hour cap is tuned to the
/// Anthropic per-token budget and would block a 60/h poll.
/// </summary>
public sealed class StatusScheduler
{
    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(30);

    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;
    private int _failureStreak;

    public bool CanFetch(DateTimeOffset now) => now >= _notBefore;

    public void RecordAttempt(DateTimeOffset now) => _notBefore = now + Floor;

    public void RecordSuccess() => _failureStreak = 0;

    public void RecordFailure(DateTimeOffset now)
    {
        _failureStreak = Math.Min(_failureStreak + 1, 3);
        var minutes = _failureStreak switch { 1 => 1, 2 => 5, _ => 15 };
        _notBefore = now + TimeSpan.FromMinutes(minutes);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj --filter StatusScheduler`
Expected: PASS, 4 tests.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/ClaudeUsageTray/Core/StatusScheduler.cs tests/ClaudeUsageTray.Tests/StatusSchedulerTests.cs
git commit -m "feat: add status poll scheduler with 30s floor and 1/5/15min backoff"
```

---

### Task 3: Warning badge on the tray icons

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/IconRenderer.cs`
- Modify: `tests/ClaudeUsageTray.Tests/IconRendererTests.cs`
- Modify: `docs/icon-preview.html`

**Interfaces:**
- Consumes: nothing from earlier tasks (pure drawing change).
- Produces:
  - `public static Icon IconRenderer.Render(char digit, int percent, Severity severity, bool clockwise, bool dimmed, int size, bool warning = false)`
  - `public static Icon IconRenderer.RenderNeutral(int size, bool warning = false)`
  Existing call sites compile unchanged thanks to the defaulted parameter; Task 5 passes `warning` explicitly.

There is no unit test that asserts *what* the badge looks like — drawing in `Tray/` is deliberately untested across this codebase, and the smoke tests keep their existing convention (requested size, not blank). The real verification is the true-size 1:1 check below, per the standing lesson that magnified previews flatter small marks.

- [ ] **Step 1: Add the `warning` parameter and the badge drawing**

In `IconRenderer.cs`, replace the `Render` method (currently lines 18–33) with:

```csharp
    /// <summary>
    /// Filled-badge icon: translucent severity-tinted disc, solid pie wedge from 12 o'clock
    /// for usage (clamped 0–100), 1 px rim, centered digit with a dark halo so it reads on
    /// dark AND light taskbars. clockwise=true for the 5h window, false (counter-clockwise)
    /// for 7d. dimmed = stale data. warning = the Claude platform is degraded: a white
    /// exclamation badge in the bottom-right corner, drawn over everything and never dimmed.
    /// </summary>
    public static Icon Render(char digit, int percent, Severity severity, bool clockwise,
        bool dimmed, int size, bool warning = false)
    {
        var color = severity switch
        {
            Severity.Red => Color.FromArgb(224, 68, 68),
            Severity.Orange => Color.FromArgb(232, 150, 40),
            _ => Color.FromArgb(64, 184, 96),
        };
        return Draw(digit.ToString(), Math.Clamp(percent, 0, 100), color, clockwise, dimmed, size, warning);
    }
```

Replace the `RenderNeutral` method (currently lines 35–37) with:

```csharp
    /// <summary>Grey badge with a centered em-dash: the "no usage data yet" state. The warning
    /// badge applies here too — an outage with no usage data must still be visible.</summary>
    public static Icon RenderNeutral(int size, bool warning = false)
        => Draw("—", percent: 0, Color.FromArgb(150, 150, 150), clockwise: true, dimmed: false, size, warning);
```

In the private `Draw` method, change the signature (line 39) to:

```csharp
    private static Icon Draw(string glyph, int percent, Color color, bool clockwise, bool dimmed,
        int size, bool warning)
```

and, inside the `using (var g = Graphics.FromImage(bmp))` block, immediately after the final
`g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);` (line 82), add:

```csharp
            if (warning) DrawWarningBadge(g, size);
```

Finally, add this method after `Draw`:

```csharp
    /// <summary>White disc with a dark rim and a shape-drawn exclamation, inscribed in the
    /// bottom-right corner — the conventional notification position, away from the centred
    /// digit. Drawn last so it rides over the rim; white fill plus dark rim reads on dark AND
    /// light taskbars, the same dual-legibility rule the digit halo was built for. Never
    /// dimmed: dimming encodes stale *usage* data, and the service state is fresh from its own
    /// fetch — a real outage must not fade. The exclamation is shapes, not text: a 5–6 px font
    /// would sit below the legibility floor this project already enforces for the digit, and a
    /// shape-drawn "!" is the only form guaranteed to read at 16 px.</summary>
    private static void DrawWarningBadge(Graphics g, int size)
    {
        float d = size * 0.45f;
        float cx = size - d / 2f;
        float cy = size - d / 2f;

        using (var fill = new SolidBrush(Color.White))
            g.FillEllipse(fill, cx - d / 2f, cy - d / 2f, d, d);
        using (var rim = new Pen(Color.FromArgb(30, 30, 30), 1f))
            g.DrawEllipse(rim, cx - d / 2f, cy - d / 2f, d, d);

        using var mark = new SolidBrush(Color.FromArgb(30, 30, 30));
        float w = d * 0.22f;
        g.FillRectangle(mark, cx - w / 2f, cy - d * 0.26f, w, d * 0.40f); // stem
        g.FillRectangle(mark, cx - w / 2f, cy + d * 0.24f, w, w);          // dot
    }
```

At the 16 px system size the badge is 7.2 px across: a ~1.6 px-wide stem 2.9 px tall and a ~1.6 px dot — the smallest form that still reads as an exclamation. The stem/dot sit slightly above the circle's centre of mass to leave room for the dot; the 1:1 check in Step 4 is the arbiter if the proportions feel off.

- [ ] **Step 2: Extend the smoke tests**

In `IconRendererTests.cs`, replace the `Render_ProducesIconOfRequestedSize` theory (lines 9–21) with:

```csharp
    [Theory]
    [InlineData('5', 0, Severity.Green, true, false, 16, false)]
    [InlineData('5', 42, Severity.Green, true, false, 16, true)]
    [InlineData('7', 63, Severity.Orange, false, false, 20, true)]
    [InlineData('7', 100, Severity.Red, false, false, 32, true)]
    [InlineData('5', 150, Severity.Red, true, false, 16, false)]  // >100 clamps, must not throw
    [InlineData('5', 42, Severity.Green, true, true, 16, false)]  // dimmed/stale variant
    [InlineData('5', 42, Severity.Green, true, true, 16, true)]   // dimmed base, badge must still be bright
    public void Render_ProducesIconOfRequestedSize(char digit, int percent, Severity sev, bool cw,
        bool dimmed, int size, bool warning)
    {
        using var icon = IconRenderer.Render(digit, percent, sev, cw, dimmed, size, warning);
        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
    }
```

Replace the `RenderNeutral_ProducesIcon` theory (lines 35–42) with:

```csharp
    [Theory]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(16, true)]
    [InlineData(24, true)]
    public void RenderNeutral_ProducesIcon(int size, bool warning)
    {
        using var icon = IconRenderer.RenderNeutral(size, warning);
        Assert.Equal(size, icon.Width);
    }
```

and add this fact after `Render_IsNotBlank`:

```csharp
    [Fact]
    public void Render_WithWarningBadge_IsNotBlank()
    {
        using var icon = IconRenderer.Render('5', 42, Severity.Green, clockwise: true, dimmed: true,
            size: 32, warning: true);
        using var bmp = icon.ToBitmap();
        bool anyPixel = false;
        for (int x = 0; x < bmp.Width && !anyPixel; x++)
            for (int y = 0; y < bmp.Height && !anyPixel; y++)
                if (bmp.GetPixel(x, y).A > 0) anyPixel = true;
        Assert.True(anyPixel, "rendered icon has no visible pixels");
    }
```

- [ ] **Step 3: Add the true-size badge variants to `docs/icon-preview.html`**

The preview's `badgeIcon` function is documented as a pixel-exact mirror of `IconRenderer.cs`; extend it so the same mirror draws the badge, and give it a real 16/20/24 px base size.

a) In `badgeIcon`, replace:

```js
    const size = 16, S = scale || 1, px = size * S;
```

with:

```js
    const size = opts.size || 16, S = scale || 1, px = size * S;
```

b) Replace the digit font line:

```js
    ctx.font = '700 ' + (9.6*S) + 'px "Segoe UI", system-ui, sans-serif';
```

with:

```js
    ctx.font = '700 ' + (px * 0.6) + 'px "Segoe UI", system-ui, sans-serif';
```

(identical at the default 16 base — `9.6 * S` is `0.6 * px`).

c) At the end of `badgeIcon`, after the final `ctx.fillText(letter, cx, cy + 0.5*S);`, add:

```js
    // Warning badge — MUST mirror IconRenderer.DrawWarningBadge exactly.
    if (opts.warning) {
      const d = px * 0.45;
      const bx = px - d / 2, by = px - d / 2;
      ctx.globalAlpha = 1; ctx.fillStyle = '#fff';
      ctx.beginPath(); ctx.arc(bx, by, d / 2, 0, Math.PI * 2); ctx.fill();
      ctx.lineWidth = 1 * S; ctx.strokeStyle = 'rgb(30, 30, 30)';
      ctx.beginPath(); ctx.arc(bx, by, d / 2, 0, Math.PI * 2); ctx.stroke();
      const w = d * 0.22;
      ctx.fillStyle = 'rgb(30, 30, 30)';
      ctx.fillRect(bx - w / 2, by - d * 0.26, w, d * 0.40);  // stem
      ctx.fillRect(bx - w / 2, by + d * 0.24, w, w);         // dot
    }
```

d) In the HTML, add this section after the "Special states" `</section>`:

```html
  <section>
    <h2>Platform status — warning badge</h2>
    <div class="states" id="badgeGrid"></div>
    <p class="caption">White disc, dark rim, shape-drawn exclamation in the bottom-right corner, drawn over the rim and never dimmed. Each true-size row shows every base state plain and badged — inspect at 1:1, per the standing lesson that magnified previews flatter small marks. The ×2 row is context only.</p>
  </section>
```

e) In the `render()` function, after the existing `if (dVal == null) { … }` block closes (so it re-renders on theme change like the rest), add:

```js
    const badgeGrid = document.getElementById('badgeGrid');
    if (badgeGrid) {
      badgeGrid.innerHTML = '';
      const states = [
        { name: 'green',   pct: 13, dim: false },
        { name: 'orange',  pct: 55, dim: false },
        { name: 'red',     pct: 91, dim: false },
        { name: 'dimmed',  pct: 55, dim: true },
        { name: 'neutral', pct: 0,  dim: false, color: C.faint, labelColor: C.faint },
      ];
      [16, 20, 24].forEach(size => {
        const rowLabel = document.createElement('div');
        rowLabel.className = 'label';
        rowLabel.style.gridColumn = '1 / -1';
        rowLabel.textContent = size + ' px, at true size';
        badgeGrid.appendChild(rowLabel);
        states.forEach(st => {
          [false, true].forEach(warn => {
            const d = document.createElement('div'); d.className = 'scell';
            d.appendChild(badgeIcon('5', st.pct, 'cw', 1,
              { size: size, dim: st.dim, color: st.color, labelColor: st.labelColor, warning: warn }));
            const l = document.createElement('div'); l.className = 'label'; l.textContent = st.name; d.appendChild(l);
            const s = document.createElement('div'); s.className = 'sub'; s.textContent = warn ? 'badge' : 'plain'; d.appendChild(s);
            badgeGrid.appendChild(d);
          });
        });
      });
      [16, 20, 24].forEach(size => {
        const d = document.createElement('div'); d.className = 'scell';
        d.appendChild(badgeIcon('5', 55, 'cw', 2, { size: size, warning: true }));
        const l = document.createElement('div'); l.className = 'label'; l.textContent = size + ' px × 2'; d.appendChild(l);
        const s = document.createElement('div'); s.className = 'sub'; s.textContent = 'enlarged'; d.appendChild(s);
        badgeGrid.appendChild(d);
      });
    }
```

- [ ] **Step 4: Build, run the suite, and verify at 1:1**

Run: `dotnet build ClaudeUsageTray.sln` then `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: build succeeds with no warnings introduced; all tests pass, including the extended `IconRendererTests`.

Then open `docs/icon-preview.html` in a browser and inspect the "Platform status — warning badge" section at 100 % zoom (true size):

- At 16 px the badge reads as a white dot with a dark exclamation on **every** base state — green, orange, red, dimmed, neutral — and the dimmed badges are just as bright as their non-dimmed neighbours.
- The badge sits in the bottom-right corner, overlapping the rim, and does not touch or cover the centred digit.
- The exclamation is legible at 16 px without magnification; if it is not, adjust the stem/dot offsets in **both** `DrawWarningBadge` and the HTML mirror (they must stay in lockstep) and re-check.
- The ×2 enlarged row matches the C# proportions; if it does not, the JS mirror has drifted — fix it before committing.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Tray/IconRenderer.cs tests/ClaudeUsageTray.Tests/IconRendererTests.cs docs/icon-preview.html
git commit -m "feat: draw a warning badge on degraded tray icons"
```

---

### Task 4: Status block in the popup

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/UsagePopup.cs`

**Interfaces:**
- Consumes: `PlatformStatus` / `PlatformIncident` from Task 1.
- Produces: `public UsagePopup(UsageSnapshot? snapshot, Settings settings, DateTimeOffset now, PlatformStatus? platformStatus = null, string? lastFetchStatus = null)`. Task 5 updates the single call site.

There is no unit test for this task: `UsagePopup` is drawing code and is deliberately untested across this codebase. Verification is a build plus a visual check of the running app (the popup shows the block in every state, including the no-data state).

- [ ] **Step 1: Add the parameter and the status block**

In `UsagePopup.cs`, replace the constructor signature (line 9) with:

```csharp
    public UsagePopup(UsageSnapshot? snapshot, Settings settings, DateTimeOffset now,
        PlatformStatus? platformStatus = null, string? lastFetchStatus = null)
```

and, immediately after the `TableLayoutPanel` is created (after the `Dock = DockStyle.Fill,` block closes at line 26, before `if (snapshot is null)`), add:

```csharp
        AddPlatformStatus(layout, platformStatus, settings, now);
```

Add these members after `AddWindowRow` (the status block is the first thing drawn, so it is grouped with the row builders):

```csharp
    /// <summary>The page's own banner is the single source of truth — exactly what the user would
    /// see at status.claude.com — so it is shown verbatim. A disruption is the first thing seen,
    /// hence above the usage rows, and it still renders in the no-data state.</summary>
    private static void AddPlatformStatus(TableLayoutPanel layout, PlatformStatus? status,
        Settings settings, DateTimeOffset now)
    {
        bool stale = status is not null
            && now - status.FetchedAt > TimeSpan.FromMinutes(settings.StalenessMinutes);

        string header;
        Color color;
        if (status is null)
        {
            header = "Claude status: unavailable";
            color = SystemColors.GrayText;
        }
        else if (status.Degraded)
        {
            header = $"Claude status: {StatusText(status)}";
            // DarkOrange for a minor banner; Firebrick for major/critical and for any unknown
            // indicator, which the Degraded rule already treats as a disruption.
            color = status.Indicator == "minor" ? Color.DarkOrange : Color.Firebrick;
        }
        else
        {
            header = $"Claude status: {StatusText(status)}";
            color = SystemColors.GrayText;
        }
        if (stale) header += " · stale";

        layout.Controls.Add(new Label
        {
            Text = header,
            AutoSize = true,
            ForeColor = color,
            Margin = new Padding(0, 0, 0, 2),
        });

        if (status is not { Degraded: true }) return;

        var shown = status.Incidents.Take(3).ToList();
        foreach (var incident in shown)
        {
            layout.Controls.Add(new Label
            {
                Text = DescribeIncident(incident, now),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 0),
            });
            if (incident.Shortlink is { } link)
            {
                var details = new LinkLabel { Text = "Details", AutoSize = true, Margin = new Padding(0, 0, 0, 0) };
                details.LinkClicked += (_, _) => OpenUrl(link);
                layout.Controls.Add(details);
            }
        }
        if (status.Incidents.Count > shown.Count)
        {
            layout.Controls.Add(new Label
            {
                Text = $"+{status.Incidents.Count - shown.Count} more",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 2, 0, 0),
            });
        }
        var page = new LinkLabel { Text = "status.claude.com", AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
        page.LinkClicked += (_, _) => OpenUrl("https://status.claude.com");
        layout.Controls.Add(page);
    }

    /// <summary>The page's banner text, verbatim; the indicator name only when the banner is
    /// empty, which the live page does not send but the parser tolerates.</summary>
    private static string StatusText(PlatformStatus status)
        => string.IsNullOrWhiteSpace(status.Description) ? status.Indicator : status.Description;

    /// <summary>One incident row: name, status with initial capital, impact when not
    /// none/missing, affected components, and age.</summary>
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

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* a dead link must never take the popup down */ }
    }
```

and add `using System.Diagnostics;` to the file's top (only new namespace `OpenUrl` needs).

- [ ] **Step 2: Build and run the full suite**

Run: `dotnet build ClaudeUsageTray.sln` then `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: build succeeds (the new parameter is defaulted, so no other call site breaks yet); all tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/ClaudeUsageTray/Tray/UsagePopup.cs
git commit -m "feat: show platform status and incidents in the popup"
```

---

### Task 5: TrayApp integration

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs`

**Interfaces:**
- Consumes: `PlatformStatusApi.FetchAsync` (Task 1), `StatusScheduler` (Task 2), the `warning` parameters on `IconRenderer` (Task 3), the `platformStatus` parameter on `UsagePopup` (Task 4).
- Produces: nothing consumed by later tasks. This is the wiring that makes the feature live.

There is no unit test for the tray glue (timer wiring, single-flight, marshaling) — consistent with prior specs, which treat the usage-fetch glue the same way. Verification is a build, the full suite, and the manual run in Step 7.

- [ ] **Step 1: Add the status fields**

After the `_lastFetchStatus` field (line 28), add:

```csharp
    // Platform status polling: 60 s steady state (StatusPage's recommended cadence); all
    // budget/backoff state lives in the unit-tested StatusScheduler. Single-flight via
    // _statusInFlight (UI thread only). Fully independent of the usage path: a status failure
    // can never null, clobber, or delay usage data.
    private readonly System.Windows.Forms.Timer _statusPoll = new() { Interval = 60_000 };
    private readonly StatusScheduler _statusScheduler = new();
    private bool _statusInFlight;
    private PlatformStatus? _status;
```

- [ ] **Step 2: Start the timer and the initial fetch in the constructor**

After `_poll.Start();` (line 72), add:

```csharp
        _statusPoll.Tick += (_, _) => StartStatusFetch();
        _statusPoll.Start();
```

and after `StartApiFetch();` (line 76, the constructor's last statement), add:

```csharp
        StartStatusFetch();
```

- [ ] **Step 3: Add the fetch methods**

After `OnApiFetchCompleted` (after line 178), add a new section:

```csharp
    // ---- platform status polling ----

    private void StartStatusFetch()
    {
        var now = DateTimeOffset.UtcNow;
        if (_statusInFlight) return; // transient; not worth logging on every timer tick
        if (!_statusScheduler.CanFetch(now)) { _log.Write(now, "status: skip: budget/backoff gate not open yet"); return; }

        _statusInFlight = true;
        _statusScheduler.RecordAttempt(now);
        _log.Write(now, "status: attempt: GET summary.json");
        _ = Task.Run(async () =>
        {
            var result = await PlatformStatusApi.FetchAsync(Http, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
            try { _sync.BeginInvoke((Action)(() => OnStatusFetchCompleted(result))); }
            catch (InvalidOperationException) { /* app shutting down */ }
        });
    }

    private void OnStatusFetchCompleted(PlatformStatus? result)
    {
        _statusInFlight = false;
        var now = DateTimeOffset.UtcNow;
        if (result is null)
        {
            // Keep the last-known-good state: a dead endpoint degrades to stale, never to blank.
            _statusScheduler.RecordFailure(now);
            _log.Write(now, "status: error: no usable response; backing off");
        }
        else
        {
            _statusScheduler.RecordSuccess();
            _status = result;
            if (result.Degraded)
            {
                string names = string.Join(", ", result.Incidents.Select(i => i.Name));
                _log.Write(now, $"status: degraded: indicator={result.Indicator} incidents={result.Incidents.Count}: {names}");
            }
            else
            {
                _log.Write(now, $"status: ok: indicator={result.Indicator} ({result.Description}) incidents={result.Incidents.Count}");
            }
        }
        Render();
    }
```

- [ ] **Step 4: Thread `degraded`/`statusStale` through `Render` and `Apply`**

Replace the `Render` method (lines 182–200) with:

```csharp
    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        bool stale = _snapshot is not null
            && now - _snapshot.FetchedAt > TimeSpan.FromMinutes(_settings.StalenessMinutes);
        bool degraded = _status is { Degraded: true };
        // A real outage must not vanish because *our* network is down: the state keeps being
        // displayed once fetched, only marked stale.
        bool statusStale = _status is not null
            && now - _status.FetchedAt > TimeSpan.FromMinutes(_settings.StalenessMinutes);

        if (_iconFive is not null)
            Apply(_iconFive, '5', _snapshot?.FiveHour, "5h", TimeSpan.FromHours(5),
                clockwise: true, stale, degraded, statusStale, now);
        if (_iconSeven is not null)
            Apply(_iconSeven, '7', _snapshot?.SevenDay, "7d", TimeSpan.FromDays(7),
                clockwise: false, stale, degraded, statusStale, now);

        _updatedItem.Text = _settingsSaveFailed
            ? "Settings could not be saved"
            : _snapshot is null
                ? "No usage data"
                : $"Updated {RelativeTime.Ago(_snapshot.FetchedAt, now)}";

        _restartToUpdateItem.Enabled = UpdateCheck.IsUpdateReady;
    }
```

Replace the `Apply` method (lines 202–225) with:

```csharp
    private void Apply(NotifyIcon icon, char digit, WindowUsage? usage, string label, TimeSpan period,
        bool clockwise, bool stale, bool degraded, bool statusStale, DateTimeOffset now)
    {
        int size = IconRenderer.SystemTrayIconSize();
        var old = icon.Icon;

        if (usage is null)
        {
            icon.Icon = IconRenderer.RenderNeutral(size, warning: degraded);
            icon.Text = TrimTooltip("No Claude usage data yet — run Claude Code." + StatusSuffix(statusStale));
        }
        else
        {
            // No hysteresis: fetches are minutes apart and the ratio only moves fast early in a
            // period, which SeverityRules' dead zone already keeps out of the badge.
            var elapsed = TimeMarker.ElapsedFraction(usage.ResetsAt, period, now);
            var severity = _settings.PaceColors
                ? SeverityRules.ForPace(usage.Percent, elapsed, _settings.Thresholds.Orange, _settings.Thresholds.Red)
                : SeverityRules.For(usage.Percent, _settings.Thresholds.Orange, _settings.Thresholds.Red);
            icon.Icon = IconRenderer.Render(digit, usage.Percent, severity, clockwise,
                dimmed: stale, size, warning: degraded);
            icon.Text = TrimTooltip(BuildTooltip(label, usage, elapsed, stale, now) + StatusSuffix(statusStale));
        }
        old?.Dispose();
    }
```

and add this helper after `TrimTooltip`:

```csharp
    /// <summary>The disruption names itself in the tooltip; normal operation and a never-fetched
    /// status stay unobtrusive.</summary>
    private string StatusSuffix(bool statusStale)
    {
        if (_status is not { Degraded: true } status) return "";
        var text = string.IsNullOrWhiteSpace(status.Description) ? status.Indicator : status.Description;
        return $" · Claude: {text}{(statusStale ? " (stale)" : "")}";
    }
```

- [ ] **Step 5: Wire the menu, the popup, and teardown**

a) In `BuildMenu`, replace the "Refresh now" line (line 302):

```csharp
        menu.Items.Add(new ToolStripMenuItem("Refresh now", null, (_, _) => { Refresh(); StartApiFetch(); StartStatusFetch(); }));
```

b) In `ShowPopup`, replace the popup construction (line 274):

```csharp
        _popup = new UsagePopup(_snapshot, _settings, DateTimeOffset.UtcNow, _status, _lastFetchStatus);
```

c) In `Dispose`, after `_poll.Dispose();` (line 442), add:

```csharp
            _statusPoll.Dispose();
```

- [ ] **Step 6: Build and run the full suite**

Run: `dotnet build ClaudeUsageTray.sln` then `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: build succeeds with no warnings introduced; all tests pass.

- [ ] **Step 7: Manual verification**

Run: `dotnet run --project src/ClaudeUsageTray/ClaudeUsageTray.csproj`

If the tray already has a running instance, `SingleInstance` will refuse to start a second one — close the existing instance first.

Check `%APPDATA%\ClaudeUsageTray\fetch.log` (a new line appears within seconds):

- `… status: attempt: GET summary.json`
- `… status: ok: indicator=none (All Systems Operational) incidents=0` — or, if this machine cannot reach status.claude.com, `… status: error: no usable response; backing off`.
- Wait ~70 s without touching anything: a second `status: attempt` line appears (the 60 s timer).

Then, with the platform operational:

- Left-click the tray icon: the popup's first line is a grey `Claude status: All Systems Operational` — both when usage data exists and in the "No Claude usage data yet" state (verify the latter by temporarily pointing `configPathOverride` at a non-existent path in `%APPDATA%\ClaudeUsageTray\settings.json`).
- No badge on either icon; the tooltip is unchanged.

With the endpoint unreachable (e.g. Wi-Fi off, or DNS for `status.claude.com` blocked):

- The log shows `status: error` lines with ~1 min gaps, then ~5 min, then ~15 min (backoff in action).
- The popup keeps showing the last known state, with ` · stale` appended once `stalenessMinutes` (default 15) has passed. If it was never fetched, it shows grey `Claude status: unavailable`.
- Usage icons, tooltips, and the rest of the popup are completely unaffected.

Right-click → `Refresh now`: if the status gate is open, a `status: attempt` line lands immediately in the log. If a status fetch ran within the last 30 s, it logs `status: skip: budget/backoff gate not open yet`.

**Degraded-state check (optional but recommended):** do not edit `PlatformStatusApi.EndpointUrl`.
It is intentionally constant for production calls. For canned degraded payloads, use the internal
test-only overload added in Task 1 (`FetchAsync(..., endpointUrl)`) from a focused test or temporary
test harness, e.g. against `http://localhost:8080/summary.json` while serving a local file.
Use `docs/icon-preview.html` for the 1:1 badge inspection and a popup smoke run with a seeded
`PlatformStatus` in test code if the visual state needs another pass. Confirm:

- Both tray icons — and the neutral `—` icon when no usage data exists — carry the white exclamation badge, bright even when the usage data is stale-dimmed.
- The tooltip ends in ` · Claude: <description>`.
- The popup shows the coloured header (DarkOrange for `minor`, Firebrick for `major`), up to three incident rows with status/impact/components/age, a `+N more` line when the canned body has more than three incidents, and both the `status.claude.com` and the incident `Details` links open the browser.
- The production `EndpointUrl` remains unchanged in `git diff`; the app clears the badge on the next successful `none` fetch.

- [ ] **Step 8: Commit**

```bash
git add src/ClaudeUsageTray/Tray/TrayApp.cs
git commit -m "feat: poll the Claude status page every 60s (#12)"
```

---

### Task 6: Ride-along docs and version bump

**Files:**
- Modify: `README.md`
- Modify: `src/ClaudeUsageTray/ClaudeUsageTray.csproj`

**Interfaces:**
- Consumes: nothing. Documentation of what Tasks 1–5 delivered.

- [ ] **Step 1: Update the README**

a) In the *What you get* table, after the `🔄 **Live + offline**` row (line 47), add:

```markdown
| 🚨 **Platform status** | Claude's own service banner — while status.claude.com says anything but "All Systems Operational", a white warning badge sits on every tray icon and the popup lists the active incidents |
```

b) In *Where the data comes from*, after bullet 2 (the offline-fallback bullet), add a third bullet:

```markdown
3. **Platform status** — the public status page at status.claude.com, polled once a minute with
   no auth and no token involved. The page's own banner decides the warning badge; incident
   details are the page's own words.
```

c) In the *Design docs* table, after the `2026-07-27-fable-and-credits-design.md` row (line 175), add:

```markdown
| [`specs/2026-08-26-platform-status-design.md`](docs/superpowers/specs/2026-08-26-platform-status-design.md) | Platform status polling and the taskbar outage indicator |
```

- [ ] **Step 2: Bump the version**

In `src/ClaudeUsageTray/ClaudeUsageTray.csproj`, change `<Version>0.6.2</Version>` (line 8) to `<Version>0.7.0</Version>` — a new feature, minor bump.

- [ ] **Step 3: Build and run the full suite one last time**

Run: `dotnet build ClaudeUsageTray.sln` then `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: build succeeds; all tests pass.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: describe platform status in the README"
git add src/ClaudeUsageTray/ClaudeUsageTray.csproj
git commit -m "chore: bump version to 0.7.0"
```

---

## Out of Scope

Do not implement these; they were explicitly excluded by the spec:

- Per-component filtering or user-selectable components to watch — the page banner is the scope.
- Any display of `scheduled_maintenances` — maintenance is not a disruption; the data stays available in the response without being consumed.
- OS-level notifications (toast/WinRT) on outage start.
- A settings toggle or configurable interval for status polling (the 60 s timer and the shared `StalenessMinutes` are fixed).
- Push-based updates; rate-limit/429 handling (the public endpoint has no per-client budget to honour).
- Any change to the usage-fetch path — no shared state, no new behaviour on the usage side.
