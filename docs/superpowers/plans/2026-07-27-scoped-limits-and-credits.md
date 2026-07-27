# Scoped Limits and Credit Usage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show per-model/per-surface weekly usage limits and extra-usage credit spend in the tray's left-click popup, parsed from the `limits[]` and `spend` blocks of the Claude usage payload.

**Architecture:** All parsing lands in `Core/UsageJson.cs` as container-relative readers, so the API client (fields at the response root) and the cache reader (fields under `utilization`) share one implementation. All formatting and row-selection logic lands in Core as pure functions (`CreditFormat`, `PopupRows`) so it is unit-testable without WinForms; `UsagePopup` only draws.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), WinForms, `System.Text.Json`, xUnit 2.9.

**Spec:** `docs/superpowers/specs/2026-07-27-fable-and-credits-design.md`

## Global Constraints

- `TargetFramework` is `net10.0-windows`; `Nullable` and `ImplicitUsings` are enabled; `LangVersion` is `latest` (from `Directory.Build.props`).
- `InvariantGlobalization` is `true` in the app csproj — all number and date formatting must pass `CultureInfo.InvariantCulture` explicitly.
- Parsers never throw for malformed input. They return null / empty and let the caller hide the row.
- Absent or unparseable data hides a row. Never render `0%`, `—`, or a placeholder.
- No money amount, currency, org spend cap, or account-specific model label may be written to `FetchLog`.
- Existing 5-hour and 7-day rows, icon text, and display modes must not regress.
- Build: `dotnet build ClaudeUsageTray.sln`. Test: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`.
- `UsageJson` is `internal`; its readers are tested through `UsageCacheReader` and `UsageApiClient`, not directly.

---

### Task 1: Extract shared percent and reset parsing

Pure refactor. `ReadWindow` currently inlines double-rounding and ISO date parsing; Task 2 needs both for a differently-named percent field (`percent` instead of `utilization`).

**Files:**
- Modify: `src/ClaudeUsageTray/Core/UsageJson.cs`
- Test: `tests/ClaudeUsageTray.Tests/UsageCacheReaderTests.cs` (existing tests are the safety net)

**Interfaces:**
- Consumes: nothing.
- Produces: `internal static int? UsageJson.ReadRoundedPercent(JsonElement parent, string name)` and `internal static DateTimeOffset? UsageJson.ReadResetsAt(JsonElement element)`.

- [ ] **Step 1: Confirm the existing suite is green before touching anything**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: PASS. This is a refactor with no new test; the existing window tests are what prove it behavior-preserving.

- [ ] **Step 2: Replace the body of `UsageJson.cs`**

```csharp
using System.Globalization;
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Shared parsers for the usage payload — used by both the .claude.json cache reader
/// and the usage-API client, which see the same fields at different nesting levels.</summary>
internal static class UsageJson
{
    /// <summary>Reads a percentage as a double and rounds it: the cache stores integers (1, 13)
    /// but the live API returns decimals (11.0, 53.6), and Int32 parsing rejects any fractional
    /// form — silently nulling live windows.</summary>
    internal static int? ReadRoundedPercent(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number
            || !p.TryGetDouble(out var value)) return null;
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    /// <summary>Reads an ISO-8601 "resets_at", normalised to UTC. Null when absent or unparseable.</summary>
    internal static DateTimeOffset? ReadResetsAt(JsonElement element)
    {
        if (element.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    /// <summary>One usage window ({ "utilization": number, "resets_at": iso }).</summary>
    internal static WindowUsage? ReadWindow(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (ReadRoundedPercent(w, "utilization") is not { } percent) return null;
        return new WindowUsage(percent, ReadResetsAt(w));
    }

    /// <summary>A trimmed non-empty string property, or null.</summary>
    internal static string? NonEmptyString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
```

- [ ] **Step 3: Verify the suite is still green**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: PASS, same test count as Step 1. A failure here means the refactor changed behavior.

- [ ] **Step 4: Commit**

```bash
git add src/ClaudeUsageTray/Core/UsageJson.cs
git commit -m "refactor: extract shared percent and resets_at parsing in UsageJson"
```

---

### Task 2: Parse scoped limits from the cache

**Files:**
- Modify: `src/ClaudeUsageTray/Core/UsageSnapshot.cs`
- Modify: `src/ClaudeUsageTray/Core/UsageJson.cs`
- Modify: `src/ClaudeUsageTray/Core/UsageCacheReader.cs`
- Test: `tests/ClaudeUsageTray.Tests/UsageCacheReaderTests.cs`

**Interfaces:**
- Consumes: `UsageJson.ReadRoundedPercent`, `UsageJson.ReadResetsAt`, `UsageJson.NonEmptyString` (Task 1).
- Produces:
  - `public sealed record ScopedLimit(string Label, string? ModelId, int Percent, DateTimeOffset? ResetsAt, bool IsActive)`
  - `UsageSnapshot.ScopedLimits` → `IReadOnlyList<ScopedLimit>`, never null
  - `internal static IReadOnlyList<ScopedLimit> UsageJson.ReadScopedLimits(JsonElement parent)`

- [ ] **Step 1: Write the failing tests**

Append to `UsageCacheReaderTests.cs`. `Wrap` builds a `.claude.json` around a `utilization` body so each fixture stays readable.

```csharp
    private static string Wrap(string utilizationBody) => $$"""
        {
          "cachedUsageUtilization": {
            "fetchedAtMs": 1784815176543,
            "utilization": {{{utilizationBody}}}
          }
        }
        """;

    private static string Limit(string scope, int percent, bool isActive = false,
        string group = "weekly", string resetsAt = "2026-07-27T16:00:00Z") => $$"""
        { "group": "{{group}}", "percent": {{percent}}, "is_active": {{(isActive ? "true" : "false")}},
          "resets_at": "{{resetsAt}}", "scope": {{scope}} }
        """;

    private const string FableScope = """{ "model": { "id": null, "display_name": "Fable" }, "surface": null }""";

    [Fact]
    public void ScopedLimit_ModelScoped_IsParsed()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap($"""
            "limits": [ {Limit(FableScope, 100, isActive: true)} ]
            """)));

