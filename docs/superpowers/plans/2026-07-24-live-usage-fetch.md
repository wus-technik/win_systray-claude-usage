# v0.3 Live Usage Fetch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The tray shows live usage by polling `https://api.anthropic.com/api/oauth/usage` with Claude Code's existing OAuth token, read-only, under a strict budget — fixing the permanently-stale cache problem (spec: `docs/superpowers/specs/2026-07-24-live-usage-fetch-design.md`).

**Architecture:** Pure Core units (`CredentialsReader`, `UsageApiClient` + shared `UsageJson` window parser, `FetchScheduler` budget gate, `SnapshotPrecedence`) are fully unit-tested; `TrayApp` gains a poll timer with single-flight fetch and UI-thread marshaling, consulting the scheduler before every fetch, plus a freshness-precedence rule that also stops the 30 s cache re-read from clobbering fresher API data. Cache reading remains the fallback path.

**Tech Stack:** C# / .NET 10 WinForms, `System.Net.Http` (built in), `System.Text.Json`, xUnit (fake `HttpMessageHandler` for API tests).

## Global Constraints

- Endpoint: `GET https://api.anthropic.com/api/oauth/usage` with headers `Authorization: Bearer <accessToken>`, `anthropic-beta: oauth-2025-04-20`, `User-Agent: ClaudeUsageTray/<version>`. No other Anthropic calls.
- Credentials: `%USERPROFILE%\.claude\.credentials.json`, key `claudeAiOauth.accessToken`, gated by `claudeAiOauth.expiresAt` (Unix ms) > now + 5 min. READ-ONLY. Never refresh/rotate tokens; never log, display, or persist token material — tests use dummy strings only.
- Budget: poll every 5 min; first fetch at startup; ≥ 30 s between any two API requests AND a rolling-hour hard cap of 20 requests (manual + timed combined); ANY 429 (with, without, or with HTTP-date `Retry-After`) → next fetch no sooner than `max(Retry-After, 15 min)`; 401/403 → remember rejected token, skip fetches until `CredentialsReader` returns a different token; network error/timeout → backoff 5 → 10 → 20 min (capped); HTTP timeout 5 s. All budget state lives in the unit-tested `FetchScheduler` — TrayApp only consults/records.
- Freshness precedence: a snapshot (API or cache) only replaces the current one when its `FetchedAt` is strictly newer; the cache path may clear the snapshot to null only when the current snapshot is already stale per `Settings.StalenessMinutes`.
- Both new file parsers follow `UsageCacheReader` conventions: `FileShare.ReadWrite`, 32 MiB guard, never throw, non-object-root tolerant.
- Existing tests must keep passing; `UsageCacheReaderTests` unmodified.
- Before every task, inspect `git status --short`. Commit only task-owned paths (never `git add -A`) and never add AI co-author trailers.

## File Structure

```
src/ClaudeUsageTray/Core/CredentialsReader.cs      (Task 1: token extraction)
src/ClaudeUsageTray/Core/UsageJson.cs              (Task 2: shared window parser)
src/ClaudeUsageTray/Core/UsageApiClient.cs         (Task 2: endpoint client + UsageFetchResult)
src/ClaudeUsageTray/Core/UsageCacheReader.cs       (Task 2: delegate to UsageJson)
src/ClaudeUsageTray/Core/FetchScheduler.cs         (Task 3: budget gate)
src/ClaudeUsageTray/Core/SnapshotPrecedence.cs     (Task 3: freshness rule)
src/ClaudeUsageTray/Tray/TrayApp.cs                (Task 3: poll timer + wiring)
tests/ClaudeUsageTray.Tests/CredentialsReaderTests.cs   (Task 1)
tests/ClaudeUsageTray.Tests/UsageApiClientTests.cs      (Task 2)
tests/ClaudeUsageTray.Tests/FetchSchedulerTests.cs      (Task 3)
tests/ClaudeUsageTray.Tests/SnapshotPrecedenceTests.cs  (Task 3)
README.md, docs/superpowers/spec/claude-usage-tray.md,
src/ClaudeUsageTray/ClaudeUsageTray.csproj         (Task 4: docs + 0.3.0)
```

---

### Task 1: CredentialsReader

**Files:**
- Create: `src/ClaudeUsageTray/Core/CredentialsReader.cs`
- Test: `tests/ClaudeUsageTray.Tests/CredentialsReaderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static string CredentialsReader.DefaultPath` and `static string? CredentialsReader.TryReadAccessToken(string path, DateTimeOffset now)` in `ClaudeUsageTray.Core`. Null on missing file/key, expired or near-expiry (< 5 min) token, malformed JSON, non-object root, or IO error. Never throws.