        var limit = Assert.Single(s!.ScopedLimits);
        Assert.Equal("Fable", limit.Label);
        Assert.Null(limit.ModelId);
        Assert.Equal(100, limit.Percent);
        Assert.True(limit.IsActive);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 16, 0, 0, TimeSpan.Zero), limit.ResetsAt);
    }

    [Fact]
    public void ScopedLimits_NoLimitsKey_IsEmptyNotNull()
        => Assert.Empty(UsageCacheReader.TryRead(WriteFixture(ValidJson))!.ScopedLimits);

    [Fact]
    public void ScopedLimit_SessionGroup_IsExcluded()
        => Assert.Empty(UsageCacheReader.TryRead(WriteFixture(Wrap($"""
            "limits": [ {Limit(FableScope, 50, group: "session")} ]
            """)))!.ScopedLimits);

    [Fact]
    public void ScopedLimit_NullScope_IsExcluded()
        => Assert.Empty(UsageCacheReader.TryRead(WriteFixture(Wrap($"""
            "limits": [ {Limit("null", 90)} ]
            """)))!.ScopedLimits);

    [Fact]
    public void ScopedLimit_NoLabelDerivable_IsExcluded()
        => Assert.Empty(UsageCacheReader.TryRead(WriteFixture(Wrap($$"""
            "limits": [ {{Limit("""{ "model": { "id": null, "display_name": null }, "surface": null }""", 70)}} ]
            """)))!.ScopedLimits);

    [Fact]
    public void ScopedLimit_FallsBackToModelId_ForLabel()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap($$"""
            "limits": [ {{Limit("""{ "model": { "id": "claude-fable", "display_name": null } }""", 40)}} ]
            """)));

        var limit = Assert.Single(s!.ScopedLimits);
        Assert.Equal("claude-fable", limit.Label);
        Assert.Equal("claude-fable", limit.ModelId);
    }

    [Fact]
    public void ScopedLimit_SurfaceOnly_IsIncludedWithUnderscoresAsSpaces()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap($$"""
            "limits": [ {{Limit("""{ "model": null, "surface": "claude_code" }""", 100, isActive: true)}} ]
            """)));

        Assert.Equal("claude code", Assert.Single(s!.ScopedLimits).Label);
    }

    [Fact]
    public void ScopedLimit_ModelAndSurface_AreBothInTheLabel()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap($$"""
            "limits": [ {{Limit("""{ "model": { "display_name": "Fable" }, "surface": "claude_code" }""", 60)}} ]
            """)));

        Assert.Equal("Fable (claude code)", Assert.Single(s!.ScopedLimits).Label);
    }

    [Fact]
    public void ScopedLimit_MalformedEntry_DoesNotDropItsSiblings()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap($$"""
            "limits": [
              { "group": "weekly", "percent": "not-a-number", "scope": {{FableScope}} },
              "a bare string",
              {{Limit("""{ "model": { "display_name": "Opus" } }""", 30)}}
            ]
            """)));

        Assert.Equal("Opus", Assert.Single(s!.ScopedLimits).Label);
    }

    [Fact]
    public void ScopedLimits_SameModelTwice_DedupesToHigherPercent()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap($$"""
            "limits": [
              {{Limit("""{ "model": { "id": null, "display_name": "Fable" } }""", 40)}},
              {{Limit("""{ "model": { "id": "claude-fable", "display_name": "Fable" } }""", 90, isActive: true)}}
            ]
            """)));

        var limit = Assert.Single(s!.ScopedLimits);
        Assert.Equal(90, limit.Percent);
        Assert.Equal("claude-fable", limit.ModelId);
        Assert.True(limit.IsActive);
    }

    [Fact]
    public void ScopedLimits_LabelsDifferingOnlyByCase_AreDeduped()
        => Assert.Single(UsageCacheReader.TryRead(WriteFixture(Wrap($$"""
            "limits": [
              {{Limit("""{ "model": { "display_name": "Fable" } }""", 10)}},
              {{Limit("""{ "model": { "display_name": "fable" } }""", 20)}}
            ]
            """)))!.ScopedLimits);

    [Fact]
    public void ScopedLimits_ActiveSortsAheadOfHigherPercentInactive()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap($$"""
            "limits": [
              {{Limit("""{ "model": { "display_name": "Inactive" } }""", 100)}},
              {{Limit("""{ "model": { "display_name": "Active" } }""", 70, isActive: true)}}
            ]
            """)));

        Assert.Equal(new[] { "Active", "Inactive" }, s!.ScopedLimits.Select(l => l.Label));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj --filter ScopedLimit`
Expected: FAIL to **compile** — `ScopedLimit` and `UsageSnapshot.ScopedLimits` do not exist yet. A compile failure is the correct red for a missing type; do not proceed until the error names those two symbols.

- [ ] **Step 3: Add the record and the snapshot member**

Replace `src/ClaudeUsageTray/Core/UsageSnapshot.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>Usage for one rolling window. Percent is the raw integer from the cache (may exceed 100).</summary>
public sealed record WindowUsage(int Percent, DateTimeOffset? ResetsAt);