- [ ] **Step 1: Write the failing tests** — `tests/ClaudeUsageTray.Tests/CredentialsReaderTests.cs`

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class CredentialsReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("cut-creds-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFixture(string json)
    {
        var path = Path.Combine(_dir, ".credentials.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string CredsJson(string token, long expiresAtMs) => $$"""
        { "claudeAiOauth": { "accessToken": "{{token}}", "refreshToken": "dummy-refresh",
          "expiresAt": {{expiresAtMs}}, "scopes": ["user:inference"], "subscriptionType": "max" } }
        """;

    [Fact]
    public void ValidFutureToken_IsReturned()
        => Assert.Equal("dummy-token-abc", CredentialsReader.TryReadAccessToken(
            WriteFixture(CredsJson("dummy-token-abc", Now.AddHours(2).ToUnixTimeMilliseconds())), Now));

    [Fact]
    public void ExpiredToken_ReturnsNull()
        => Assert.Null(CredentialsReader.TryReadAccessToken(
            WriteFixture(CredsJson("dummy-token-abc", Now.AddMinutes(-1).ToUnixTimeMilliseconds())), Now));

    [Fact]
    public void NearExpiryToken_ReturnsNull() // < 5 min margin
        => Assert.Null(CredentialsReader.TryReadAccessToken(
            WriteFixture(CredsJson("dummy-token-abc", Now.AddMinutes(4).ToUnixTimeMilliseconds())), Now));

    [Fact]
    public void MissingFile_ReturnsNull()
        => Assert.Null(CredentialsReader.TryReadAccessToken(Path.Combine(_dir, "nope.json"), Now));

    [Fact]
    public void MissingClaudeAiOauthKey_ReturnsNull()
        => Assert.Null(CredentialsReader.TryReadAccessToken(
            WriteFixture("""{ "mcpOAuth": {} }"""), Now));

    [Fact]
    public void EmptyToken_ReturnsNull()
        => Assert.Null(CredentialsReader.TryReadAccessToken(
            WriteFixture(CredsJson("", Now.AddHours(2).ToUnixTimeMilliseconds())), Now));

    [Fact]
    public void MissingExpiresAt_ReturnsNull()
        => Assert.Null(CredentialsReader.TryReadAccessToken(
            WriteFixture("""{ "claudeAiOauth": { "accessToken": "dummy-token-abc" } }"""), Now));

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("42")]
    public void MalformedOrNonObject_ReturnsNull(string json)
        => Assert.Null(CredentialsReader.TryReadAccessToken(WriteFixture(json), Now));

    [Fact]
    public void DefaultPath_IsClaudeCredentialsUnderUserProfile()
        => Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", ".credentials.json"),
            CredentialsReader.DefaultPath);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter CredentialsReaderTests`
Expected: FAIL to compile — `CredentialsReader` not defined.

- [ ] **Step 3: Write the implementation** — `src/ClaudeUsageTray/Core/CredentialsReader.cs`

```csharp
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>
/// Read-only extraction of Claude Code's OAuth access token. This app NEVER writes the
/// credentials file, never refreshes tokens, and never logs or persists token material.
/// </summary>
public static class CredentialsReader
{
    /// <summary>Margin below which a token is treated as unusable (Claude Code will refresh it).</summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    /// <summary>claudeAiOauth.accessToken when present, non-empty, and valid past the margin; else null. Never throws.</summary>
    public static string? TryReadAccessToken(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > 32 * 1024 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object) return null;
            if (!oauth.TryGetProperty("accessToken", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String) return null;
            var token = tokenElement.GetString();
            if (string.IsNullOrEmpty(token)) return null;
            if (!oauth.TryGetProperty("expiresAt", out var expires)
                || expires.ValueKind != JsonValueKind.Number
                || !expires.TryGetInt64(out var expiresAtMs)) return null;
            if (DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs) <= now + ExpiryMargin) return null;
            return token;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
            or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter CredentialsReaderTests`
Expected: PASS (11 tests).

- [ ] **Step 5: Run the full suite, then commit**

Run: `dotnet test` — expected PASS (84 total).

```powershell
git add src/ClaudeUsageTray/Core/CredentialsReader.cs tests/ClaudeUsageTray.Tests/CredentialsReaderTests.cs
git commit -m "feat: read-only OAuth access token extraction from Claude Code credentials"
```

---

### Task 2: UsageJson, UsageApiClient, UsageFetchResult

**Files:**
- Create: `src/ClaudeUsageTray/Core/UsageJson.cs`
- Create: `src/ClaudeUsageTray/Core/UsageApiClient.cs`
- Modify: `src/ClaudeUsageTray/Core/UsageCacheReader.cs` (delegate window parsing to `UsageJson`)
- Test: `tests/ClaudeUsageTray.Tests/UsageApiClientTests.cs`

**Interfaces:**
- Consumes: `UsageSnapshot`, `WindowUsage` (existing records).
- Produces (namespace `ClaudeUsageTray.Core`):
  - `internal static WindowUsage? UsageJson.ReadWindow(JsonElement parent, string name)` — the exact parsing currently private in `UsageCacheReader` (integer `utilization`, optional ISO `resets_at` with `AssumeUniversal|AdjustToUniversal`).
  - `sealed record UsageFetchResult(UsageSnapshot? Snapshot, bool Unauthorized, bool RateLimited, TimeSpan? RetryAfter)` — `RateLimited` is true for ANY 429 regardless of header form; `RetryAfter` is the delta form, or the HTTP-date form computed against `now`, else null.
  - `static Task<UsageFetchResult> UsageApiClient.FetchAsync(HttpClient http, string accessToken, DateTimeOffset now, CancellationToken ct)` and `const string UsageApiClient.EndpointUrl`. Never throws.

- [ ] **Step 1: Write the failing tests** — `tests/ClaudeUsageTray.Tests/UsageApiClientTests.cs`

```csharp
using System.Net;
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class UsageApiClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

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

    private const string BothWindows = """
        { "five_hour": { "utilization": 42, "resets_at": "2026-07-24T18:39:59Z" },
          "seven_day": { "utilization": 13, "resets_at": "2026-07-27T15:59:59Z" },
          "extra_usage": {} }
        """;

    private static (UsageFetchResult Result, FakeHandler Handler) Fetch(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        using var http = new HttpClient(handler);
        var result = UsageApiClient.FetchAsync(http, "dummy-token", Now, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (result, handler);
    }

    [Fact]
    public void Success_ParsesBothWindows_AndStampsNow()
    {
        var (r, _) = Fetch(_ => Json(HttpStatusCode.OK, BothWindows));
        Assert.NotNull(r.Snapshot);
        Assert.Equal(Now, r.Snapshot!.FetchedAt);
        Assert.Equal(42, r.Snapshot.FiveHour!.Percent);
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 18, 39, 59, TimeSpan.Zero), r.Snapshot.FiveHour.ResetsAt);
        Assert.Equal(13, r.Snapshot.SevenDay!.Percent);
        Assert.False(r.Unauthorized);
        Assert.Null(r.RetryAfter);
    }

    [Fact]
    public void Request_HasExactUrlAndHeaders()
    {
        var (_, h) = Fetch(_ => Json(HttpStatusCode.OK, BothWindows));
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", h.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", h.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("dummy-token", h.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal("oauth-2025-04-20", Assert.Single(h.LastRequest.Headers.GetValues("anthropic-beta")));
        Assert.StartsWith("ClaudeUsageTray/", h.LastRequest.Headers.UserAgent.ToString());
    }

    [Fact]
    public void Success_MissingWindow_IsNull()
    {
        var (r, _) = Fetch(_ => Json(HttpStatusCode.OK, """{ "five_hour": { "utilization": 7 } }"""));
        Assert.Equal(7, r.Snapshot!.FiveHour!.Percent);
        Assert.Null(r.Snapshot.FiveHour.ResetsAt);
        Assert.Null(r.Snapshot.SevenDay);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1]")]
    public void MalformedBody_ReturnsFailure(string body)
    {
        var (r, _) = Fetch(_ => Json(HttpStatusCode.OK, body));
        Assert.Null(r.Snapshot);
        Assert.False(r.Unauthorized);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void AuthFailure_SetsUnauthorized(HttpStatusCode status)
    {
        var (r, _) = Fetch(_ => new HttpResponseMessage(status));
        Assert.Null(r.Snapshot);
        Assert.True(r.Unauthorized);
    }

    [Fact]
    public void TooManyRequests_CarriesRetryAfterDelta()
    {
        var (r, _) = Fetch(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1200));
            return resp;
        });
        Assert.Null(r.Snapshot);
        Assert.False(r.Unauthorized);
        Assert.True(r.RateLimited);
        Assert.Equal(TimeSpan.FromSeconds(1200), r.RetryAfter);
    }

    [Fact]
    public void TooManyRequests_WithHttpDateRetryAfter_ComputesDeltaAgainstNow()
    {
        var (r, _) = Fetch(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(Now.AddMinutes(30));
            return resp;
        });
        Assert.True(r.RateLimited);
        Assert.Equal(TimeSpan.FromMinutes(30), r.RetryAfter);
    }

    [Fact]
    public void TooManyRequests_WithoutHeader_IsStillRateLimited()
    {
        var (r, _) = Fetch(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        Assert.Null(r.Snapshot);
        Assert.True(r.RateLimited);
        Assert.Null(r.RetryAfter);
    }

    [Fact]
    public void ServerError_ReturnsFailure_NotRateLimited()
    {
        var (r, _) = Fetch(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Null(r.Snapshot);
        Assert.False(r.Unauthorized);
        Assert.False(r.RateLimited);
    }

    [Fact]
    public void NetworkException_ReturnsFailure_NeverThrows()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("boom"));
        using var http = new HttpClient(handler);
        var r = UsageApiClient.FetchAsync(http, "dummy-token", Now, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.Null(r.Snapshot);
        Assert.False(r.Unauthorized);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter UsageApiClientTests`
Expected: FAIL to compile — types not defined.

- [ ] **Step 3: Write `src/ClaudeUsageTray/Core/UsageJson.cs`**

```csharp
using System.Globalization;
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Shared parser for one usage window ({ "utilization": int, "resets_at": iso }) —
/// used by both the .claude.json cache reader and the usage-API client.</summary>
internal static class UsageJson
{
    internal static WindowUsage? ReadWindow(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("utilization", out var p) || p.ValueKind != JsonValueKind.Number
            || !p.TryGetInt32(out var percent)) return null;

        DateTimeOffset? resetsAt = null;
        if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            resetsAt = parsed;
        }
        return new WindowUsage(percent, resetsAt);
    }
}
```

- [ ] **Step 4: Modify `src/ClaudeUsageTray/Core/UsageCacheReader.cs`** — delete its private `ReadWindow` method and replace the two call sites `ReadWindow(u, "five_hour")` / `ReadWindow(u, "seven_day")` with `UsageJson.ReadWindow(u, "five_hour")` / `UsageJson.ReadWindow(u, "seven_day")`. Remove the now-unused `using System.Globalization;` if nothing else needs it. No behavior change — `UsageCacheReaderTests` must pass unmodified.

- [ ] **Step 5: Write `src/ClaudeUsageTray/Core/UsageApiClient.cs`**

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Outcome of one usage-API fetch: Snapshot set on success; Unauthorized on 401/403;
/// RateLimited on ANY 429 (so throttles are never mistaken for network errors);
/// RetryAfter from the 429's header — delta form, or HTTP-date computed against now.</summary>
public sealed record UsageFetchResult(UsageSnapshot? Snapshot, bool Unauthorized, bool RateLimited, TimeSpan? RetryAfter);

/// <summary>Read-only client for Anthropic's OAuth usage endpoint. Never throws; never logs the token.</summary>
public static class UsageApiClient
{
    public const string EndpointUrl = "https://api.anthropic.com/api/oauth/usage";

    private static readonly string UserAgent =
        $"ClaudeUsageTray/{typeof(UsageApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    public static async Task<UsageFetchResult> FetchAsync(
        HttpClient http, string accessToken, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, EndpointUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new UsageFetchResult(null, Unauthorized: true, RateLimited: false, RetryAfter: null);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var header = response.Headers.RetryAfter;
                TimeSpan? retryAfter = header?.Delta ?? (header?.Date is { } date ? date - now : null);
                return new UsageFetchResult(null, false, RateLimited: true, retryAfter);
            }
            if (!response.IsSuccessStatusCode)
                return new UsageFetchResult(null, false, false, null);

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new UsageFetchResult(null, false, false, null);

            var five = UsageJson.ReadWindow(doc.RootElement, "five_hour");
            var seven = UsageJson.ReadWindow(doc.RootElement, "seven_day");
            return new UsageFetchResult(new UsageSnapshot(now, five, seven), false, false, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
            or OperationCanceledException or JsonException)
        {
            return new UsageFetchResult(null, false, false, null);
        }
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter UsageApiClientTests` — expected PASS (13 tests).
Then: `dotnet test --filter UsageCacheReaderTests` — expected PASS (21 tests, file unmodified).

- [ ] **Step 7: Run the full suite, then commit**

Run: `dotnet test` — expected PASS (97 total).

```powershell
git add src/ClaudeUsageTray/Core/UsageJson.cs src/ClaudeUsageTray/Core/UsageApiClient.cs src/ClaudeUsageTray/Core/UsageCacheReader.cs tests/ClaudeUsageTray.Tests/UsageApiClientTests.cs
git commit -m "feat: usage-API client with budget-aware result contract and shared window parser"
```

---

### Task 3: FetchScheduler, SnapshotPrecedence, TrayApp polling integration

**Files:**
- Create: `src/ClaudeUsageTray/Core/FetchScheduler.cs`
- Create: `src/ClaudeUsageTray/Core/SnapshotPrecedence.cs`
- Test: `tests/ClaudeUsageTray.Tests/FetchSchedulerTests.cs`
- Test: `tests/ClaudeUsageTray.Tests/SnapshotPrecedenceTests.cs`
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs`

**Interfaces:**
- Consumes: `CredentialsReader.TryReadAccessToken` / `DefaultPath` (Task 1), `UsageApiClient.FetchAsync` / `UsageFetchResult` (Task 2), existing TrayApp members (`_sync`, `_snapshot`, `Refresh()`, `Render()`, `_settings`, `_consecutiveReadFailures`).
- Produces:
  - `sealed class FetchScheduler` (`ClaudeUsageTray.Core`) with `FetchScheduler(int maxPerHour = 20)`, `bool CanFetch(DateTimeOffset now)`, `void RecordAttempt(DateTimeOffset now)`, `void RecordSuccess()`, `void RecordRateLimited(DateTimeOffset now, TimeSpan? retryAfter)`, `void RecordFailure(DateTimeOffset now)`. Encapsulates: 30 s floor after every attempt, rolling-hour attempt cap, ≥ 15 min penalty on any rate limit (`max(retryAfter, 15 min)`), 5/10/20 min failure backoff (streak capped, reset on success).
  - `static bool SnapshotPrecedence.IsNewer(UsageSnapshot? candidate, UsageSnapshot? current)` — true iff `candidate` is non-null AND (`current` is null OR `candidate.FetchedAt > current.FetchedAt`).
  - TrayApp polls the API per the budget rules, consulting the scheduler before every fetch.

- [ ] **Step 1a: Write the failing scheduler tests** — `tests/ClaudeUsageTray.Tests/FetchSchedulerTests.cs`

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class FetchSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshScheduler_AllowsFetch() => Assert.True(new FetchScheduler().CanFetch(T0));

    [Fact]
    public void Floor_BlocksFor30Seconds_ThenAllows()
    {
        var s = new FetchScheduler();
        s.RecordAttempt(T0);
        Assert.False(s.CanFetch(T0.AddSeconds(29)));
        Assert.True(s.CanFetch(T0.AddSeconds(30)));
    }

    [Fact]
    public void RollingHourCap_BlocksAtLimit_UnblocksWhenOldestAgesOut()
    {
        var s = new FetchScheduler(maxPerHour: 3);
        s.RecordAttempt(T0);
        s.RecordAttempt(T0.AddMinutes(1));
        s.RecordAttempt(T0.AddMinutes(2));
        Assert.False(s.CanFetch(T0.AddMinutes(10)));           // cap reached
        Assert.False(s.CanFetch(T0.AddMinutes(59)));           // still 3 in window
        Assert.True(s.CanFetch(T0.AddMinutes(60)));            // T0 attempt aged out
    }

    [Fact]
    public void RateLimited_WithoutRetryAfter_Blocks15Minutes()
    {
        var s = new FetchScheduler();
        s.RecordAttempt(T0);
        s.RecordRateLimited(T0, retryAfter: null);
        Assert.False(s.CanFetch(T0.AddMinutes(14)));
        Assert.True(s.CanFetch(T0.AddMinutes(15)));
    }

    [Fact]
    public void RateLimited_WithLongRetryAfter_BlocksForRetryAfter()
    {
        var s = new FetchScheduler();
        s.RecordAttempt(T0);
        s.RecordRateLimited(T0, TimeSpan.FromMinutes(45));
        Assert.False(s.CanFetch(T0.AddMinutes(44)));
        Assert.True(s.CanFetch(T0.AddMinutes(45)));
    }

    [Fact]
    public void RateLimited_WithShortRetryAfter_StillBlocks15Minutes()
    {
        var s = new FetchScheduler();
        s.RecordAttempt(T0);
        s.RecordRateLimited(T0, TimeSpan.FromMinutes(2));
        Assert.False(s.CanFetch(T0.AddMinutes(14)));
        Assert.True(s.CanFetch(T0.AddMinutes(15)));
    }

    [Fact]
    public void FailureBackoff_Escalates5_10_20_AndCaps()
    {
        var s = new FetchScheduler();
        s.RecordFailure(T0);
        Assert.False(s.CanFetch(T0.AddMinutes(4)));
        Assert.True(s.CanFetch(T0.AddMinutes(5)));
        s.RecordFailure(T0.AddMinutes(5));
        Assert.False(s.CanFetch(T0.AddMinutes(14)));
        Assert.True(s.CanFetch(T0.AddMinutes(15)));            // 5 + 10
        s.RecordFailure(T0.AddMinutes(15));
        Assert.True(s.CanFetch(T0.AddMinutes(35)));            // 15 + 20
        s.RecordFailure(T0.AddMinutes(35));
        Assert.False(s.CanFetch(T0.AddMinutes(54)));           // still 20 (capped)
        Assert.True(s.CanFetch(T0.AddMinutes(55)));
    }

    [Fact]
    public void Success_ResetsFailureStreak()
    {
        var s = new FetchScheduler();
        s.RecordFailure(T0);
        s.RecordFailure(T0.AddMinutes(5));
        s.RecordSuccess();
        s.RecordFailure(T0.AddMinutes(30));
        Assert.True(s.CanFetch(T0.AddMinutes(35)));            // back to 5 min, not 20
    }

    [Fact]
    public void BudgetCap_AppliesEvenAfterFloorElapsed()
    {
        var s = new FetchScheduler(maxPerHour: 1);
        s.RecordAttempt(T0);
        Assert.False(s.CanFetch(T0.AddMinutes(30)));           // floor long past; budget blocks
        Assert.True(s.CanFetch(T0.AddMinutes(60)));
    }
}
```

- [ ] **Step 1b: Write the failing precedence tests** — `tests/ClaudeUsageTray.Tests/SnapshotPrecedenceTests.cs`

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class SnapshotPrecedenceTests
{
    private static UsageSnapshot At(int hour) => new(new DateTimeOffset(2026, 7, 24, hour, 0, 0, TimeSpan.Zero), null, null);

    [Fact] public void NullCandidate_IsNeverNewer() => Assert.False(SnapshotPrecedence.IsNewer(null, At(12)));
    [Fact] public void NullCandidate_AgainstNull_IsNotNewer() => Assert.False(SnapshotPrecedence.IsNewer(null, null));
    [Fact] public void Candidate_AgainstNullCurrent_IsNewer() => Assert.True(SnapshotPrecedence.IsNewer(At(12), null));
    [Fact] public void NewerCandidate_Wins() => Assert.True(SnapshotPrecedence.IsNewer(At(13), At(12)));
    [Fact] public void OlderCandidate_Loses() => Assert.False(SnapshotPrecedence.IsNewer(At(11), At(12)));
    [Fact] public void EqualTimestamp_Loses() => Assert.False(SnapshotPrecedence.IsNewer(At(12), At(12)));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FetchSchedulerTests|SnapshotPrecedenceTests"`
Expected: FAIL to compile — `FetchScheduler` / `SnapshotPrecedence` not defined.

- [ ] **Step 3a: Write `src/ClaudeUsageTray/Core/FetchScheduler.cs`**

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>
/// Budget gate for usage-API fetches: a 30 s floor between attempts, a rolling-hour attempt
/// cap (manual and timed fetches combined), a ≥ 15 min penalty on any rate limit, and
/// 5/10/20-minute network-failure backoff. Pure state machine driven by caller-supplied
/// timestamps — no clocks, no threads, fully unit-testable.
/// </summary>
public sealed class FetchScheduler
{
    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RateLimitPenalty = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BudgetWindow = TimeSpan.FromHours(1);

    private readonly int _maxPerHour;
    private readonly Queue<DateTimeOffset> _attempts = new();
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;
    private int _failureStreak;

    /// <summary>Default 20/h: safety margin under the endpoint's measured ~28-30/h per-token budget.</summary>
    public FetchScheduler(int maxPerHour = 20) => _maxPerHour = maxPerHour;

    public bool CanFetch(DateTimeOffset now)
    {
        if (now < _notBefore) return false;
        while (_attempts.Count > 0 && now - _attempts.Peek() >= BudgetWindow) _attempts.Dequeue();
        return _attempts.Count < _maxPerHour;
    }

    public void RecordAttempt(DateTimeOffset now)
    {
        _attempts.Enqueue(now);
        _notBefore = now + Floor;
    }

    public void RecordSuccess() => _failureStreak = 0;

    public void RecordRateLimited(DateTimeOffset now, TimeSpan? retryAfter)
        => _notBefore = now + (retryAfter > RateLimitPenalty ? retryAfter.Value : RateLimitPenalty);

    public void RecordFailure(DateTimeOffset now)
    {
        _failureStreak = Math.Min(_failureStreak + 1, 3);
        var minutes = _failureStreak switch { 1 => 5, 2 => 10, _ => 20 };
        _notBefore = now + TimeSpan.FromMinutes(minutes);
    }
}
```

- [ ] **Step 3b: Write `src/ClaudeUsageTray/Core/SnapshotPrecedence.cs`**

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>Freshness rule: a snapshot only replaces the current one when strictly newer —
/// this is what stops the 30 s cache re-read from clobbering a fresher API fetch.</summary>
public static class SnapshotPrecedence
{
    public static bool IsNewer(UsageSnapshot? candidate, UsageSnapshot? current)
        => candidate is not null && (current is null || candidate.FetchedAt > current.FetchedAt);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FetchSchedulerTests|SnapshotPrecedenceTests"` — expected PASS (15 tests: 9 scheduler + 6 precedence).

- [ ] **Step 5: Modify `src/ClaudeUsageTray/Tray/TrayApp.cs`** — read the current file first; it contains patterns (e.g. `_settingsSaveFailed`, `DisposeNotifyIcon`, `TryIsStartupEnabled`) that must be preserved. Make exactly these changes:

a) Add fields next to the existing timers:

```csharp
    // Live usage polling: 5 min steady state; all budget/backoff state lives in the
    // unit-tested FetchScheduler. Single-flight via _fetchInFlight (UI thread only).
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 300_000 };
    private readonly FetchScheduler _fetchScheduler = new();
    private bool _fetchInFlight;
    private string? _rejectedToken;
```

b) In the constructor, after `_tick.Start();` add:

```csharp
        _poll.Tick += (_, _) => StartApiFetch();
        _poll.Start();
```

and after the existing initial `Refresh();` call add:

```csharp
        StartApiFetch();
```

c) Add the fetch methods (new `// ---- live usage polling ----` section after `Refresh()`):

```csharp
    private void StartApiFetch()
    {
        var now = DateTimeOffset.UtcNow;
        if (_fetchInFlight || !_fetchScheduler.CanFetch(now)) return;
        var token = CredentialsReader.TryReadAccessToken(CredentialsReader.DefaultPath, now);
        if (token is null || token == _rejectedToken) return;

        _fetchInFlight = true;
        _fetchScheduler.RecordAttempt(now);
        _ = Task.Run(async () =>
        {
            var result = await UsageApiClient.FetchAsync(Http, token, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
            try { _sync.BeginInvoke((Action)(() => OnApiFetchCompleted(result, token))); }
            catch (InvalidOperationException) { /* app shutting down */ }
        });
    }

    private void OnApiFetchCompleted(UsageFetchResult result, string token)
    {
        _fetchInFlight = false;
        var now = DateTimeOffset.UtcNow;
        if (result.Snapshot is not null)
        {
            _fetchScheduler.RecordSuccess();
            _rejectedToken = null;
            if (SnapshotPrecedence.IsNewer(result.Snapshot, _snapshot))
            {
                _snapshot = result.Snapshot;
                _consecutiveReadFailures = 0;
            }
        }
        else if (result.Unauthorized)
        {
            // Claude Code owns the credentials; wait for it to refresh them rather than retrying.
            _rejectedToken = token;
        }
        else if (result.RateLimited)
        {
            _fetchScheduler.RecordRateLimited(now, result.RetryAfter);
        }
        else
        {
            _fetchScheduler.RecordFailure(now);
        }
        Render();
    }
```

d) In `Refresh()`, apply the precedence rule: replace the successful-read branch body

```csharp
        if (read is not null)
        {
            _snapshot = read;
            _consecutiveReadFailures = 0;
        }
```

with

```csharp
        if (read is not null)
        {
            if (SnapshotPrecedence.IsNewer(read, _snapshot)) _snapshot = read;
            _consecutiveReadFailures = 0;
        }
```

and guard both `_snapshot = null;` assignments (missing file; 3 consecutive failures) so the cache path only clears an already-stale snapshot:

```csharp
            if (_snapshot is null || DateTimeOffset.UtcNow - _snapshot.FetchedAt
                > TimeSpan.FromMinutes(_settings.StalenessMinutes))
            {
                _snapshot = null;
            }
```

(keep each branch's `_consecutiveReadFailures = 0;` as is).

e) The "Refresh now" menu handler currently calls `Refresh()`; extend it to also call `StartApiFetch()` (the 30 s floor makes repeated clicks safe).

f) In `Dispose(bool disposing)`, dispose `_poll` alongside the other timers.

- [ ] **Step 6: Build and run the full suite (regression)**

Run: `dotnet test` — expected PASS (112 total), build warning-free.

- [ ] **Step 7: Manual verification — live data (deferred to human where noted)**

Run: `dotnet run --project src/ClaudeUsageTray` (human): within seconds of startup the icons should show CURRENT usage (matching claude.ai), not the stale cache; the tooltip's "Updated just now" confirms an API fetch. "Refresh now" repeated quickly must not fire more than one API request per 30 s. With `%USERPROFILE%\.claude\.credentials.json` renamed away (restore afterwards!), behavior must degrade to v0.2 cache-fallback with no crash.

- [ ] **Step 8: Commit**

```powershell
git add src/ClaudeUsageTray/Core/FetchScheduler.cs src/ClaudeUsageTray/Core/SnapshotPrecedence.cs tests/ClaudeUsageTray.Tests/FetchSchedulerTests.cs tests/ClaudeUsageTray.Tests/SnapshotPrecedenceTests.cs src/ClaudeUsageTray/Tray/TrayApp.cs
git commit -m "feat: live usage polling with budget scheduler and freshness precedence"
```

---

### Task 4: Docs and version bump

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/spec/claude-usage-tray.md` (extend amendment note)
- Modify: `src/ClaudeUsageTray/ClaudeUsageTray.csproj` (`<Version>` → `0.3.0`)

**Interfaces:** none (docs only).

- [ ] **Step 1: Update README**

a) Replace the opening-paragraph claim

```markdown
writes to `%USERPROFILE%\.claude.json` (`cachedUsageUtilization`) — **no network
calls to Anthropic, no tokens, no credentials**. Spec: `docs/superpowers/spec/claude-usage-tray.md`.
```

with

```markdown
writes to `%USERPROFILE%\.claude.json` (`cachedUsageUtilization`), combined with
**live polling of Anthropic's OAuth usage endpoint** (read-only, every 5 minutes,
using Claude Code's own token — the app never stores, refreshes, or logs it).
Spec: `docs/superpowers/spec/claude-usage-tray.md`.
```

b) In the Usage section, after the "- **Right-click** …" bullet, add:

```markdown
- **Data:** fetched live from Anthropic's usage API (same source as claude.ai)
  every 5 minutes with strict rate-limit respect; falls back to Claude Code's
  local cache when no valid token is available.
```

- [ ] **Step 2: Extend the v0.1 spec amendment note**

In `docs/superpowers/spec/claude-usage-tray.md`, the blockquote note near the top (added for v0.2) gets one more sentence appended inside the same blockquote:

```markdown
> **Amended (v0.3):** the "no network calls to Anthropic" constraint is superseded — the app now polls `GET https://api.anthropic.com/api/oauth/usage` read-only with Claude Code's existing OAuth token under a strict budget; see `docs/superpowers/specs/2026-07-24-live-usage-fetch-design.md`. The `.claude.json` cache read remains as fallback. Credentials are still never written, refreshed, or logged.
```

(as a second blockquote paragraph directly below the v0.2 note).

- [ ] **Step 3: Bump the version**

In `src/ClaudeUsageTray/ClaudeUsageTray.csproj`: `<Version>0.2.0</Version>` → `<Version>0.3.0</Version>`.

- [ ] **Step 4: Full suite, then commit**

Run: `dotnet test` — expected PASS (112 total).

```powershell
git add README.md docs/superpowers/spec/claude-usage-tray.md src/ClaudeUsageTray/ClaudeUsageTray.csproj
git commit -m "docs: document live usage polling; bump version to 0.3.0"
```

---

## Acceptance map (from the spec)

| Spec requirement | Task |
|---|---|
| Token extraction, expiry-gated, read-only, never throws | 1 |
| Endpoint/headers contract, response parsing, error → result mapping | 2 |
| Shared window parser (no duplicated parsing logic) | 2 |
| 5 min cadence, 30 s floor, rolling-hour cap 20, any-429 ≥ 15 min penalty, 401 rejected-token latch, network backoff 5/10/20 | 3 (FetchScheduler, unit-tested) |
| Freshness precedence incl. cache-clobber fix and stale-only clearing | 3 |
| Single-flight + UI-thread marshaling | 3 |
| README truthfulness + v0.1 spec amendment + 0.3.0 | 4 |
| No token in logs/UI/settings; dummy tokens in tests | 1–3 (by construction) |
| Live-data acceptance and no-credentials degradation | Task 3 Step 7 (human) |