/// <summary>One scoped weekly limit from limits[] — scoped to a model (e.g. Fable), a surface, or
/// both. Label is payload-derived and doubles as the dedup key. IsActive is retained but never
/// filtered on: the observed payload has a real 90% weekly limit flagged is_active:false, so the
/// flag cannot mean "does not apply".</summary>
public sealed record ScopedLimit(
    string Label, string? ModelId, int Percent, DateTimeOffset? ResetsAt, bool IsActive);

/// <summary>The parsed usage payload. Windows are null when absent from the source.</summary>
public sealed record UsageSnapshot(
    DateTimeOffset FetchedAt,
    WindowUsage? FiveHour,
    WindowUsage? SevenDay,
    IReadOnlyList<ScopedLimit>? ScopedLimits = null)
{
    /// <summary>Empty means absent. Never null to consumers, whatever the caller passed.</summary>
    public IReadOnlyList<ScopedLimit> ScopedLimits { get; init; } = ScopedLimits ?? [];
}
```

If the compiler rejects the nullable-parameter / non-nullable-property pairing (records constrain
how closely the two must agree for the generated `Deconstruct`), rename the parameter to
`scopedLimits` and keep a separate `ScopedLimits` property rather than making the public surface
nullable.

- [ ] **Step 4: Add `ReadScopedLimits` to `UsageJson.cs`**

Insert before the closing brace:

```csharp
    /// <summary>Renderable scoped weekly limits from limits[], deduped by label and ordered
    /// active-first then by descending percent. Empty when limits[] is absent or unusable.
    /// Entries needing a label they cannot supply are skipped: a bar captioned with nothing is
    /// worse than a missing bar.</summary>
    internal static IReadOnlyList<ScopedLimit> ReadScopedLimits(JsonElement parent)
    {
        if (!parent.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
            return [];

        // Insertion-ordered dedup: the dictionary merges collisions, the list keeps ties stable.
        var merged = new Dictionary<string, ScopedLimit>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (NonEmptyString(entry, "group") is not "weekly") continue;
            if (!entry.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object)
                continue;

            string? modelName = null, modelId = null;
            if (scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
            {
                modelName = NonEmptyString(model, "display_name");
                modelId = NonEmptyString(model, "id");
            }
            var surface = NonEmptyString(scope, "surface")?.Replace('_', ' ');

            // display_name first: it is the field observed populated, while id is observed null.
            var label = (modelName ?? modelId, surface) switch
            {
                (not null and var m, not null and var s) => $"{m} ({s})",
                (not null and var m, null) => m,
                (null, not null and var s) => s,
                _ => null,
            };
            if (label is null) continue;
            if (ReadRoundedPercent(entry, "percent") is not { } percent) continue;

            var candidate = new ScopedLimit(label, modelId, percent, ReadResetsAt(entry),
                IsActive: entry.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True);

            if (merged.TryGetValue(label, out var existing)) merged[label] = Merge(existing, candidate);
            else { merged[label] = candidate; order.Add(label); }
        }

        return order.Select(k => merged[k])
            .OrderByDescending(l => l.IsActive)
            .ThenByDescending(l => l.Percent)
            .ThenBy(l => l.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Keeps the higher percent so a dedup never makes usage look lower than it is; ties
    /// keep the first entry. ModelId and IsActive survive from either side — losing an active flag
    /// to a dedup would forfeit the row's exemption from the popup's row cap.</summary>
    private static ScopedLimit Merge(ScopedLimit first, ScopedLimit second)
    {
        var winner = second.Percent > first.Percent ? second : first;
        return winner with
        {
            ModelId = winner.ModelId ?? first.ModelId ?? second.ModelId,
            IsActive = first.IsActive || second.IsActive,
        };
    }
```

- [ ] **Step 5: Read the limits in `UsageCacheReader.cs`**

Replace the window-reading block (currently lines 31-37):

```csharp
            var fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(fetched.GetInt64());
            WindowUsage? five = null, seven = null;
            IReadOnlyList<ScopedLimit> scoped = [];
            if (cached.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                five = UsageJson.ReadWindow(u, "five_hour");
                seven = UsageJson.ReadWindow(u, "seven_day");
                scoped = UsageJson.ReadScopedLimits(u);
            }
            return new UsageSnapshot(fetchedAt, five, seven, scoped);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: PASS, including every pre-existing test. Output must be free of warnings.

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Core/UsageSnapshot.cs src/ClaudeUsageTray/Core/UsageJson.cs \
        src/ClaudeUsageTray/Core/UsageCacheReader.cs tests/ClaudeUsageTray.Tests/UsageCacheReaderTests.cs
git commit -m "feat: parse scoped weekly limits from the .claude.json cache"
```

---

### Task 3: Parse scoped limits from the API response

Same reader, different nesting — the API returns these fields at the response root.

**Files:**
- Modify: `src/ClaudeUsageTray/Core/UsageApiClient.cs:47-49`
- Test: `tests/ClaudeUsageTray.Tests/UsageApiClientTests.cs`

**Interfaces:**
- Consumes: `UsageJson.ReadScopedLimits` (Task 2).
- Produces: nothing new.

- [ ] **Step 1: Write the failing test**

Append to `UsageApiClientTests.cs`. Match the existing file's helper for stubbing a response — read the top of the file first and reuse it rather than adding a second stub.

```csharp
    [Fact]
    public async Task ScopedLimits_AreReadFromTheResponseRoot()
    {
        var json = """
            {
              "five_hour": { "utilization": 11.0, "resets_at": "2026-07-27T16:00:00Z" },
              "limits": [
                { "group": "weekly", "percent": 100.0, "is_active": true,
                  "resets_at": "2026-07-27T16:00:00Z",
                  "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null } }
              ]
            }
            """;

        var result = await FetchAsync(HttpStatusCode.OK, json);

        var limit = Assert.Single(result.Snapshot!.ScopedLimits);
        Assert.Equal("Fable", limit.Label);
        Assert.Equal(100, limit.Percent);
        Assert.True(limit.IsActive);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj --filter ScopedLimits_AreReadFromTheResponseRoot`
Expected: FAIL — `Assert.Single` gets an empty collection, because the client does not read `limits` yet. If it instead fails to compile on `FetchAsync`, adapt the call to the helper the file already has.

- [ ] **Step 3: Read the limits in `UsageApiClient.cs`**

Replace the two window reads and the construction:

```csharp
            var five = UsageJson.ReadWindow(doc.RootElement, "five_hour");
            var seven = UsageJson.ReadWindow(doc.RootElement, "seven_day");
            var scoped = UsageJson.ReadScopedLimits(doc.RootElement);
            return new UsageFetchResult(new UsageSnapshot(now, five, seven, scoped), false, false, null);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/UsageApiClient.cs tests/ClaudeUsageTray.Tests/UsageApiClientTests.cs
git commit -m "feat: parse scoped weekly limits from the usage API response"
```

---

### Task 4: Parse credit usage from both sources

`spend` is authoritative money; `extra_usage` is a degraded fallback contributing percent and state only, because its units are unverified. The two are never merged.

**Files:**
- Modify: `src/ClaudeUsageTray/Core/UsageSnapshot.cs`
- Modify: `src/ClaudeUsageTray/Core/UsageJson.cs`
- Modify: `src/ClaudeUsageTray/Core/UsageCacheReader.cs`
- Modify: `src/ClaudeUsageTray/Core/UsageApiClient.cs`
- Test: `tests/ClaudeUsageTray.Tests/UsageCacheReaderTests.cs`

**Interfaces:**
- Consumes: `UsageJson.ReadRoundedPercent`, `UsageJson.NonEmptyString` (Task 1).
- Produces:
  - `public sealed record Money(long AmountMinor, string Currency, int Exponent)`
  - `public sealed record CreditState(bool Enabled, string? DisabledReason, bool LimitReached)`
  - `public sealed record CreditUsage(Money? Used, Money? Limit, int Percent, string? PayloadSeverity, CreditState State)`
  - `UsageSnapshot.Credits` → `CreditUsage?`
  - `internal static CreditUsage? UsageJson.ReadCredits(JsonElement parent)`

- [ ] **Step 1: Write the failing tests**

Append to `UsageCacheReaderTests.cs`:

```csharp
    private const string SpendBlock = """
        "spend": {
          "used":  { "amount_minor": 4001, "currency": "EUR", "exponent": 2 },
          "limit": { "amount_minor": 4000, "currency": "EUR", "exponent": 2 },
          "percent": 100, "severity": "critical", "enabled": true, "disabled_reason": null
        }
        """;

    private const string LegacyBlock = """
        "extra_usage": {
          "is_enabled": true, "monthly_limit": 4000, "used_credits": 4001, "utilization": 73,
          "currency": "EUR", "decimal_places": 2, "disabled_reason": null,
          "spend_limit_reached": false
        }
        """;

    [Fact]
    public void Credits_SpendBlock_IsParsedAsMoney()
    {
        var c = UsageCacheReader.TryRead(WriteFixture(Wrap(SpendBlock)))!.Credits;

        Assert.Equal(4001, c!.Used!.AmountMinor);
        Assert.Equal("EUR", c.Used.Currency);
        Assert.Equal(2, c.Used.Exponent);
        Assert.Equal(4000, c.Limit!.AmountMinor);
        Assert.Equal(100, c.Percent);
        Assert.Equal("critical", c.PayloadSeverity);
        Assert.True(c.State.Enabled);
    }

    [Fact]
    public void Credits_Absent_IsNull()
        => Assert.Null(UsageCacheReader.TryRead(WriteFixture(ValidJson))!.Credits);

    [Fact]
    public void Credits_OverLimit_ReportsLimitReachedFromSpendNotTheLegacyFlag()
    {
        // spend says 4001 of 4000; extra_usage.spend_limit_reached says false. spend must win.
        var c = UsageCacheReader.TryRead(WriteFixture(Wrap($"{SpendBlock}, {LegacyBlock}")))!.Credits;

        Assert.True(c!.State.LimitReached);
        Assert.Equal(100, c.Percent);          // from spend, not the legacy 73
    }

    [Fact]
    public void Credits_ZeroLimit_IsNotReportedAsLimitReached()
    {
        var c = UsageCacheReader.TryRead(WriteFixture(Wrap("""
            "spend": {
              "used":  { "amount_minor": 0, "currency": "EUR", "exponent": 2 },
              "limit": { "amount_minor": 0, "currency": "EUR", "exponent": 2 },
              "percent": 0, "enabled": false, "disabled_reason": "org_spend_cap_reached"
            }
            """)))!.Credits;

        Assert.False(c!.State.LimitReached);
        Assert.False(c.State.Enabled);
        Assert.Equal("org_spend_cap_reached", c.State.DisabledReason);
    }

    [Fact]
    public void Credits_LegacyOnly_YieldsPercentWithoutAmounts()
    {
        var c = UsageCacheReader.TryRead(WriteFixture(Wrap(LegacyBlock)))!.Credits;

        Assert.Null(c!.Used);
        Assert.Null(c.Limit);
        Assert.Equal(73, c.Percent);
        Assert.True(c.State.Enabled);
    }

    [Fact]
    public void Credits_SpendMissingAmounts_FallsBackToLegacy()
    {
        var c = UsageCacheReader.TryRead(WriteFixture(Wrap($$"""
            "spend": { "percent": 99, "enabled": true }, {{LegacyBlock}}
            """)))!.Credits;

        Assert.Null(c!.Used);
        Assert.Equal(73, c.Percent);   // legacy utilization, since spend had no money to trust
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj --filter Credits_`
Expected: FAIL to compile — `Money`, `CreditState`, `CreditUsage`, and `UsageSnapshot.Credits` do not exist.

- [ ] **Step 3: Add the records to `UsageSnapshot.cs`**

Insert after `ScopedLimit` and extend the snapshot:

```csharp
/// <summary>An amount in the payload's own money encoding: minor units + ISO code + exponent.</summary>
public sealed record Money(long AmountMinor, string Currency, int Exponent);

/// <summary>Credit state beyond the percentage. A single bool cannot express the observed case of
/// spend.enabled == true alongside an org spend cap already being reached.</summary>
public sealed record CreditState(bool Enabled, string? DisabledReason, bool LimitReached);

/// <summary>Extra-usage credits. Used/Limit are null when only the legacy extra_usage block is
/// available: its units are unverified (the field is named used_credits, and spend.cap carries
/// separate money and credits slots), so Percent is then the only trustworthy figure.</summary>
public sealed record CreditUsage(
    Money? Used, Money? Limit, int Percent, string? PayloadSeverity, CreditState State);
```

Extend `UsageSnapshot` with a second optional parameter:

```csharp
public sealed record UsageSnapshot(
    DateTimeOffset FetchedAt,
    WindowUsage? FiveHour,
    WindowUsage? SevenDay,
    IReadOnlyList<ScopedLimit>? ScopedLimits = null,
    CreditUsage? Credits = null)
{
    /// <summary>Empty means absent. Never null to consumers, whatever the caller passed.</summary>
    public IReadOnlyList<ScopedLimit> ScopedLimits { get; init; } = ScopedLimits ?? [];
}
```

- [ ] **Step 4: Add the credit readers to `UsageJson.cs`**

```csharp
    /// <summary>Credits from the authoritative money-typed `spend` block, falling back to the
    /// legacy `extra_usage` block. The two are never merged: a payload where they disagree
    /// yields spend's numbers.</summary>
    internal static CreditUsage? ReadCredits(JsonElement parent)
        => ReadSpend(parent) ?? ReadExtraUsage(parent);

    private static CreditUsage? ReadSpend(JsonElement parent)
    {
        if (!parent.TryGetProperty("spend", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        // No trustworthy amounts means no money row — let the legacy block supply a percent instead.
        if (ReadMoney(s, "used") is not { } used || ReadMoney(s, "limit") is not { } limit) return null;

        // LimitReached comes from spend itself. Importing extra_usage.spend_limit_reached here
        // would let the legacy block overrule the authoritative one: the observed payload is
        // 4001 of 4000 at 100% while that flag reads false.
        var limitReached = limit.AmountMinor > 0 && used.AmountMinor >= limit.AmountMinor;

        return new CreditUsage(used, limit, ReadRoundedPercent(s, "percent") ?? 0,
            NonEmptyString(s, "severity"),
            new CreditState(ReadFlag(s, "enabled", whenAbsent: true),
                NonEmptyString(s, "disabled_reason"), limitReached));
    }

    private static CreditUsage? ReadExtraUsage(JsonElement parent)
    {
        if (!parent.TryGetProperty("extra_usage", out var u) || u.ValueKind != JsonValueKind.Object)
            return null;
        if (ReadRoundedPercent(u, "utilization") is not { } percent) return null;

        // used_credits/monthly_limit are deliberately not mapped to money: their units are
        // unverified. A percent with no amount is honest; a possibly-wrong currency amount is not.
        return new CreditUsage(null, null, percent, null,
            new CreditState(ReadFlag(u, "is_enabled", whenAbsent: true),
                NonEmptyString(u, "disabled_reason"), ReadFlag(u, "spend_limit_reached", whenAbsent: false)));
    }

    private static Money? ReadMoney(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var m) || m.ValueKind != JsonValueKind.Object) return null;
        if (!m.TryGetProperty("amount_minor", out var a) || a.ValueKind != JsonValueKind.Number
            || !a.TryGetInt64(out var minor)) return null;

        // Exponent guards against a malformed value scaling the amount into nonsense.
        var exponent = m.TryGetProperty("exponent", out var e) && e.ValueKind == JsonValueKind.Number
            && e.TryGetInt32(out var x) && x is >= 0 and <= 6 ? x : 2;
        return new Money(minor, NonEmptyString(m, "currency") ?? "", exponent);
    }

    private static bool ReadFlag(JsonElement parent, string name, bool whenAbsent)
        => parent.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.ValueKind == JsonValueKind.True
            : whenAbsent;
```

- [ ] **Step 5: Wire both readers**

In `UsageCacheReader.cs`, add `credits` alongside `scoped` inside the `utilization` block and pass it:

```csharp
            WindowUsage? five = null, seven = null;
            IReadOnlyList<ScopedLimit> scoped = [];
            CreditUsage? credits = null;
            if (cached.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                five = UsageJson.ReadWindow(u, "five_hour");
                seven = UsageJson.ReadWindow(u, "seven_day");
                scoped = UsageJson.ReadScopedLimits(u);
                credits = UsageJson.ReadCredits(u);
            }
            return new UsageSnapshot(fetchedAt, five, seven, scoped, credits);
```

In `UsageApiClient.cs`:

```csharp
            var credits = UsageJson.ReadCredits(doc.RootElement);
            return new UsageFetchResult(
                new UsageSnapshot(now, five, seven, scoped, credits), false, false, null);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: PASS. Note the pre-existing `ValidJson` fixture contains `"extra_usage": {}, "spend": 0` — both unusable shapes, so `Credits` must come out null there. If `Credits_Absent_IsNull` fails, a reader is accepting a non-object.

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Core src/ClaudeUsageTray/Core/UsageJson.cs tests/ClaudeUsageTray.Tests/UsageCacheReaderTests.cs
git commit -m "feat: parse credit usage from spend with an extra_usage fallback"
```

---

### Task 5: Format credits for display

**Files:**
- Create: `src/ClaudeUsageTray/Core/CreditFormat.cs`
- Create: `tests/ClaudeUsageTray.Tests/CreditFormatTests.cs`

**Interfaces:**
- Consumes: `Money`, `CreditState`, `CreditUsage` (Task 4).
- Produces: `public static string CreditFormat.Describe(CreditUsage c)` and
  `public static string? CreditFormat.DescribeState(CreditState s)` (null when there is nothing worth saying).

- [ ] **Step 1: Write the failing tests**

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class CreditFormatTests
{
    private static CreditUsage Credits(Money? used, Money? limit, int percent,
        bool enabled = true, string? reason = null, bool limitReached = false)
        => new(used, limit, percent, null, new CreditState(enabled, reason, limitReached));

    [Fact]
    public void Describe_Money_UsesIsoCodeAndExponentDecimals()
        => Assert.Equal("40.01 / 40.00 EUR (100%)", CreditFormat.Describe(
            Credits(new Money(4001, "EUR", 2), new Money(4000, "EUR", 2), 100)));

    [Fact]
    public void Describe_ZeroExponent_RendersNoDecimals()
        => Assert.Equal("1500 / 2000 JPY (75%)", CreditFormat.Describe(
            Credits(new Money(1500, "JPY", 0), new Money(2000, "JPY", 0), 75)));

    [Fact]
    public void Describe_NoAmounts_RendersPercentOnly()
        => Assert.Equal("73%", CreditFormat.Describe(Credits(null, null, 73)));

    [Fact]
    public void Describe_OverLimit_KeepsThePercentAboveOneHundred()
        => Assert.Equal("50.00 / 40.00 EUR (125%)", CreditFormat.Describe(
            Credits(new Money(5000, "EUR", 2), new Money(4000, "EUR", 2), 125)));

    [Fact]
    public void DescribeState_Normal_IsNull()
        => Assert.Null(CreditFormat.DescribeState(new CreditState(true, null, false)));

    [Fact]
    public void DescribeState_LimitReached_SaysSo()
        => Assert.Equal("limit reached", CreditFormat.DescribeState(new CreditState(true, null, true)));

    [Fact]
    public void DescribeState_Disabled_IncludesTheHumanisedReason()
        => Assert.Equal("disabled — org spend cap reached",
            CreditFormat.DescribeState(new CreditState(false, "org_spend_cap_reached", false)));

    [Fact]
    public void DescribeState_DisabledWithoutReason_JustSaysDisabled()
        => Assert.Equal("disabled", CreditFormat.DescribeState(new CreditState(false, null, false)));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj --filter CreditFormat`
Expected: FAIL to compile — `CreditFormat` does not exist.

- [ ] **Step 3: Write `CreditFormat.cs`**

```csharp
using System.Globalization;

namespace ClaudeUsageTray.Core;

/// <summary>Display strings for credit usage. Pure functions in Core so they are testable
/// without WinForms.</summary>
public static class CreditFormat
{
    /// <summary>"40.01 / 40.00 EUR (100%)", or "100%" when the amounts' units are unverified.
    /// The ISO code is used rather than a symbol: a code-to-symbol table is wrong for every code
    /// not in it, and CurrentCulture describes the user's locale, not the account's currency —
    /// it would print "$" for a EUR account.</summary>
    public static string Describe(CreditUsage c)
    {
        if (c.Used is not { } used || c.Limit is not { } limit) return $"{c.Percent}%";
        var code = string.IsNullOrEmpty(used.Currency) ? "" : $" {used.Currency}";
        return $"{Amount(used)} / {Amount(limit)}{code} ({c.Percent}%)";
    }

    /// <summary>The state worth showing on its own line, or null when there is none. Rendered
    /// separately rather than appended to the usage row, because "disabled" and "limit reached"
    /// mean different things and the reason carries which.</summary>
    public static string? DescribeState(CreditState s)
    {
        if (s.LimitReached) return "limit reached";
        if (s.Enabled) return null;
        return s.DisabledReason is { } reason ? $"disabled — {reason.Replace('_', ' ')}" : "disabled";
    }

    private static string Amount(Money m)
    {
        var scale = 1m;
        for (var i = 0; i < m.Exponent; i++) scale *= 10m;
        return (m.AmountMinor / scale).ToString($"F{m.Exponent}", CultureInfo.InvariantCulture);
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/CreditFormat.cs tests/ClaudeUsageTray.Tests/CreditFormatTests.cs
git commit -m "feat: add CreditFormat for currency-aware credit display strings"
```

---

### Task 6: Decide which scoped rows the popup draws

Row selection is logic, so it lives in Core where it can be tested; `UsagePopup` just draws what it returns.

**Files:**
- Create: `src/ClaudeUsageTray/Core/PopupRows.cs`
- Create: `tests/ClaudeUsageTray.Tests/PopupRowsTests.cs`

**Interfaces:**
- Consumes: `ScopedLimit` (Task 2).
- Produces:
  - `public sealed record ScopedRows(IReadOnlyList<ScopedLimit> Visible, int HiddenCount)`
  - `public static ScopedRows PopupRows.ForScopedLimits(IReadOnlyList<ScopedLimit> limits)`
  - `public const int PopupRows.Cap = 4`

- [ ] **Step 1: Write the failing tests**

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class PopupRowsTests
{
    private static ScopedLimit Limit(string label, int percent, bool active = false)
        => new(label, null, percent, null, active);

    private static IReadOnlyList<ScopedLimit> Many(int count, bool active)
        => Enumerable.Range(1, count).Select(i => Limit($"m{i}", 100 - i, active)).ToList();

    [Fact]
    public void SixInactive_ShowsFourAndHidesTwo()
    {
        var rows = PopupRows.ForScopedLimits(Many(6, active: false));

        Assert.Equal(4, rows.Visible.Count);
        Assert.Equal(2, rows.HiddenCount);
    }

    [Fact]
    public void ExactlyFour_HidesNothing()
    {
        var rows = PopupRows.ForScopedLimits(Many(4, active: false));

        Assert.Equal(4, rows.Visible.Count);
        Assert.Equal(0, rows.HiddenCount);
    }

    [Fact]
    public void ActiveRowsAreNeverHiddenByTheCap()
    {
        var limits = Many(5, active: true).Concat(Many(1, active: false)).ToList();

        var rows = PopupRows.ForScopedLimits(limits);

        Assert.Equal(5, rows.Visible.Count);
        Assert.All(rows.Visible, l => Assert.True(l.IsActive));
        Assert.Equal(1, rows.HiddenCount);   // counts only the hidden inactive row
    }

    [Fact]
    public void ActiveRowsConsumeCapSlotsBeforeInactiveOnes()
    {
        var limits = new[] { Limit("active", 10, active: true) }
            .Concat(Many(6, active: false)).ToList();

        var rows = PopupRows.ForScopedLimits(limits);

        Assert.Equal(4, rows.Visible.Count);
        Assert.Equal("active", rows.Visible[0].Label);
        Assert.Equal(3, rows.HiddenCount);
    }

    [Fact]
    public void Empty_ShowsNothing()
    {
        var rows = PopupRows.ForScopedLimits([]);

        Assert.Empty(rows.Visible);
        Assert.Equal(0, rows.HiddenCount);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj --filter PopupRows`
Expected: FAIL to compile — `PopupRows` does not exist.

- [ ] **Step 3: Write `PopupRows.cs`**

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>The scoped-limit rows the popup should draw, plus how many were withheld.</summary>
public sealed record ScopedRows(IReadOnlyList<ScopedLimit> Visible, int HiddenCount);

public static class PopupRows
{
    /// <summary>Row budget for scoped limits. The popup is AutoSize and PositionNearCursor clamps
    /// its position but not its size, so an unbounded row count would clip off-screen.</summary>
    public const int Cap = 4;

    /// <summary>Active limits always render, even past the cap: the cap exists to bound a list of
    /// background limits, and must never be the reason the limit actually throttling the user is
    /// invisible. HiddenCount therefore counts only withheld inactive rows.</summary>
    public static ScopedRows ForScopedLimits(IReadOnlyList<ScopedLimit> limits)
    {
        var active = limits.Where(l => l.IsActive).ToList();
        var inactive = limits.Where(l => !l.IsActive).ToList();

        var slots = Math.Max(0, Cap - active.Count);
        var shownInactive = Math.Min(slots, inactive.Count);

        return new ScopedRows(
            [.. active, .. inactive.Take(shownInactive)],
            inactive.Count - shownInactive);
    }
}
```

- [ ] **Step 4: Run them to verify they pass**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/PopupRows.cs tests/ClaudeUsageTray.Tests/PopupRowsTests.cs
git commit -m "feat: add PopupRows to bound scoped-limit rows without hiding active ones"
```

---

### Task 7: Render the new rows in the popup

WinForms construction is not unit-tested here (the existing popup isn't either); correctness rests on Tasks 5-6 being tested and on a build plus a manual look at the running tray.

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/UsagePopup.cs`

**Interfaces:**
- Consumes: `CreditFormat.Describe`, `CreditFormat.DescribeState` (Task 5); `PopupRows.ForScopedLimits`, `ScopedRows` (Task 6); `UsageSnapshot.ScopedLimits`, `UsageSnapshot.Credits` (Tasks 2, 4).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Extract the bar primitive**

Replace `AddWindowRow` (currently lines 66-102) with a caption/bar split. `AddBar` is the shared
primitive; scoped limits and credits get their own captions rather than being forced through a
`WindowUsage`, which would discard `ModelId` and `IsActive` before rendering.

```csharp
    private static void AddWindowRow(TableLayoutPanel layout, string title, WindowUsage? usage,
        Settings settings, DateTimeOffset now)
    {
        if (usage is null)
        {
            layout.Controls.Add(new Label { Text = $"{title}: no data", AutoSize = true });
            return;
        }
        var resets = usage.ResetsAt is { } r ? $" · resets in {RelativeTime.In(r, now)}" : "";
        AddCaption(layout, $"{title} — {usage.Percent}%{resets}");
        AddBar(layout, usage.Percent, SeverityFor(usage.Percent, settings));
    }

    private static void AddScopedRow(TableLayoutPanel layout, ScopedLimit limit,
        Settings settings, DateTimeOffset now)
    {
        var resets = limit.ResetsAt is { } r ? $" · resets in {RelativeTime.In(r, now)}" : "";
        AddCaption(layout, $"{limit.Label} weekly — {limit.Percent}%{resets}");
        AddBar(layout, limit.Percent, SeverityFor(limit.Percent, settings));
    }

    private static void AddCreditRow(TableLayoutPanel layout, CreditUsage credits, Settings settings)
    {
        AddCaption(layout, $"Credits — {CreditFormat.Describe(credits)}");
        // Credits prefer the payload's own severity: it can encode account state, such as a cap
        // already being reached, that a percentage alone cannot express.
        AddBar(layout, credits.Percent,
            ParseSeverity(credits.PayloadSeverity) ?? SeverityFor(credits.Percent, settings));

        if (CreditFormat.DescribeState(credits.State) is { } state)
        {
            layout.Controls.Add(new Label
            {
                Text = state,
                AutoSize = true,
                ForeColor = Color.Firebrick,
                Margin = new Padding(0, 0, 0, 4),
            });
        }
    }

    private static void AddCaption(TableLayoutPanel layout, string text)
        => layout.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 6, 0, 2) });

    private static Severity SeverityFor(int percent, Settings settings)
        => SeverityRules.For(percent, settings.Thresholds.Orange, settings.Thresholds.Red);

    private static Severity? ParseSeverity(string? payloadSeverity) => payloadSeverity switch
    {
        "critical" => Severity.Red,
        "warning" => Severity.Orange,
        "normal" => Severity.Green,
        _ => null,
    };

    /// <summary>Custom-drawn bar (ProgressBar can't be recolored per-severity).</summary>
    private static void AddBar(TableLayoutPanel layout, int percent, Severity severity)
    {
        var barColor = severity switch
        {
            Severity.Red => Color.FromArgb(224, 68, 68),
            Severity.Orange => Color.FromArgb(232, 150, 40),
            _ => Color.FromArgb(64, 184, 96),
        };
        var bar = new Panel { Width = 240, Height = 12, Margin = new Padding(0, 0, 0, 4) };
        var filled = Math.Clamp(percent, 0, 100);
        bar.Paint += (_, e) =>
        {
            e.Graphics.FillRectangle(SystemBrushes.ControlLight, 0, 0, bar.Width, bar.Height);
            using var brush = new SolidBrush(barColor);
            e.Graphics.FillRectangle(brush, 0, 0, bar.Width * filled / 100, bar.Height);
            e.Graphics.DrawRectangle(SystemPens.ControlDark, 0, 0, bar.Width - 1, bar.Height - 1);
        };
        layout.Controls.Add(bar);
    }
```

- [ ] **Step 2: Add the rows to the constructor**

In the `else` branch, after the two `AddWindowRow` calls and before the `updated` label:

```csharp
            var rows = PopupRows.ForScopedLimits(snapshot.ScopedLimits);
            foreach (var limit in rows.Visible) AddScopedRow(layout, limit, settings, now);
            if (rows.HiddenCount > 0)
            {
                layout.Controls.Add(new Label
                {
                    Text = $"+{rows.HiddenCount} more",
                    AutoSize = true,
                    ForeColor = SystemColors.GrayText,
                    Margin = new Padding(0, 2, 0, 0),
                });
            }
            if (snapshot.Credits is { } credits) AddCreditRow(layout, credits, settings);
```

- [ ] **Step 3: Build and run the whole suite**

Run: `dotnet build ClaudeUsageTray.sln` then `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: build succeeds with no warnings; all tests pass, including `SmokeTests`.

- [ ] **Step 4: Look at the running app**

Run: `dotnet run --project src/ClaudeUsageTray/ClaudeUsageTray.csproj`
Left-click the tray icon. With the current `~/.claude.json`, expect a `Fable weekly — 100%` bar
between the 7-day bar and a `Credits — 40.01 / 40.00 EUR (100%)` bar, followed by a red
`limit reached` line. Confirm the popup fits on screen and the 5-hour and 7-day rows are unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Tray/UsagePopup.cs
git commit -m "feat: show scoped limits and credit usage in the popup"
```

---

## Self-Review

**Spec coverage:** Data model → Task 2 (`ScopedLimit`), Task 4 (`Money`/`CreditState`/`CreditUsage`).
Renderable rules, label fallback, surface handling, dedup, ordering → Task 2. Flat-vs-`limits`
precedence → enforced by Task 2's group/scope filters, asserted by
`ScopedLimit_SessionGroup_IsExcluded` and `ScopedLimit_NullScope_IsExcluded`. Credit source
precedence and `LimitReached` derivation → Task 4. ISO-code formatting and state text → Task 5.
Row cap with active exemption → Task 6. Bar-primitive extraction, per-row-type severity sourcing,
`IsActive` drawing nothing → Task 7. Diagnostics/privacy → satisfied by omission: no task adds a
`FetchLog.Write` call.

**Known gap:** the spec's `UsageApiClientTests` fixture list mentions re-testing every cache
fixture at root nesting. Task 3 covers scoped limits only. The readers are literally the same
methods on the same container element, so duplicating all fourteen fixtures would assert the same
code twice; Task 3's single root-nesting test plus Task 4's shared `ReadCredits` wiring is the
proportionate coverage. Recorded rather than silently dropped.
