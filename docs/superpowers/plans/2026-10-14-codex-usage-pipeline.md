# Codex Usage Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show Codex's 5-hour, 7-day and spend-control limits next to Claude's, with their own tray badges and their own section in one shared popup.

**Architecture:** A second pipeline in `Core/Codex/` that produces the *existing* `UsageSnapshot`, so severity, pacing, bars and badges are reached by instantiation rather than by modification. `TrayApp` holds two independent provider states; neither provider's failure can null, clobber or delay the other's data. Live API only — the token Codex stores is read, never written.

**Tech Stack:** .NET 10, C#, WinForms, xUnit, `System.Text.Json`, `HttpClient`.

**Spec:** [docs/superpowers/specs/2026-10-13-codex-usage-design.md](../specs/2026-10-13-codex-usage-design.md)

**Depends on:** [2026-10-14-codex-usage-prerequisites.md](2026-10-14-codex-usage-prerequisites.md) — all six tasks landed. This plan assumes `UsageNotificationRules`, `ScopedLimit.Period`, `StatusSource.BadgeFor`, nullable `StatusSourceSettings.Enabled`, `BadgeShape` and the bounded popup already exist.

## Global Constraints

- App and tests target `net10.0-windows10.0.19041.0`. Nothing else does.
- `Core/` is WinForms-free, clock-free and thread-free. Every time-dependent function takes a
  caller-supplied `DateTimeOffset now`. I/O readers belong there and already live there.
- **Nothing in a read path throws.** `CodexPath`, `CodexCredentialsReader`, `CodexUsageJson` and
  `CodexUsageApiClient` swallow IO/JSON/HTTP errors and return null.
- **The token is read-only.** Never written, never refreshed, never logged — `fetch.log` included.
- `fetch.log` carries percentages and outcomes only. The Codex payload also returns `email`,
  `user_id`, `account_id`, and spend-control `limit` / `used` / `balance`: none of these is ever
  logged or displayed.
- Absent data means no row. Never a placeholder `0 %` or `—` for a limit the account lacks.
- **No live calls to `chatgpt.com` from tests or during development.** Canned payloads only; the
  endpoint's request budget is unmeasured and shared with the user's real Codex sessions.
- Test fixtures must use placeholder identifiers (`user-TEST`, `test@example.com`, a zero GUID),
  never a real account's values.
- No linter step. Match surrounding style, including the explanatory doc comments.
- `dotnet test` output on this machine is German: grep `Bestanden!` / `Fehler:`, not `Passed!`.
- Commit messages in English. Never add a Claude/AI co-author trailer.

## Review Focus

Five conditions the spec implies that no happy path exercises. Each has its test added to the owning
task.

1. `auth.json` exists but the token's `exp` has passed, or the JWT has two segments instead of three
   — must read as "no credentials", never throw. *Task 1.*
2. A payload with `rate_limit: null`, or `spend_control` present but `individual_limit: null` — each
   absent block yields no row rather than a zero row. *Task 2.*
3. A `credits` block with `has_credits: true` and `balance: null` — must produce **no** credits row,
   because synthesising `Percent: 0` would draw a green 0 % bar. *Task 2.*
4. Two consecutive credential read failures while Codex rewrites its token — the icons must stay,
   dimmed; only the third drops them. *Task 6.*
5. A Codex toast retracted while a Claude toast is on screen — Claude's must survive. *Task 9.*

---

### Task 1: Codex paths and credentials

**Files:**
- Create: `src/ClaudeUsageTray/Core/Codex/CodexPath.cs`
- Create: `src/ClaudeUsageTray/Core/Codex/CodexCredentialsReader.cs`
- Test: `tests/ClaudeUsageTray.Tests/CodexPathTests.cs`
- Test: `tests/ClaudeUsageTray.Tests/CodexCredentialsReaderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `CodexPath.Home(string? overrideHome = null)` → `string`
  - `CodexPath.AuthFile(string? overrideHome = null)` → `string`
  - `record CodexAuth(string AccessToken, string? AccountId, DateTimeOffset? ExpiresAt)`
  - `CodexCredentialsReader.TryRead(string authFilePath, DateTimeOffset now)` → `CodexAuth?`
  - `CodexCredentialsReader.Status(string authFilePath, DateTimeOffset now)` → `CredentialStatus`

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/CodexPathTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class CodexPathTests
{
    [Fact]
    public void Home_DefaultsToDotCodexUnderTheUserProfile()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        Assert.Equal(expected, CodexPath.Home());
    }

    /// <summary>CODEX_HOME is honoured by the Codex CLI itself, so a relocated home must be followed
    /// or the tray reads a stale token from a directory Codex abandoned.</summary>
    [Fact]
    public void Home_PrefersAnExplicitOverride()
        => Assert.Equal(@"D:\codex", CodexPath.Home(@"D:\codex"));

    [Fact]
    public void Home_IgnoresABlankOverride()
        => Assert.Equal(CodexPath.Home(), CodexPath.Home("   "));

    [Fact]
    public void AuthFile_SitsInTheHome()
        => Assert.Equal(Path.Combine(@"D:\codex", "auth.json"), CodexPath.AuthFile(@"D:\codex"));
}
```

Create `tests/ClaudeUsageTray.Tests/CodexCredentialsReaderTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Read-only extraction of Codex's stored token. No live calls, no writes, and no token
/// material in any assertion message.</summary>
public class CodexCredentialsReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A three-segment JWT whose payload carries the given exp. Only the payload is read,
    /// so the header and signature are filler.</summary>
    private static string Jwt(DateTimeOffset? exp)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = exp is { } e ? $"{{\"exp\":{e.ToUnixTimeSeconds()}}}" : "{}";
        return $"{B64("{\"alg\":\"RS256\"}")}.{B64(payload)}.sig";
    }

    private static string Write(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-auth-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string AuthJson(string token, string? accountId = "00000000-0000-0000-0000-000000000000")
        => JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt",
            tokens = new { access_token = token, account_id = accountId },
        });

    [Fact]
    public void TryRead_ReturnsTheTokenAndAccount()
    {
        var path = Write(AuthJson(Jwt(Now.AddDays(2))));
        try
        {
            var auth = CodexCredentialsReader.TryRead(path, Now);
            Assert.NotNull(auth);
            Assert.Equal("00000000-0000-0000-0000-000000000000", auth!.AccountId);
            Assert.Equal(Now.AddDays(2).ToUnixTimeSeconds(), auth.ExpiresAt!.Value.ToUnixTimeSeconds());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryRead_MissingFile_IsNull()
        => Assert.Null(CodexCredentialsReader.TryRead(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json"), Now));

    [Fact]
    public void TryRead_MalformedJson_IsNull()
    {
        var path = Write("{ not json at all ");
        try { Assert.Null(CodexCredentialsReader.TryRead(path, Now)); }
        finally { File.Delete(path); }
    }

    /// <summary>Review Focus 1, first half: an expired token is a known no-data reason, so the
    /// fetch is never attempted and the 401 never happens.</summary>
    [Fact]
    public void TryRead_ExpiredToken_IsNull()
    {
        var path = Write(AuthJson(Jwt(Now.AddMinutes(-1))));
        try { Assert.Null(CodexCredentialsReader.TryRead(path, Now)); }
        finally { File.Delete(path); }
    }

    /// <summary>Within the expiry margin counts as expired: Codex is about to refresh it.</summary>
    [Fact]
    public void TryRead_TokenInsideTheExpiryMargin_IsNull()
    {
        var path = Write(AuthJson(Jwt(Now.AddMinutes(2))));
        try { Assert.Null(CodexCredentialsReader.TryRead(path, Now)); }
        finally { File.Delete(path); }
    }

    /// <summary>Review Focus 1, second half: a token that is not a three-segment JWT must not throw.
    /// An unreadable expiry is treated as unusable rather than as "valid forever".</summary>
    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("two.segments")]
    [InlineData("three.!!!notbase64!!!.sig")]
    public void TryRead_UndecodableToken_IsNull(string token)
    {
        var path = Write(AuthJson(token));
        try { Assert.Null(CodexCredentialsReader.TryRead(path, Now)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryRead_TokenWithNoExpClaim_IsNull()
    {
        var path = Write(AuthJson(Jwt(null)));
        try { Assert.Null(CodexCredentialsReader.TryRead(path, Now)); }
        finally { File.Delete(path); }
    }

    /// <summary>A missing account id is not fatal: the header is omitted and the endpoint resolves
    /// the account from the token.</summary>
    [Fact]
    public void TryRead_WithoutAccountId_StillReturnsTheToken()
    {
        var path = Write(AuthJson(Jwt(Now.AddDays(2)), accountId: null));
        try
        {
            var auth = CodexCredentialsReader.TryRead(path, Now);
            Assert.NotNull(auth);
            Assert.Null(auth!.AccountId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Status_DistinguishesMissingFromUnusable()
    {
        Assert.Equal(CredentialStatus.Missing, CodexCredentialsReader.Status(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json"), Now));

        var path = Write(AuthJson(Jwt(Now.AddMinutes(-1))));
        try { Assert.Equal(CredentialStatus.Unusable, CodexCredentialsReader.Status(path, Now)); }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CodexPathTests|FullyQualifiedName~CodexCredentialsReaderTests"`
Expected: FAIL — `CodexPath` does not exist (CS0246).

- [ ] **Step 3: Implement the path resolver**

Create `src/ClaudeUsageTray/Core/Codex/CodexPath.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>Where Codex keeps its state. Mirrors <see cref="ConfigPath.Resolve"/>, with one extra
/// rule: the Codex CLI honours CODEX_HOME, so a relocated home has to be followed or the tray reads
/// a token from a directory Codex has abandoned. The caller passes the override rather than this
/// reading the environment itself, so the resolution is testable without mutating process state.</summary>
public static class CodexPath
{
    public const string HomeVariable = "CODEX_HOME";

    public static string Home(string? overrideHome = null)
        => string.IsNullOrWhiteSpace(overrideHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : overrideHome;

    public static string AuthFile(string? overrideHome = null)
        => Path.Combine(Home(overrideHome), "auth.json");

    /// <summary>The override the app actually runs with: CODEX_HOME when the environment sets it.
    /// Separated from <see cref="Home"/> so every other caller stays pure.</summary>
    public static string? HomeFromEnvironment()
    {
        try { return Environment.GetEnvironmentVariable(HomeVariable); }
        catch (SecurityException) { return null; }
    }
}
```

Add `using System.Security;` at the top.

- [ ] **Step 4: Implement the credentials reader**

Create `src/ClaudeUsageTray/Core/Codex/CodexCredentialsReader.cs`:

```csharp
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Codex's stored token, as far as the tray needs it. ExpiresAt comes from the token's own
/// exp claim: an expiry the app can see is a known no-data reason, which beats discovering it as a
/// 401 after spending a request from an unmeasured budget.</summary>
public sealed record CodexAuth(string AccessToken, string? AccountId, DateTimeOffset? ExpiresAt);

/// <summary>
/// Read-only extraction of the Codex CLI's OAuth access token. This app NEVER writes auth.json,
/// never refreshes tokens, and never logs or persists token material — the same invariant the
/// Claude credentials path carries.
/// </summary>
public static class CodexCredentialsReader
{
    /// <summary>Margin below which a token is treated as unusable, because Codex is about to
    /// refresh it. Same five minutes as the Claude path.</summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    /// <summary>tokens.access_token when present, non-empty, and valid past the margin; else null.
    /// Never throws. A token whose expiry cannot be read is unusable rather than assumed good: the
    /// alternative is spending requests on a token that will only ever answer 401.</summary>
    public static CodexAuth? TryRead(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > 32 * 1024 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("tokens", out var tokens)
                || tokens.ValueKind != JsonValueKind.Object) return null;
            if (!tokens.TryGetProperty("access_token", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String) return null;
            var token = tokenElement.GetString();
            if (string.IsNullOrEmpty(token)) return null;

            var expires = ExpiryOf(token);
            if (expires is null || expires <= now + ExpiryMargin) return null;

            string? account = tokens.TryGetProperty("account_id", out var id)
                              && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

            return new CodexAuth(token, account, expires);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
            or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Existence plus the same validation <see cref="TryRead"/> applies. The token itself is
    /// not returned or retained.</summary>
    public static CredentialStatus Status(string path, DateTimeOffset now)
    {
        bool exists;
        try { exists = File.Exists(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { return CredentialStatus.Unusable; }
        if (!exists) return CredentialStatus.Missing;
        return TryRead(path, now) is null ? CredentialStatus.Unusable : CredentialStatus.Valid;
    }

    /// <summary>The exp claim of a three-segment JWT, or null for anything else. Decoding only — no
    /// signature check: the token is not being trusted here, merely asked when it stops working.</summary>
    private static DateTimeOffset? ExpiryOf(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("exp", out var exp)
                || exp.ValueKind != JsonValueKind.Number
                || !exp.TryGetInt64(out var seconds)) return null;
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CodexPathTests|FullyQualifiedName~CodexCredentialsReaderTests"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/ClaudeUsageTray/Core/Codex tests/ClaudeUsageTray.Tests/CodexPathTests.cs tests/ClaudeUsageTray.Tests/CodexCredentialsReaderTests.cs
git commit -m "feat(codex): read the Codex token read-only

Resolves the Codex home (CODEX_HOME aware) and extracts
tokens.access_token from auth.json, with the expiry decoded from the
token's own exp claim so an expired token is a known no-data reason
rather than a 401 against an unmeasured request budget."
```

---

### Task 2: Parse the Codex usage payload

Maps the payload onto the existing `UsageSnapshot`. Three deliberate omissions, each a direct
application of an invariant:

- **Credits produce no row.** The block carries `has_credits`, `unlimited` and `balance: null` — a
  capability flag with no measurement. `CreditUsage` requires a non-nullable `Percent` and
  `CreditSeverity` feeds it to `SeverityRules`, so a synthesised `0` would draw a green 0 % bar.
- **The windows map positionally**, `primary_window` → `FiveHour` and `secondary_window` →
  `SevenDay`. `limit_window_seconds` is not consulted: the app has exactly two window slots with
  fixed periods, and silently dropping a window whose length changed would be worse than showing it.
- **Spend control is monthly**, via `ScopedLimit.Period`.

**Files:**
- Create: `src/ClaudeUsageTray/Core/Codex/CodexUsageJson.cs`
- Test: `tests/ClaudeUsageTray.Tests/CodexUsageJsonTests.cs`

**Interfaces:**
- Consumes: `ScopedPeriod` / `ScopedLimit.Period` (prerequisites Task 2).
- Produces: `CodexUsageJson.Parse(string json, DateTimeOffset fetchedAt)` → `UsageSnapshot?`
  and `CodexUsageJson.SpendControlLabel` → `"Spend control"`.

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/CodexUsageJsonTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Canned payloads only. Identifiers are placeholders: the real response carries an email,
/// a user id and an account id, and none of them belongs in a repository.</summary>
public class CodexUsageJsonTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);

    /// <summary>The shape verified live on 2026-10-13, with identifiers replaced.</summary>
    private const string Full = """
        {
          "user_id": "user-TEST",
          "account_id": "00000000-0000-0000-0000-000000000000",
          "email": "test@example.com",
          "plan_type": "team",
          "rate_limit": {
            "allowed": true,
            "limit_reached": false,
            "primary_window":   { "used_percent": 42, "limit_window_seconds": 18000,  "reset_at": 1791932468 },
            "secondary_window": { "used_percent": 21, "limit_window_seconds": 604800, "reset_at": 1792474516 }
          },
          "credits": { "has_credits": true, "unlimited": false, "balance": null },
          "spend_control": {
            "reached": true,
            "individual_limit": {
              "source": "workspace_spend_controls", "unit": "credit",
              "limit": "5", "used": "5.573267102241516", "remaining": "0",
              "used_percent": 111, "remaining_percent": 0, "reset_at": 1793491200
            }
          }
        }
        """;

    [Fact]
    public void Parse_ReadsBothWindows()
    {
        var snapshot = CodexUsageJson.Parse(Full, Now);

        Assert.Equal(42, snapshot!.FiveHour!.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791932468), snapshot.FiveHour.ResetsAt);
        Assert.Equal(21, snapshot.SevenDay!.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792474516), snapshot.SevenDay.ResetsAt);
        Assert.Equal(UsageSource.Codex, snapshot.Source);
        Assert.Equal(Now, snapshot.FetchedAt);
    }

    [Fact]
    public void Parse_ReadsSpendControlAsAMonthlyScopedLimit()
    {
        var limit = Assert.Single(CodexUsageJson.Parse(Full, Now)!.ScopedLimits);

        Assert.Equal("Spend control", limit.Label);
        Assert.Equal(111, limit.Percent);
        Assert.Equal(ScopedPeriod.Monthly, limit.Period);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793491200), limit.ResetsAt);
        Assert.True(limit.IsActive);
        Assert.Null(limit.ModelId);
    }

    /// <summary>Review Focus 3: the credits block says the account has credits and nothing about how
    /// many. A row here would be a green 0 % bar — the placeholder the invariants forbid.</summary>
    [Fact]
    public void Parse_NeverProducesACreditsRow()
        => Assert.Null(CodexUsageJson.Parse(Full, Now)!.Credits);

    /// <summary>Review Focus 2: absent blocks yield no rows, not zero rows — and a payload with no
    /// usable figure anywhere is not a snapshot at all. Adopting an empty one would reset the
    /// freshness clock and flip CodexAvailability to Available with nothing to show.</summary>
    [Fact]
    public void Parse_WithNoRateLimit_IsNull()
        => Assert.Null(CodexUsageJson.Parse("""{ "rate_limit": null, "plan_type": "team" }""", Now));

    /// <summary>One usable window is enough to be a snapshot; the other absent block yields no row
    /// rather than a zero row. This is the case that pins "absent means no row" — the all-absent
    /// case above pins "no reading at all".</summary>
    [Fact]
    public void Parse_WithOnlyOneWindow_IsASnapshotWithOneRow()
    {
        var snapshot = CodexUsageJson.Parse("""
            { "rate_limit": { "primary_window": { "used_percent": 7, "reset_at": 1791932468 } } }
            """, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(7, snapshot!.FiveHour!.Percent);
        Assert.Null(snapshot.SevenDay);
        Assert.Empty(snapshot.ScopedLimits);
    }

    /// <summary>A reset_at outside DateTimeOffset's range must degrade, not throw:
    /// FromUnixTimeSeconds raises ArgumentOutOfRangeException, and a read path that throws takes the
    /// tray down.</summary>
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Parse_WithAnImpossibleReset_KeepsThePercentageAndDropsTheReset(long seconds)
    {
        var snapshot = CodexUsageJson.Parse($$"""
            { "rate_limit": { "primary_window": { "used_percent": 12, "reset_at": {{seconds}} } } }
            """, Now);

        Assert.Equal(12, snapshot!.FiveHour!.Percent);
        Assert.Null(snapshot.FiveHour.ResetsAt);
    }

    [Fact]
    public void Parse_WithSpendControlButNoIndividualLimit_HasNoScopedRow()
    {
        var snapshot = CodexUsageJson.Parse("""
            { "rate_limit": { "primary_window": { "used_percent": 5, "reset_at": 1791932468 } },
              "spend_control": { "reached": false, "individual_limit": null } }
            """, Now);

        Assert.Equal(5, snapshot!.FiveHour!.Percent);
        Assert.Empty(snapshot.ScopedLimits);
    }

    [Fact]
    public void Parse_WithOnlyTheSecondaryWindow_HasNoFiveHourRow()
    {
        var snapshot = CodexUsageJson.Parse("""
            { "rate_limit": { "secondary_window": { "used_percent": 9, "reset_at": 1792474516 } } }
            """, Now);

        Assert.Null(snapshot!.FiveHour);
        Assert.Equal(9, snapshot.SevenDay!.Percent);
    }

    /// <summary>A window with no reset is still a reading: the percentage is what the badge draws,
    /// and the countdown simply has nothing to say.</summary>
    [Fact]
    public void Parse_WindowWithoutAReset_KeepsThePercentage()
    {
        var snapshot = CodexUsageJson.Parse("""
            { "rate_limit": { "primary_window": { "used_percent": 33 } } }
            """, Now);

        Assert.Equal(33, snapshot!.FiveHour!.Percent);
        Assert.Null(snapshot.FiveHour.ResetsAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public void Parse_Garbage_IsNull(string json)
        => Assert.Null(CodexUsageJson.Parse(json, Now));

    /// <summary>A payload with no usable figure at all is not a snapshot: adopting it would reset
    /// the freshness clock and make the display claim data it does not have.</summary>
    [Fact]
    public void Parse_EmptyObject_IsNull()
        => Assert.Null(CodexUsageJson.Parse("{}", Now));

    /// <summary>A percentage over 100 survives: spend control is already at 111 %, and clamping it
    /// here would hide a breached cap. The renderer clamps for drawing only.</summary>
    [Fact]
    public void Parse_KeepsAPercentageAboveOneHundred()
        => Assert.Equal(111, CodexUsageJson.Parse(Full, Now)!.ScopedLimits[0].Percent);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~CodexUsageJsonTests`
Expected: FAIL — `CodexUsageJson` does not exist; `UsageSource.Codex` does not exist.

- [ ] **Step 3: Add the enum member**

In `src/ClaudeUsageTray/Core/UsageSnapshot.cs`, extend `UsageSource` and its doc comment:

```csharp
/// <summary>Which program produced the data. The cache file and the live API are both Claude
/// Code's; the distinction the user sees is Claude Code versus the Claude Desktop history. Codex is
/// a different account entirely, with its own pipeline, and is named here because the fetch log
/// reports which source a reading came from.</summary>
public enum UsageSource { ClaudeCode, DesktopHistory, Codex }
```

- [ ] **Step 4: Implement the parser**

Create `src/ClaudeUsageTray/Core/Codex/CodexUsageJson.cs`:

```csharp
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>
/// Parses `GET /backend-api/codex/usage` into the app's own <see cref="UsageSnapshot"/>, so every
/// severity, pace and drawing decision downstream is the one Claude's data already gets.
///
/// Three deliberate omissions. **Credits produce no row**: the block carries has_credits, unlimited
/// and a null balance — a capability flag, not a measurement — and CreditUsage needs a percentage,
/// so a synthesised zero would draw a green 0 % bar, which is exactly the placeholder the display
/// invariants forbid. **The windows map positionally**, primary → five-hour and secondary →
/// seven-day: limit_window_seconds is not consulted, because the app has two window slots with
/// fixed periods and dropping a window whose declared length changed would be worse than showing
/// it. **Nothing throws**: any shape that is not an object with at least one usable figure is null.
/// </summary>
public static class CodexUsageJson
{
    /// <summary>A fixed string this app supplies, not a payload label — which is what makes it safe
    /// to name in fetch.log, unlike a scoped limit's account-specific model name.</summary>
    public const string SpendControlLabel = "Spend control";

    public static UsageSnapshot? Parse(string json, DateTimeOffset fetchedAt)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            WindowUsage? five = null, seven = null;
            if (doc.RootElement.TryGetProperty("rate_limit", out var rate)
                && rate.ValueKind == JsonValueKind.Object)
            {
                five = ReadWindow(rate, "primary_window");
                seven = ReadWindow(rate, "secondary_window");
            }

            var scoped = ReadSpendControl(doc.RootElement);

            // No usable figure is not a reading: adopting it would reset the freshness clock and
            // make the display assert data it does not have.
            if (five is null && seven is null && scoped.Count == 0) return null;

            return new UsageSnapshot(fetchedAt, five, seven, scoped) { Source = UsageSource.Codex };
        }
        catch (JsonException) { return null; }
    }

    private static WindowUsage? ReadWindow(JsonElement rate, string name)
    {
        if (!rate.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
            return null;
        if (!window.TryGetProperty("used_percent", out var percent)
            || percent.ValueKind != JsonValueKind.Number
            || !percent.TryGetDouble(out var used)) return null;

        return new WindowUsage((int)Math.Round(used), ReadReset(window));
    }

    /// <summary>The workspace spend cap, as a scoped limit with its own period. Its reset is a
    /// calendar-month boundary, so Monthly rather than the default Weekly; IsActive is true because
    /// the payload only sends this block when the cap applies. The money fields — limit, used,
    /// remaining — are deliberately not read: the percentage is the whole of what this app shows,
    /// and amounts have no path into the display or the log.</summary>
    private static IReadOnlyList<ScopedLimit> ReadSpendControl(JsonElement root)
    {
        if (!root.TryGetProperty("spend_control", out var spend)
            || spend.ValueKind != JsonValueKind.Object) return [];
        if (!spend.TryGetProperty("individual_limit", out var limit)
            || limit.ValueKind != JsonValueKind.Object) return [];
        if (!limit.TryGetProperty("used_percent", out var percent)
            || percent.ValueKind != JsonValueKind.Number
            || !percent.TryGetDouble(out var used)) return [];

        return [new ScopedLimit(SpendControlLabel, null, (int)Math.Round(used), ReadReset(limit),
            IsActive: true) { Period = ScopedPeriod.Monthly }];
    }

    /// <summary>reset_at is unix seconds, unlike the Claude payload's ISO-8601 strings. TryGetInt64
    /// succeeds for any 64-bit integer, but FromUnixTimeSeconds throws outside roughly ±62 billion
    /// seconds — so an absurd value drops the reset and keeps the percentage, rather than taking the
    /// read path down.</summary>
    private static DateTimeOffset? ReadReset(JsonElement element)
    {
        if (!element.TryGetProperty("reset_at", out var reset)
            || reset.ValueKind != JsonValueKind.Number
            || !reset.TryGetInt64(out var seconds)) return null;

        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~CodexUsageJsonTests`
Expected: PASS

- [ ] **Step 6: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS. `UsageSnapshotTests` and anything switching on `UsageSource` may need the new member
handled; a `switch` that was exhaustive over two members is the likely break.

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Core/Codex/CodexUsageJson.cs src/ClaudeUsageTray/Core/UsageSnapshot.cs tests/ClaudeUsageTray.Tests/CodexUsageJsonTests.cs
git commit -m "feat(codex): parse the usage payload into UsageSnapshot

Windows map positionally onto the app's two slots; spend control becomes
a monthly scoped limit. Credits are deliberately dropped: the block
carries a capability flag and a null balance, and a synthesised 0 %
would draw the placeholder bar the display invariants forbid."
```

---

### Task 3: The Codex usage client

**Files:**
- Create: `src/ClaudeUsageTray/Core/Codex/CodexUsageApiClient.cs`
- Test: `tests/ClaudeUsageTray.Tests/CodexUsageApiClientTests.cs`

**Interfaces:**
- Consumes: `CodexAuth` (Task 1), `CodexUsageJson.Parse` (Task 2).
- Produces: `CodexUsageApiClient.FetchAsync(HttpClient, CodexAuth, DateTimeOffset, CancellationToken)`
  → `Task<UsageFetchResult>` (the existing result record) and `CodexUsageApiClient.EndpointUrl`.

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/CodexUsageApiClientTests.cs`:

```csharp
using System.Net;
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Canned responses only. The endpoint's request budget is unmeasured and shared with the
/// user's real Codex sessions, so no test ever reaches the network.</summary>
public class CodexUsageApiClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);

    private static readonly CodexAuth Auth =
        new("dummy.token.value", "00000000-0000-0000-0000-000000000000", Now.AddDays(1));

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

    private const string Ok = """
        { "rate_limit": { "primary_window":   { "used_percent": 42, "reset_at": 1791932468 },
                          "secondary_window": { "used_percent": 21, "reset_at": 1792474516 } } }
        """;

    private static (UsageFetchResult Result, FakeHandler Handler) Fetch(
        Func<HttpRequestMessage, HttpResponseMessage> respond, CodexAuth? auth = null)
    {
        var handler = new FakeHandler(respond);
        using var http = new HttpClient(handler);
        var result = CodexUsageApiClient.FetchAsync(http, auth ?? Auth, Now, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (result, handler);
    }

    [Fact]
    public void Fetch_Success_ReturnsASnapshot()
    {
        var (result, _) = Fetch(_ => Json(HttpStatusCode.OK, Ok));

        Assert.Equal(42, result.Snapshot!.FiveHour!.Percent);
        Assert.Equal(UsageSource.Codex, result.Snapshot.Source);
        Assert.False(result.Unauthorized);
        Assert.False(result.RateLimited);
    }

    [Fact]
    public void Fetch_SendsBearerTokenAndAccountHeader()
    {
        var (_, handler) = Fetch(_ => Json(HttpStatusCode.OK, Ok));
        var request = handler.LastRequest!;

        Assert.Equal(CodexUsageApiClient.EndpointUrl, request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Auth.AccessToken, request.Headers.Authorization.Parameter);
        Assert.Equal("00000000-0000-0000-0000-000000000000",
            Assert.Single(request.Headers.GetValues("ChatGPT-Account-Id")));
    }

    [Fact]
    public void Fetch_WithoutAnAccountId_OmitsTheHeader()
    {
        var (_, handler) = Fetch(_ => Json(HttpStatusCode.OK, Ok),
            auth: new CodexAuth("dummy.token.value", null, Now.AddDays(1)));

        Assert.False(handler.LastRequest!.Headers.Contains("ChatGPT-Account-Id"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void Fetch_Rejected_IsUnauthorized(HttpStatusCode status)
    {
        var (result, _) = Fetch(_ => Json(status, "{}"));

        Assert.True(result.Unauthorized);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Fetch_TooManyRequests_IsRateLimitedWithRetryAfter()
    {
        var (result, _) = Fetch(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(30));
            return response;
        });

        Assert.True(result.RateLimited);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    [Fact]
    public void Fetch_ServerError_IsAPlainFailure()
    {
        var (result, _) = Fetch(_ => Json(HttpStatusCode.InternalServerError, "{}"));

        Assert.Null(result.Snapshot);
        Assert.False(result.Unauthorized);
        Assert.False(result.RateLimited);
    }

    [Fact]
    public void Fetch_SuccessWithUnusableBody_IsAPlainFailure()
    {
        var (result, _) = Fetch(_ => Json(HttpStatusCode.OK, "{ not json"));

        Assert.Null(result.Snapshot);
        Assert.False(result.Unauthorized);
    }

    [Fact]
    public void Fetch_NetworkError_NeverThrows()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("down"));
        using var http = new HttpClient(handler);

        var result = CodexUsageApiClient.FetchAsync(http, Auth, Now, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Null(result.Snapshot);
    }

    /// <summary>auth.json is third-party input. A control character in the account id would make
    /// HttpHeaders.Add throw FormatException — the header is dropped instead, because an unusable
    /// account id should cost the header, not the whole fetch.</summary>
    [Fact]
    public void Fetch_WithAnIllegalAccountId_SendsTheRequestWithoutTheHeader()
    {
        var (result, handler) = Fetch(_ => Json(HttpStatusCode.OK, Ok),
            auth: new CodexAuth("dummy.token.value", "bad\nvalue", Now.AddDays(1)));

        Assert.False(handler.LastRequest!.Headers.Contains("ChatGPT-Account-Id"));
        Assert.NotNull(result.Snapshot);
    }

    /// <summary>And an unusable token fails the fetch rather than throwing out of it.</summary>
    [Fact]
    public void Fetch_WithAnIllegalToken_NeverThrows()
    {
        var (result, _) = Fetch(_ => Json(HttpStatusCode.OK, Ok),
            auth: new CodexAuth("bad\ntoken", null, Now.AddDays(1)));

        Assert.Null(result.Snapshot);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~CodexUsageApiClientTests`
Expected: FAIL — `CodexUsageApiClient` does not exist (CS0246).

- [ ] **Step 3: Implement the client**

Create `src/ClaudeUsageTray/Core/Codex/CodexUsageApiClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Read-only client for the Codex usage endpoint. Never throws; never logs the token.
/// Returns the same <see cref="UsageFetchResult"/> the Claude client does, so TrayApp's outcome
/// handling — adopt, back off, arm — is one shape for both providers.</summary>
public static class CodexUsageApiClient
{
    public const string EndpointUrl = "https://chatgpt.com/backend-api/codex/usage";

    private static readonly string UserAgent =
        $"ClaudeUsageTray/{typeof(CodexUsageApiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    public static async Task<UsageFetchResult> FetchAsync(
        HttpClient http, CodexAuth auth, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, EndpointUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            // Omitted rather than sent empty when absent: the endpoint resolves the account from the
            // token, and a blank header is a worse request than none. Also omitted when the value
            // would not be a legal header: auth.json is third-party input, and
            // HttpHeaders.Add throws FormatException on a control character — a throw this method
            // promises never to make.
            if (HeaderSafe(auth.AccountId))
                request.Headers.Add("ChatGPT-Account-Id", auth.AccountId);
            request.Headers.Accept.ParseAdd("application/json");
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

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new UsageFetchResult(CodexUsageJson.Parse(body, now), false, false, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
            or OperationCanceledException or JsonException or FormatException or ArgumentException)
        {
            // FormatException/ArgumentException: AuthenticationHeaderValue and Headers.Add reject
            // values with control characters, and the token comes from a file this app does not own.
            return new UsageFetchResult(null, false, false, null);
        }
    }

    /// <summary>Whether a value can legally be a header. Checked rather than caught where it is
    /// cheap to check: an unusable account id should mean "send the request without the header",
    /// not "fail the fetch".</summary>
    private static bool HeaderSafe(string? value)
        => !string.IsNullOrEmpty(value) && !value.Any(c => char.IsControl(c));
}
```

Add `using System.Linq;` for `Any`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~CodexUsageApiClientTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/Codex/CodexUsageApiClient.cs tests/ClaudeUsageTray.Tests/CodexUsageApiClientTests.cs
git commit -m "feat(codex): add the usage endpoint client

Returns the same UsageFetchResult the Claude client does, so one outcome
shape covers both providers. Canned-response tests only: the endpoint's
budget is unmeasured and shared with real Codex sessions."
```

---

### Task 4: Codex display selection

Codex has one source, so there is no precedence contest — but staleness still has to be decided
somewhere pure, and `SourceSelection.Choose` takes two Claude-specific slots and two Claude-specific
allowances.

**Files:**
- Create: `src/ClaudeUsageTray/Core/Codex/CodexDisplay.cs`
- Test: `tests/ClaudeUsageTray.Tests/CodexDisplayTests.cs`

**Interfaces:**
- Consumes: `SourceSelection.Age`, `DisplayChoice`.
- Produces: `CodexDisplay.Choose(UsageSnapshot?, DateTimeOffset, Settings)` → `DisplayChoice`.

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/CodexDisplayTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class CodexDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot At(DateTimeOffset when)
        => new(when, new WindowUsage(42, when.AddHours(3)), null) { Source = UsageSource.Codex };

    private static Settings Settings30 => new() { StalenessMinutes = 30 };

    [Fact]
    public void Choose_WithNoSnapshot_IsEmptyAndNotStale()
    {
        var choice = CodexDisplay.Choose(null, Now, Settings30);

        Assert.Null(choice.Snapshot);
        Assert.False(choice.Stale);
    }

    [Fact]
    public void Choose_FreshSnapshot_IsNotStale()
        => Assert.False(CodexDisplay.Choose(At(Now.AddMinutes(-5)), Now, Settings30).Stale);

    [Fact]
    public void Choose_AtTheAllowance_IsNotStale()
        => Assert.False(CodexDisplay.Choose(At(Now.AddMinutes(-30)), Now, Settings30).Stale);

    /// <summary>Past the allowance it degrades to stale, never to blank: the snapshot is still the
    /// best thing known, and blanking a badge the user is watching is worse than dimming it.</summary>
    [Fact]
    public void Choose_PastTheAllowance_IsStaleButStillShown()
    {
        var choice = CodexDisplay.Choose(At(Now.AddMinutes(-31)), Now, Settings30);

        Assert.NotNull(choice.Snapshot);
        Assert.True(choice.Stale);
    }

    /// <summary>A timestamp implausibly far in the future fails every freshness test rather than
    /// reading as brand new — the same clamp SourceSelection.Age applies to Claude's slots.</summary>
    [Fact]
    public void Choose_FutureSnapshot_IsStale()
        => Assert.True(CodexDisplay.Choose(At(Now.AddHours(1)), Now, Settings30).Stale);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~CodexDisplayTests`
Expected: FAIL — `CodexDisplay` does not exist (CS0246).

- [ ] **Step 3: Implement it**

Create `src/ClaudeUsageTray/Core/Codex/CodexDisplay.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>What the Codex icons and the Codex popup section show. Codex has a single source, so
/// there is no precedence contest to run — but staleness is still a decision, and it belongs here
/// rather than in the UI. Reuses <see cref="SourceSelection.Age"/> so clock skew is clamped the same
/// way it is for Claude's slots, and shares Claude's StalenessMinutes allowance: both are live API
/// readings on a minutes cadence, unlike the Desktop history's hours.</summary>
public static class CodexDisplay
{
    public static DisplayChoice Choose(UsageSnapshot? snapshot, DateTimeOffset now, Settings settings)
    {
        if (snapshot is null) return new(null, false);
        var stale = SourceSelection.Age(snapshot, now) > TimeSpan.FromMinutes(settings.StalenessMinutes);
        return new(snapshot, stale);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~CodexDisplayTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/Codex/CodexDisplay.cs tests/ClaudeUsageTray.Tests/CodexDisplayTests.cs
git commit -m "feat(codex): decide Codex staleness in Core

One source means no precedence contest, but staleness is still a
decision and belongs out of the UI. Reuses SourceSelection.Age so clock
skew is clamped exactly as it is for Claude's slots."
```

---

### Task 5: Codex settings keys

**Files:**
- Modify: `src/ClaudeUsageTray/Core/Settings.cs` (add two properties; remove the
  `SecondaryNotificationsForTest` placeholder added in prerequisites Task 1)
- Test: `tests/ClaudeUsageTray.Tests/SettingsTests.cs`
- Test: `tests/ClaudeUsageTray.Tests/NotificationRulesUsageTests.cs` (retarget the placeholder)

**Interfaces:**
- Consumes: `UsageNotificationSettings`, `TolerantUsageNotificationsConverter`.
- Produces: `Settings.CodexDisplayMode` → `DisplayMode` (default `Both`);
  `Settings.CodexNotifications` → `UsageNotificationSettings`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/ClaudeUsageTray.Tests/SettingsTests.cs`:

```csharp
    [Fact]
    public void CodexDisplayMode_DefaultsToBoth()
        => Assert.Equal(DisplayMode.Both, new Settings().CodexDisplayMode);

    [Fact]
    public void CodexNotifications_DefaultToOnAtRed()
    {
        var settings = new Settings();
        Assert.True(settings.CodexNotifications.Enabled);
        Assert.Equal(NotifyLevel.Red, settings.CodexNotifications.Level);
    }

    [Fact]
    public void Load_ReadsTheCodexKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                { "codexDisplayMode": "fiveHour",
                  "codexNotifications": { "enabled": false, "level": "orange" } }
                """);

            var settings = Settings.Load(path);
            Assert.Equal(DisplayMode.FiveHour, settings.CodexDisplayMode);
            Assert.False(settings.CodexNotifications.Enabled);
            Assert.Equal(NotifyLevel.Orange, settings.CodexNotifications.Level);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The tolerant converter applies here too: a typo'd level resets that level alone and
    /// must not take the Claude block, the thresholds or the display mode down with it.</summary>
    [Fact]
    public void Load_WithABadCodexLevel_KeepsEverythingElse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                { "thresholds": { "orange": 55, "red": 85 },
                  "codexNotifications": { "enabled": false, "level": "purple" } }
                """);

            var settings = Settings.Load(path);
            Assert.Equal(55, settings.Thresholds.Orange);
            Assert.False(settings.CodexNotifications.Enabled);
            Assert.Equal(NotifyLevel.Red, settings.CodexNotifications.Level);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Round-trips, so a dialog save does not silently drop a key the dialog does not own.</summary>
    [Fact]
    public void Save_RoundTripsTheCodexKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new Settings
            {
                CodexDisplayMode = DisplayMode.SevenDay,
                CodexNotifications = new UsageNotificationSettings { Enabled = false, Level = NotifyLevel.Orange },
            };
            settings.Save(path);

            var again = Settings.Load(path);
            Assert.Equal(DisplayMode.SevenDay, again.CodexDisplayMode);
            Assert.False(again.CodexNotifications.Enabled);
            Assert.Equal(NotifyLevel.Orange, again.CodexNotifications.Level);
        }
        finally { File.Delete(path); }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SettingsTests`
Expected: FAIL — `CodexDisplayMode` is not a member of `Settings`.

- [ ] **Step 3: Add the properties**

In `src/ClaudeUsageTray/Core/Settings.cs`, next to `DisplayMode` and `UsageNotifications`:

```csharp
    /// <summary>Which Codex badges to show, independent of Claude's. Separate rather than shared so
    /// a crowded tray can carry Claude's pair and only Codex's five-hour badge.</summary>
    public DisplayMode CodexDisplayMode { get; set; } = DisplayMode.Both;
```

```csharp
    /// <summary>Codex's own notification switch and level. Its own block, not Claude's: badging
    /// Codex silently while Claude still toasts is a reasonable thing to want, and the rules object
    /// is per-provider precisely so this can differ.</summary>
    [JsonConverter(typeof(TolerantUsageNotificationsConverter))]
    public UsageNotificationSettings CodexNotifications { get; set; } = new();
```

Match how `UsageNotifications` declares its converter — if it is applied on the property, do the
same; if it is registered in the serializer options, register this one there too.

**Both new keys must be normalized, or a legal settings file crashes `Render()`.** `NormalizeFields`
guards the Claude block at `Settings.cs:238-239`, and that guard is not decorative: for a
reference-typed property, `System.Text.Json` assigns `null` for a JSON `null` *without* invoking the
tolerant converter. So `"codexNotifications": null` survives `Load` as a null, and Task 9's selector
`s => s.CodexNotifications` dereferences it inside `UsageNotificationRules.OnUsage` — a
`NullReferenceException` raised from a read path, which the invariants forbid. Add beside the
existing guard:

```csharp
        CodexNotifications ??= new UsageNotificationSettings();
        if (!Enum.IsDefined(CodexNotifications.Level)) CodexNotifications.Level = NotifyLevel.Red;
        if (!Enum.IsDefined(CodexDisplayMode)) CodexDisplayMode = DisplayMode.Both;
```

The `CodexDisplayMode` line matters for a different reason: an out-of-range *number* would otherwise
survive, and an unparseable *string* throws `JsonException`, which `Settings.Load` answers with full
defaults — resetting thresholds and the Claude display mode because of a typo in a Codex key. Add a
test for `"codexNotifications": null` and one for a bad `codexDisplayMode`, both asserting the
unrelated settings survive.

- [ ] **Step 4: Retire the test placeholder**

Delete `SecondaryNotificationsForTest` from `Settings`, and in
`tests/ClaudeUsageTray.Tests/NotificationRulesUsageTests.cs` point
`OnUsage_ReadsTheProvidersOwnNotificationSettings` at the real thing: `CodexNotifications` instead of
`SecondaryNotificationsForTest`, and `s => s.CodexNotifications` as the selector.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SettingsTests|FullyQualifiedName~NotificationRulesUsageTests"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/ClaudeUsageTray/Core/Settings.cs tests/ClaudeUsageTray.Tests/SettingsTests.cs tests/ClaudeUsageTray.Tests/NotificationRulesUsageTests.cs
git commit -m "feat(codex): add codexDisplayMode and codexNotifications

Both mirror their Claude counterparts, so tolerant parsing comes for
free: a typo'd Codex level resets that level alone. Replaces the
placeholder settings block the per-provider rules test was using."
```

---

### Task 6: The Codex fetch loop and availability

The spec's availability rule is the part most likely to be built wrong, so it is specified as a state
machine with named recompute events.

`FetchScheduler` gains an optional floor so Codex can run at half Claude's cadence off the same
timer: the budget cap alone would let six fetches happen in the first half hour and then none, which
is not the same thing as one per ten minutes.

**Files:**
- Modify: `src/ClaudeUsageTray/Core/FetchScheduler.cs:11-28`
- Create: `src/ClaudeUsageTray/Core/Codex/CodexAvailability.cs`
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs` (fields, constructor, poll tick, new fetch methods)
- Test: `tests/ClaudeUsageTray.Tests/FetchSchedulerTests.cs`
- Test: `tests/ClaudeUsageTray.Tests/CodexAvailabilityTests.cs`

**Interfaces:**
- Consumes: `CodexCredentialsReader`, `CodexUsageApiClient`, `CodexDisplay`, `CodexPath`.
- Produces:
  - `FetchScheduler(int maxPerHour = 20, TimeSpan? floor = null)`
  - `enum CodexState { Unknown, Available, Unavailable }`
  - `CodexAvailability` with `State`, `NoteSnapshot()`, `NoteCredentialsMissing()`,
    `NoteCredentialsUnreadable()`, `NoteFetchFailure()`

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/CodexAvailabilityTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Whether the Codex badges exist at all. The tray changing shape is the most visible thing
/// this feature does, so every transition is pinned.</summary>
public class CodexAvailabilityTests
{
    [Fact]
    public void StartsUnknown()
        => Assert.Equal(CodexState.Unknown, new CodexAvailability().State);

    [Fact]
    public void ASnapshotMakesItAvailable()
    {
        var availability = new CodexAvailability();
        availability.NoteSnapshot();
        Assert.Equal(CodexState.Available, availability.State);
    }

    /// <summary>An absent auth.json is a durable answer: Codex is not installed or is logged out.</summary>
    [Fact]
    public void MissingCredentialsAreImmediatelyUnavailable()
    {
        var availability = new CodexAvailability();
        availability.NoteCredentialsMissing();
        Assert.Equal(CodexState.Unavailable, availability.State);
    }

    /// <summary>Review Focus 4: CodexCredentialsReader returns null for any IO error, including the
    /// transient lock while Codex rewrites the token. Two failures must not change the tray's shape.</summary>
    [Fact]
    public void TwoUnreadableAttemptsKeepItAvailable()
    {
        var availability = new CodexAvailability();
        availability.NoteSnapshot();

        availability.NoteCredentialsUnreadable();
        availability.NoteCredentialsUnreadable();

        Assert.Equal(CodexState.Available, availability.State);
    }

    [Fact]
    public void ThreeUnreadableAttemptsGiveUp()
    {
        var availability = new CodexAvailability();
        availability.NoteSnapshot();

        availability.NoteCredentialsUnreadable();
        availability.NoteCredentialsUnreadable();
        availability.NoteCredentialsUnreadable();

        Assert.Equal(CodexState.Unavailable, availability.State);
    }

    [Fact]
    public void AGoodReadResetsTheStreak()
    {
        var availability = new CodexAvailability();
        availability.NoteSnapshot();

        availability.NoteCredentialsUnreadable();
        availability.NoteCredentialsUnreadable();
        availability.NoteSnapshot();
        availability.NoteCredentialsUnreadable();

        Assert.Equal(CodexState.Available, availability.State);
    }

    /// <summary>A 401, a 429 or a dead network dims the icons; it never removes them. The
    /// credentials are still there — the request failed, which is a different claim.</summary>
    [Fact]
    public void FetchFailuresNeverRemoveTheIcons()
    {
        var availability = new CodexAvailability();
        availability.NoteSnapshot();

        for (int i = 0; i < 10; i++) availability.NoteFetchFailure();

        Assert.Equal(CodexState.Available, availability.State);
    }

    /// <summary>Recovery is possible: a user who logs back in gets the badges back without a
    /// restart.</summary>
    [Fact]
    public void ASnapshotAfterUnavailableRecovers()
    {
        var availability = new CodexAvailability();
        availability.NoteCredentialsMissing();
        availability.NoteSnapshot();

        Assert.Equal(CodexState.Available, availability.State);
    }
}
```

Add to `tests/ClaudeUsageTray.Tests/FetchSchedulerTests.cs`:

```csharp
    /// <summary>Codex polls at half Claude's rate off the same timer, so the floor has to be
    /// configurable: a 6/h budget alone would allow six fetches in the first half hour and then
    /// none, which is not one fetch per ten minutes.</summary>
    [Fact]
    public void CanFetch_HonoursAConfiguredFloor()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var scheduler = new FetchScheduler(maxPerHour: 6, floor: TimeSpan.FromMinutes(10));

        Assert.True(scheduler.CanFetch(now));
        scheduler.RecordAttempt(now);

        Assert.False(scheduler.CanFetch(now.AddMinutes(5)));
        Assert.True(scheduler.CanFetch(now.AddMinutes(10)));
    }

    /// <summary>The default floor is unchanged for every existing caller.</summary>
    [Fact]
    public void CanFetch_DefaultFloorIsThirtySeconds()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var scheduler = new FetchScheduler();

        scheduler.RecordAttempt(now);
        Assert.False(scheduler.CanFetch(now.AddSeconds(29)));
        Assert.True(scheduler.CanFetch(now.AddSeconds(30)));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CodexAvailabilityTests|FullyQualifiedName~FetchSchedulerTests"`
Expected: FAIL — `CodexAvailability` does not exist; `FetchScheduler` has no `floor` parameter.

- [ ] **Step 3: Make the scheduler's floor configurable**

In `src/ClaudeUsageTray/Core/FetchScheduler.cs`, replace the static `Floor` with an instance field:

```csharp
    private static readonly TimeSpan DefaultFloor = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _floor;
```

```csharp
    /// <summary>Default 20/h with a 30 s floor: a safety margin under the Claude endpoint's measured
    /// ~28-30/h per-token budget. A caller against an *unmeasured* budget should pass a slower floor
    /// rather than inherit a number tuned for a different endpoint.</summary>
    public FetchScheduler(int maxPerHour = 20, TimeSpan? floor = null)
    {
        _maxPerHour = maxPerHour;
        _floor = floor ?? DefaultFloor;
    }
```

Replace every use of `Floor` in `CanFetch` and `RecordAttempt` with `_floor`. Read the file first:
there are two or three, and one of them is inside the rate-limit backoff computation.

- [ ] **Step 4: Implement the availability state machine**

Create `src/ClaudeUsageTray/Core/Codex/CodexAvailability.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>Whether the Codex badges exist at all.</summary>
public enum CodexState { Unknown, Available, Unavailable }

/// <summary>
/// The Codex pair's existence, as a state machine rather than as conditions scattered through the
/// UI — the tray changing shape is the most visible thing this feature does.
///
/// The asymmetry is deliberate. Becoming available takes one snapshot; becoming unavailable takes a
/// *durable* signal, because <see cref="CodexCredentialsReader"/> returns null for any IO error,
/// including the transient lock while Codex itself rewrites the token. Without the streak the tray
/// would change shape during a routine token refresh. A failed *fetch* never removes anything: the
/// credentials are still there, the request merely failed, and that degrades to stale.
/// </summary>
public sealed class CodexAvailability
{
    /// <summary>Consecutive unreadable credential reads before giving up. Three poll cycles is far
    /// longer than any token rewrite and still short enough that a real logout is noticed.</summary>
    public const int FailuresBeforeUnavailable = 3;

    private int _unreadable;

    public CodexState State { get; private set; } = CodexState.Unknown;

    /// <summary>A usable reading arrived: available, and the failure streak is forgotten. Recovers
    /// from Unavailable, so logging back in restores the badges without a restart.</summary>
    public void NoteSnapshot()
    {
        _unreadable = 0;
        State = CodexState.Available;
    }

    /// <summary>auth.json is not there. Codex is not installed or is logged out — a durable answer,
    /// no streak required.</summary>
    public void NoteCredentialsMissing()
    {
        _unreadable = 0;
        State = CodexState.Unavailable;
    }

    /// <summary>auth.json is there but yielded nothing usable: expired, malformed, or locked.</summary>
    public void NoteCredentialsUnreadable()
    {
        if (++_unreadable >= FailuresBeforeUnavailable) State = CodexState.Unavailable;
    }

    /// <summary>A 401, a 429, or a dead network. Never changes the state: this is what dimming is
    /// for.</summary>
    public void NoteFetchFailure() { }
}
```

- [ ] **Step 5: Wire the fetch loop into TrayApp**

In `src/ClaudeUsageTray/Tray/TrayApp.cs`, add beside the Claude fetch state:

```csharp
    // Codex usage polling. Fully independent of the Claude path: its own scheduler, its own
    // single-flight flag, its own last-known-good snapshot. One provider's failure must never null,
    // clobber or delay the other's data. Driven by the same 5 min _poll tick, gated to 10 min by the
    // scheduler's floor — Claude's 20/h was tuned against a measured ceiling this endpoint does not
    // share, so Codex starts at half the rate and is revisited only if observed 429s justify it.
    private readonly FetchScheduler _codexScheduler = new(maxPerHour: 6, floor: TimeSpan.FromMinutes(10));
    private readonly CodexAvailability _codexAvailability = new();
    private readonly string _codexAuthPath = CodexPath.AuthFile(CodexPath.HomeFromEnvironment());
    private UsageSnapshot? _codexSnapshot;
    private bool _codexFetchInFlight;
    private string? _codexRejectedToken;
    private string _codexFetchStatus = "no fetch yet";
```

In the constructor, extend the poll tick and the initial kick:

```csharp
        _poll.Tick += (_, _) => { StartApiFetch(); StartCodexFetch(); };
```
```csharp
        StartApiFetch();
        StartCodexFetch();
```

Add the two methods beside their Claude counterparts:

```csharp
    private void StartCodexFetch()
    {
        var now = DateTimeOffset.UtcNow;
        if (_codexFetchInFlight) return;
        if (!_codexScheduler.CanFetch(now)) return;   // silent: the 10 min floor skips most ticks

        var auth = CodexCredentialsReader.TryRead(_codexAuthPath, now);
        if (auth is null)
        {
            var status = CodexCredentialsReader.Status(_codexAuthPath, now);
            if (status == CredentialStatus.Missing)
            {
                _codexAvailability.NoteCredentialsMissing();
                _codexFetchStatus = "no Codex credentials";
            }
            else
            {
                _codexAvailability.NoteCredentialsUnreadable();
                _codexFetchStatus = "Codex credentials unusable";
            }
            ApplyDisplayMode();
            Render();
            return;
        }

        if (auth.AccessToken == _codexRejectedToken) return;

        _codexFetchInFlight = true;
        _codexScheduler.RecordAttempt(now);
        _log.Write(now, "codex: attempt: GET codex/usage");
        _ = Task.Run(async () =>
        {
            var result = await CodexUsageApiClient.FetchAsync(Http, auth, DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);
            try { _sync.BeginInvoke((Action)(() => OnCodexFetchCompleted(result, auth.AccessToken))); }
            catch (InvalidOperationException) { /* app shutting down */ }
        });
    }

    private void OnCodexFetchCompleted(UsageFetchResult result, string token)
    {
        _codexFetchInFlight = false;
        var now = DateTimeOffset.UtcNow;

        if (result.Snapshot is not null)
        {
            _codexScheduler.RecordSuccess();
            _codexRejectedToken = null;
            _codexSnapshot = result.Snapshot;
            _codexAvailability.NoteSnapshot();

            string five = result.Snapshot.FiveHour?.Percent.ToString() ?? "-";
            string seven = result.Snapshot.SevenDay?.Percent.ToString() ?? "-";
            // Percentages only. The payload also carries an email, a user id, an account id and the
            // spend cap's money amounts; none of them is ever written here.
            string spend = result.Snapshot.ScopedLimits
                .FirstOrDefault(l => l.Label == CodexUsageJson.SpendControlLabel) is { } s
                ? $" spend={s.Percent}%" : "";
            _codexFetchStatus = $"live · 5h={five}% 7d={seven}%";
            _log.Write(now, $"codex: 200 ok: 5h={five}% 7d={seven}%{spend}");
        }
        else if (result.Unauthorized)
        {
            _codexRejectedToken = token;
            _codexAvailability.NoteFetchFailure();
            _codexFetchStatus = "Codex token rejected (401/403)";
            _log.Write(now, "codex: 401/403 unauthorized: token rejected; waiting for Codex to refresh it");
        }
        else if (result.RateLimited)
        {
            _codexScheduler.RecordRateLimited(now, result.RetryAfter);
            _codexAvailability.NoteFetchFailure();
            string ra = result.RetryAfter is { } r ? $"{(int)r.TotalSeconds}s" : "none";
            _codexFetchStatus = "Codex rate-limited (429)";
            _log.Write(now, $"codex: 429 rate-limited: retry-after={ra}");
        }
        else
        {
            _codexScheduler.RecordFailure(now);
            _codexAvailability.NoteFetchFailure();
            _codexFetchStatus = "Codex network/other error";
            _log.Write(now, "codex: network/other error: no response");
        }

        ApplyDisplayMode();
        Render();
    }
```

Add `using System.Linq;` if the file does not already have it.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CodexAvailabilityTests|FullyQualifiedName~FetchSchedulerTests"`
Expected: PASS

- [ ] **Step 7: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS. `ApplyDisplayMode` and `Render` are called from new places; `SmokeTests` is the one
most likely to catch a null-reference in a path that now runs before the first fetch.

- [ ] **Step 8: Commit**

```bash
git add src/ClaudeUsageTray/Core/FetchScheduler.cs src/ClaudeUsageTray/Core/Codex/CodexAvailability.cs src/ClaudeUsageTray/Tray/TrayApp.cs tests/ClaudeUsageTray.Tests/CodexAvailabilityTests.cs tests/ClaudeUsageTray.Tests/FetchSchedulerTests.cs
git commit -m "feat(codex): poll the Codex endpoint independently

Own scheduler, own single-flight flag, own snapshot slot, driven by the
existing 5 min tick and gated to 10 min by a configurable floor —
Claude's 20/h was tuned against a measured ceiling this endpoint does
not share. Availability is a state machine: one snapshot to appear,
three consecutive unreadable credential reads to disappear, and a failed
fetch never changes the tray's shape."
```

---

### Task 7: Codex tray icons

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs` (`ApplyDisplayMode`, `Render`, `Apply`, dispose paths)
- Test: `tests/ClaudeUsageTray.Tests/CodexIconTests.cs` (create)

**Interfaces:**
- Consumes: `BadgeShape` (prerequisites Task 5), `CodexState` (Task 6), `Settings.CodexDisplayMode`
  (Task 5), `StatusMonitor.BadgeDegraded(UsageProvider)` (prerequisites Task 3).
- Produces: no new public API.

- [ ] **Step 1: Write the failing test**

The icon wiring lives in `TrayApp`, which tests do not construct. Pin the decision instead, by
extracting it: a pure function that says which icons should exist.

Create `tests/ClaudeUsageTray.Tests/CodexIconTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class CodexIconTests
{
    [Theory]
    [InlineData(CodexState.Unknown, DisplayMode.Both, false, false)]
    [InlineData(CodexState.Unavailable, DisplayMode.Both, false, false)]
    [InlineData(CodexState.Available, DisplayMode.Both, true, true)]
    [InlineData(CodexState.Available, DisplayMode.FiveHour, true, false)]
    [InlineData(CodexState.Available, DisplayMode.SevenDay, false, true)]
    public void WantedIcons_RequireBothAvailabilityAndTheDisplayMode(
        CodexState state, DisplayMode mode, bool five, bool seven)
    {
        var wanted = IconPresence.ForCodex(state, mode);

        Assert.Equal(five, wanted.Five);
        Assert.Equal(seven, wanted.Seven);
    }

    /// <summary>Claude's pair does not depend on availability: its icons exist from startup and show
    /// the no-data badge until data arrives. Only the second provider appears and disappears.</summary>
    [Theory]
    [InlineData(DisplayMode.Both, true, true)]
    [InlineData(DisplayMode.FiveHour, true, false)]
    [InlineData(DisplayMode.SevenDay, false, true)]
    public void ClaudeIcons_FollowTheDisplayModeAlone(DisplayMode mode, bool five, bool seven)
    {
        var wanted = IconPresence.ForClaude(mode);

        Assert.Equal(five, wanted.Five);
        Assert.Equal(seven, wanted.Seven);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~CodexIconTests`
Expected: FAIL — `IconPresence` does not exist (CS0246).

- [ ] **Step 3: Extract the decision into Core**

Create `src/ClaudeUsageTray/Core/IconPresence.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>Which of a provider's two badges should exist.</summary>
public sealed record WantedIcons(bool Five, bool Seven);

/// <summary>Which tray icons each provider contributes. A pure function rather than two conditions
/// inside ApplyDisplayMode, because "the Codex badges appear only once there is data" is a rule worth
/// a test, and TrayApp is not constructible in one.</summary>
public static class IconPresence
{
    /// <summary>Claude's pair follows the display mode alone: the icons exist from startup and carry
    /// the no-data badge until a reading arrives. Claude is the app's reason to be in the tray, and
    /// an empty tray would read as a crash.</summary>
    public static WantedIcons ForClaude(DisplayMode mode)
        => new(mode is DisplayMode.FiveHour or DisplayMode.Both,
               mode is DisplayMode.SevenDay or DisplayMode.Both);

    /// <summary>Codex's pair needs data as well as the setting. There is no enable toggle, so
    /// presence is the signal — and a badge for a tool that is not installed would be noise.</summary>
    public static WantedIcons ForCodex(CodexState state, DisplayMode mode)
        => state != CodexState.Available ? new(false, false) : ForClaude(mode);
}
```

- [ ] **Step 4: Use it for both providers in TrayApp**

Replace `ApplyDisplayMode` in `src/ClaudeUsageTray/Tray/TrayApp.cs`:

```csharp
    private void ApplyDisplayMode()
    {
        var claude = IconPresence.ForClaude(_settings.DisplayMode);
        var codex = IconPresence.ForCodex(_codexAvailability.State, _settings.CodexDisplayMode);

        // Best effort only: Windows decides notification-area order and overflow placement. That is
        // also why the provider is encoded in the badge's silhouette rather than in its position.
        if (!claude.Five) DisposeNotifyIcon(ref _iconFive);
        if (!claude.Seven) DisposeNotifyIcon(ref _iconSeven);
        if (!codex.Five) DisposeNotifyIcon(ref _codexIconFive);
        if (!codex.Seven) DisposeNotifyIcon(ref _codexIconSeven);

        if (claude.Five && _iconFive is null) _iconFive = CreateIcon();
        if (claude.Seven && _iconSeven is null) _iconSeven = CreateIcon();
        if (codex.Five && _codexIconFive is null) _codexIconFive = CreateIcon();
        if (codex.Seven && _codexIconSeven is null) _codexIconSeven = CreateIcon();
    }
```

Declare the two new fields beside `_iconFive` / `_iconSeven`, and add them to **every** place those
two are disposed — the dispose path, the exit path, and the update-and-restart path. Read
`TrayApp.cs` for `DisposeNotifyIcon(ref _iconFive)` and extend each occurrence; a missed one is a
ghost icon left in the tray after exit.

- [ ] **Step 5: Give `Apply` a shape and a provider**

`Apply` currently hardcodes the disc, takes Claude's degraded flag, reads the Claude-specific
`_noDataText` field directly (`TrayApp.cs:381-383`), and calls `WithStatus(text, now)` at two sites
(`TrayApp.cs:383` and `:395`). Prerequisites Task 3 made `WithStatus` require a provider, so all four
of those have to change together. The full replacement:

```csharp
    private void Apply(NotifyIcon icon, char digit, DisplayChoice choice, WindowUsage? usage, string label,
        TimeSpan period, bool clockwise, bool degraded, DateTimeOffset now,
        UsageProvider provider, BadgeShape shape, string noDataFallback)
    {
        int size = IconRenderer.SystemTrayIconSize();
        var old = icon.Icon;

        if (usage is null)
        {
            icon.Icon = IconRenderer.RenderNeutral(size, warning: degraded, shape: shape);
            // A snapshot can exist and still be missing just this window -- that is a per-window
            // gap, not the general no-data sentence. The general sentence is the caller's, because
            // Claude's reason ("no credentials file") is not Codex's.
            string noDataText = choice.Snapshot is not null ? $"{label}: no data" : noDataFallback;
            icon.Text = WithStatus(noDataText, now, provider);
        }
        else
        {
            var elapsed = TimeMarker.ElapsedFraction(ResetPresentation.EffectiveResetsAt(usage, now), period, now);
            var severity = UsageValues.WindowSeverity(usage, period, _settings, now);
            icon.Icon = IconRenderer.Render(digit, usage.Percent, severity, clockwise,
                dimmed: choice.Stale, size, warning: degraded, shape: shape);
            var tooltip = UsageTooltip.Build(label, usage, elapsed, _settings.PaceColors,
                _settings.Thresholds.Red, choice.Stale, choice.Snapshot?.Source, choice.Snapshot?.FetchedAt, now);
            icon.Text = WithStatus(tooltip, now, provider);
        }
        old?.Dispose();
    }
```

Keep the existing doc comments on the branches; only the marked lines change.

Then in `Render`:

```csharp
        var codexChoice = CodexDisplay.Choose(_codexSnapshot, now, _settings);
        bool claudeDegraded = _statusMonitor.BadgeDegraded(UsageProvider.Claude);
        bool codexDegraded = _statusMonitor.BadgeDegraded(UsageProvider.Codex);

        string claudeNoData = _noDataText ?? NoDataReason.Default;

        if (_iconFive is not null)
            Apply(_iconFive, '5', choice, choice.Snapshot?.FiveHour, "5h", UsageValues.FiveHourPeriod,
                clockwise: true, claudeDegraded, now, UsageProvider.Claude, BadgeShape.Disc, claudeNoData);
        if (_iconSeven is not null)
            Apply(_iconSeven, '7', choice, choice.Snapshot?.SevenDay, "7d", UsageValues.SevenDayPeriod,
                clockwise: false, claudeDegraded, now, UsageProvider.Claude, BadgeShape.Disc, claudeNoData);

        // Codex tooltips are prefixed, because four badges in a tray need to say which account they
        // are about — and they carry OpenAI's outage suffix, never Claude's.
        if (_codexIconFive is not null)
            Apply(_codexIconFive, '5', codexChoice, codexChoice.Snapshot?.FiveHour, "Codex 5h",
                UsageValues.FiveHourPeriod, clockwise: true, codexDegraded, now,
                UsageProvider.Codex, BadgeShape.RoundedSquare, _codexFetchStatus);
        if (_codexIconSeven is not null)
            Apply(_codexIconSeven, '7', codexChoice, codexChoice.Snapshot?.SevenDay, "Codex 7d",
                UsageValues.SevenDayPeriod, clockwise: false, codexDegraded, now,
                UsageProvider.Codex, BadgeShape.RoundedSquare, _codexFetchStatus);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~CodexIconTests`
Expected: PASS

- [ ] **Step 7: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add src/ClaudeUsageTray/Core/IconPresence.cs src/ClaudeUsageTray/Tray/TrayApp.cs tests/ClaudeUsageTray.Tests/CodexIconTests.cs
git commit -m "feat(codex): add the Codex tray badges

Rounded-square silhouette, own DisplayMode, present only once a snapshot
exists. Which icons exist is a pure function in Core so the rule is
testable without constructing TrayApp. Codex badges carry OpenAI's
outage marker and tooltip suffix, never Claude's."
```

---

### Task 8: The shared popup

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/UsagePopup.cs` (extract the per-provider block, add the section)
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs` (`ShowPopup` passes the Codex choice)
- Test: `tests/ClaudeUsageTray.Tests/UsagePopupCodexTests.cs` (create)

**Interfaces:**
- Consumes: `CodexDisplay.Choose` (Task 4).
- Produces: `UsagePopup(DisplayChoice claude, Settings, DateTimeOffset, IReadOnlyList<SourceView>?
  statusSources = null, string? lastFetchStatus = null, string? noDataText = null,
  DisplayChoice? codex = null, string? codexFetchStatus = null, int? maxHeight = null)`.

**Parameter order matters here.** Prerequisites Task 6 added `maxHeight` as the last parameter; the
two Codex parameters go *before* it, so that `UsagePopupHeightTests`' existing
`maxHeight: 200` named arguments keep working and nothing is passed positionally into the wrong slot.
Every new argument stays optional, so existing construction sites and tests are untouched.

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/UsagePopupCodexTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>CreateControl, never Show: OnDeactivate calls Close, and with no message loop Show
/// disposes the form and leaves a blank render with no controls and no error.</summary>
public class UsagePopupCodexTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static DisplayChoice Claude()
        => new(new UsageSnapshot(Now, new WindowUsage(42, Now.AddHours(3)),
            new WindowUsage(21, Now.AddDays(4))), Stale: false);

    private static DisplayChoice Codex()
        => new(new UsageSnapshot(Now, new WindowUsage(55, Now.AddHours(2)),
            new WindowUsage(33, Now.AddDays(5)),
            [new ScopedLimit("Spend control", null, 111, Now.AddDays(19), true)
                { Period = ScopedPeriod.Monthly }])
            { Source = UsageSource.Codex }, Stale: false);

    private static IEnumerable<string> Captions(Control root)
    {
        foreach (var control in root.Controls.Cast<Control>())
        {
            if (control is Label label) yield return label.Text;
            foreach (var nested in Captions(control)) yield return nested;
        }
    }

    /// <summary>A Claude-only popup is byte-for-byte what it was: no headers, no separator, no
    /// visual churn for a user who has never run Codex.</summary>
    [Fact]
    public void WithoutCodex_NoProviderHeaders()
    {
        using var popup = new UsagePopup(Claude(), new Settings(), Now);
        popup.CreateControl();

        var captions = Captions(popup).ToList();
        Assert.Contains(captions, c => c.Contains("5-hour window"));
        Assert.DoesNotContain(captions, c => c.Contains("Codex"));
        Assert.DoesNotContain(captions, c => c.Trim() == "Claude");
    }

    [Fact]
    public void WithCodex_ShowsBothSectionsWithHeaders()
    {
        using var popup = new UsagePopup(Claude(), new Settings(), Now, codex: Codex(), codexFetchStatus: "live");
        popup.CreateControl();

        var captions = Captions(popup).ToList();
        Assert.Contains(captions, c => c.Contains("Claude"));
        Assert.Contains(captions, c => c.Contains("Codex"));
        Assert.Equal(2, captions.Count(c => c.Contains("5-hour window")));
    }

    [Fact]
    public void WithCodex_ShowsTheSpendControlRowAsMonthly()
    {
        using var popup = new UsagePopup(Claude(), new Settings(), Now, codex: Codex(), codexFetchStatus: "live");
        popup.CreateControl();

        Assert.Contains(Captions(popup), c => c.Contains("Spend control monthly") && c.Contains("111%"));
    }

    /// <summary>Each section owns its own freshness line: the pipelines have independent cadences, so
    /// one shared footer would misreport one of them.</summary>
    [Fact]
    public void WithCodex_EachSectionHasItsOwnUpdatedLine()
    {
        var staleCodex = new DisplayChoice(Codex().Snapshot, Stale: true);
        using var popup = new UsagePopup(Claude(), new Settings(), Now, codex: staleCodex, codexFetchStatus: "live");
        popup.CreateControl();

        var captions = Captions(popup).ToList();
        Assert.Equal(2, captions.Count(c => c.Contains("updated", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(captions, c => c.Contains("stale"));
    }

    /// <summary>No credits row for Codex, whatever the popup does for Claude: the payload has no
    /// percentage to draw and a 0 % bar is the placeholder the invariants forbid.</summary>
    [Fact]
    public void WithCodex_NoCreditsRow()
    {
        using var popup = new UsagePopup(Claude(), new Settings(), Now, codex: Codex(), codexFetchStatus: "live");
        popup.CreateControl();

        Assert.DoesNotContain(Captions(popup), c => c.StartsWith("Credits"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~UsagePopupCodexTests`
Expected: FAIL — the constructor has no Codex parameters (CS1501).

- [ ] **Step 3: Extract the per-provider block**

In `src/ClaudeUsageTray/Tray/UsagePopup.cs`, move the body of the `else` branch (window rows, scoped
rows, the "+N more" line, credits, and the updated line) into one private method, then call it twice:

```csharp
    /// <summary>One provider's rows. Extracted so a second provider renders through exactly the same
    /// code — a copy would let the two drift, and the bars agreeing is the point.</summary>
    /// <param name="header">Null for a single-provider popup, which is what keeps a Claude-only
    /// popup byte-for-byte what it was: the headers arrive with the second section, which is the
    /// only moment they carry information.</param>
    private void AddProviderSection(TableLayoutPanel layout, DisplayChoice choice, Settings settings,
        DateTimeOffset now, string? header, string noDataFallback, string? fetchStatus)
    {
        if (header is not null) AddSectionHeader(layout, header);

        if (choice.Snapshot is not { } snapshot)
        {
            layout.Controls.Add(WrappingLabel(noDataFallback, SystemColors.ControlText, new Padding(0)));
        }
        else
        {
            bool desktop = snapshot.Source == UsageSource.DesktopHistory;
            AddWindowRow(layout, "5-hour window", snapshot.FiveHour, UsageValues.FiveHourPeriod, settings, now, desktop);
            AddWindowRow(layout, "7-day window", snapshot.SevenDay, UsageValues.SevenDayPeriod, settings, now, desktop);

            var rows = PopupRows.ForScopedLimits(snapshot.ScopedLimits);
            foreach (var limit in rows.Visible) AddScopedRow(layout, limit, settings, now);
            if (rows.HiddenCount > 0) AddMoreLine(layout, rows.HiddenCount);

            if (snapshot.Credits is { } credits) AddCreditRow(layout, credits, settings);

            AddUpdatedLine(layout, snapshot, choice.Stale, desktop, now);
        }

        if (!string.IsNullOrEmpty(fetchStatus)) AddFetchLine(layout, fetchStatus);
    }
```

`AddSectionHeader`, `AddMoreLine`, `AddUpdatedLine` and `AddFetchLine` are the existing inline blocks
lifted into methods — keep their current styling exactly, including the `"+N more"` grey and the
`Color.Firebrick` stale colour. The constructor becomes:

```csharp
    public UsagePopup(DisplayChoice choice, Settings settings, DateTimeOffset now,
        IReadOnlyList<SourceView>? statusSources = null, string? lastFetchStatus = null,
        string? noDataText = null, DisplayChoice? codex = null, string? codexFetchStatus = null,
        int? maxHeight = null)
    {
        // ... form setup and layout creation unchanged ...

        AddPlatformStatus(layout, statusSources, settings, now);

        // Headers only exist when there is a second section to tell apart.
        bool both = codex is not null;
        AddProviderSection(layout, choice, settings, now, both ? "Claude" : null,
            noDataText ?? NoDataReason.Default, lastFetchStatus);

        if (codex is { } codexChoice)
            AddProviderSection(layout, codexChoice, settings, now, "Codex",
                codexFetchStatus ?? "no Codex data", codexFetchStatus);

        // ... host, ClampHeight, PositionNearCursor unchanged ...
    }
```

- [ ] **Step 4: Pass the Codex choice from TrayApp**

In `ShowPopup`, hand over the Codex side only when its icons exist — a popup section for a provider
with no badge would be the one place Codex appears unannounced:

```csharp
        var codex = _codexAvailability.State == CodexState.Available
            ? CodexDisplay.Choose(_codexSnapshot, now, _settings)
            : null;
        _popup = new UsagePopup(SourceSelection.Choose(_cliSnapshot, _desktopSnapshot, now, _settings),
            _settings, now, _statusMonitor.Sources(), _lastFetchStatus, _noDataText,
            codex, codex is null ? null : _codexFetchStatus);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~UsagePopup`
Expected: PASS — including the existing width, source, status and height tests, which all construct
the popup without the new arguments.

- [ ] **Step 6: Look at it once**

Render the two-provider popup to PNG with `Control.DrawToBitmap`, scaled 6x with
`InterpolationMode.NearestNeighbor` per CLAUDE.md. Check the section headers, that the spend-control
bar is red, and that the Claude-only popup is unchanged. Delete the probe file afterwards.

- [ ] **Step 7: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add src/ClaudeUsageTray/Tray/UsagePopup.cs src/ClaudeUsageTray/Tray/TrayApp.cs tests/ClaudeUsageTray.Tests/UsagePopupCodexTests.cs
git commit -m "feat(codex): show both providers in one popup

The per-provider block is extracted and called twice, so the two
sections cannot drift. Headers and the Codex section appear only when
Codex data exists, leaving a Claude-only popup exactly as it was, and
each section carries its own freshness and fetch line."
```

---

### Task 9: Codex notifications

**Files:**
- Modify: `src/ClaudeUsageTray/Core/UsageNotificationRules.cs` (`ForCodex`, the `Describe` exception,
  the provider-named title)
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs` (`Notify`, `Arm`)
- Test: `tests/ClaudeUsageTray.Tests/CodexNotificationTests.cs` (create)

**Interfaces:**
- Consumes: `UsageNotificationRules` (prerequisites Task 1), `Settings.CodexNotifications` (Task 5),
  `CodexUsageJson.SpendControlLabel` (Task 2).
- Produces: `UsageNotificationRules.ForCodex()`; `Describe` names spend control.

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/CodexNotificationTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~CodexNotificationTests`
Expected: FAIL — `ForCodex` does not exist.

- [ ] **Step 3: Add the Codex factory and the provider-named title**

In `src/ClaudeUsageTray/Core/UsageNotificationRules.cs`, add beside `ForClaude`:

```csharp
    public static UsageNotificationRules ForCodex()
        => new(NotificationTags.Usage("codex"), s => s.CodexNotifications, "Codex ");
```

Extend the primary constructor with a title prefix, defaulted to empty so Claude's toast wording is
untouched — a user with one provider should not start seeing it named at them:

```csharp
public sealed class UsageNotificationRules(
    string tag, Func<Settings, UsageNotificationSettings> notifications, string titlePrefix = "")
```

and in `Compose`:

```csharp
        var worst = crossed.Any(v => Verdict(v) == Severity.Red) ? "red" : "orange";
        var title = crossed.Count == 1
            ? $"{titlePrefix}usage limit {worst}"
            : $"{titlePrefix}usage limits {worst}";
        title = char.ToUpperInvariant(title[0]) + title[1..];
```

That keeps "Usage limit red" for Claude and gives "Codex usage limit red" for Codex. Check the
existing title assertions in `NotificationRulesUsageTests` and keep them passing verbatim.

- [ ] **Step 4: Name spend control in the log**

In `Describe`:

```csharp
    /// <summary>Log-safe: the fixed keys by name, scoped limits by count only — their labels are the
    /// account-specific model names fetch.log must never carry. Spend control is the exception and is
    /// named: that label is a fixed string this app supplies, not a payload value, and the figure is
    /// a percentage with no money in it.</summary>
    private static string Describe(IReadOnlyList<UsageValue> values)
    {
        static bool IsNamed(UsageValue v) => v.Key is "5h" or "7d" or "credits";

        var named = values.Where(IsNamed).Select(v => $"{v.Key}={v.Percent}%").ToList();
        var spend = values.Where(v => v.Key == CodexUsageJson.SpendControlLabel).ToList();
        named.AddRange(spend.Select(v => $"spend={v.Percent}%"));

        int scoped = values.Count(v => !IsNamed(v) && v.Key != CodexUsageJson.SpendControlLabel);
        if (scoped > 0) named.Add($"scoped={scoped}");
        return string.Join(" ", named);
    }
```

- [ ] **Step 5: Wire both providers in TrayApp**

Add the Codex rules field beside the Claude one:

```csharp
    private readonly UsageNotificationRules _codexNotifications = UsageNotificationRules.ForCodex();
```

In `Notify`, evaluate both — the Codex half only when its icons exist, so a provider the user cannot
see never interrupts them:

```csharp
    private void Notify(DisplayChoice choice, DateTimeOffset now)
    {
        var usage = _usageNotifications.OnUsage(choice, _settings, now);
        foreach (var line in usage.Log) _log.Write(now, line);
        if (usage.RemoveUsageToast) _toasts.Remove(_usageNotifications.Tag);
        if (usage.Notification is { } toast) _toasts.Show(toast);

        if (_codexAvailability.State == CodexState.Available)
        {
            var codex = _codexNotifications.OnUsage(CodexDisplay.Choose(_codexSnapshot, now, _settings),
                _settings, now);
            foreach (var line in codex.Log) _log.Write(now, $"codex: {line}");
            if (codex.RemoveUsageToast) _toasts.Remove(_codexNotifications.Tag);
            if (codex.Notification is { } codexToast) _toasts.Show(codexToast);
        }

        foreach (var outcome in _statusNotifications.OnStatus(_statusMonitor.Sources(), _settings))
        {
            foreach (var line in outcome.Log) _log.Write(now, line);
            if (outcome.Notification is { } status) _toasts.Show(status);
        }
    }
```

In `OnCodexFetchCompleted`, arm the Codex rules from each outcome, mirroring Claude's `Arm`:
`_codexNotifications.NoteLiveOutcome(LiveOutcome.Snapshot)` on success, `Unauthorized`,
`RateLimited` and `Failed` on the other branches, writing any returned line prefixed `codex: `. Also
call `NoteLiveOutcome(LiveOutcome.NoToken)` on the missing-credentials path in `StartCodexFetch`, for
the same reason Claude does: a machine that will never have a token must not wait forever to arm.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CodexNotificationTests|FullyQualifiedName~NotificationRules"`
Expected: PASS

- [ ] **Step 7: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add src/ClaudeUsageTray/Core/UsageNotificationRules.cs src/ClaudeUsageTray/Tray/TrayApp.cs tests/ClaudeUsageTray.Tests/CodexNotificationTests.cs
git commit -m "feat(codex): toast Codex crossings on their own switch

Second rules instance with its own arming state, Action Center slot and
level, titled so four badges' worth of toasts can be told apart. Spend
control is named in the log because its label is a fixed string this app
supplies, unlike a payload-labelled scoped limit."
```

---

### Task 10: Auto-enable the OpenAI status source

**Files:**
- Modify: `src/ClaudeUsageTray/Core/Settings.cs` (`EnabledSources` takes the Codex state)
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs` (re-apply at the availability events)
- Test: `tests/ClaudeUsageTray.Tests/SettingsTests.cs`

**Interfaces:**
- Consumes: nullable `Enabled` (prerequisites Task 4), `CodexState` (Task 6).
- Produces: `Settings.EnabledSources(CodexState codex = CodexState.Unknown)`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/ClaudeUsageTray.Tests/SettingsTests.cs`:

```csharp
    /// <summary>An OpenAI outage badge that nothing can draw on is not worth polling for; one that
    /// can be drawn is worth having without configuration. So the registry default is overridden by
    /// Codex being present — and only while the user has expressed no opinion.</summary>
    [Fact]
    public void EnabledSources_TurnsOpenAiOnWhenCodexIsAvailable()
    {
        var settings = new Settings
        {
            StatusSources = new(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = new StatusSourceSettings { Enabled = null },
                ["openai"] = new StatusSourceSettings { Enabled = null },
            },
        };

        Assert.DoesNotContain("openai", settings.EnabledSources(CodexState.Unknown).Select(e => e.Source.Id));
        Assert.Contains("openai", settings.EnabledSources(CodexState.Available).Select(e => e.Source.Id));
    }

    /// <summary>An explicit opt-out is never overridden, however much Codex is in use.</summary>
    [Fact]
    public void EnabledSources_RespectsAnExplicitOptOutEvenWithCodex()
    {
        var settings = new Settings
        {
            StatusSources = new(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new StatusSourceSettings { Enabled = false },
            },
        };

        Assert.DoesNotContain("openai", settings.EnabledSources(CodexState.Available).Select(e => e.Source.Id));
    }

    /// <summary>And Codex's presence says nothing about Claude's source.</summary>
    [Fact]
    public void EnabledSources_CodexDoesNotAffectTheClaudeSource()
    {
        var settings = new Settings
        {
            StatusSources = new(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = new StatusSourceSettings { Enabled = false },
            },
        };

        Assert.DoesNotContain("claude", settings.EnabledSources(CodexState.Available).Select(e => e.Source.Id));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SettingsTests`
Expected: FAIL — `EnabledSources` takes no arguments (CS1501).

- [ ] **Step 3: Resolve the default against Codex availability**

In `src/ClaudeUsageTray/Core/Settings.cs`:

```csharp
    /// <summary>The enabled sources with their watch filters, in registry order — what StatusMonitor
    /// consumes. An undecided source resolves to its registry default, except that OpenAI's default
    /// flips on once Codex usage data exists: its only badge target is the Codex icons, so polling it
    /// before those exist buys nothing, and polling it after should not need configuring. An explicit
    /// true or false is the user's own answer and is never overridden.</summary>
    public IReadOnlyList<(StatusSource Source, IReadOnlyList<string> Filter)> EnabledSources(
        CodexState codex = CodexState.Unknown)
    {
        var result = new List<(StatusSource, IReadOnlyList<string>)>();
        foreach (var source in StatusSourceRegistry.All)
        {
            if (!StatusSources.TryGetValue(source.Id, out var entry) || entry is null) continue;
            bool byDefault = source.EnabledByDefault
                || (source.BadgeFor == UsageProvider.Codex && codex == CodexState.Available);
            if (entry.Enabled ?? byDefault) result.Add((source, entry.Components ?? []));
        }
        return result;
    }
```

- [ ] **Step 4: Re-apply when availability changes**

In `src/ClaudeUsageTray/Tray/TrayApp.cs`, pass the state at every `EnabledSources()` call site — the
constructor and the settings-save path — and re-apply after a Codex fetch concludes. Add to both
`StartCodexFetch`'s credential branches and `OnCodexFetchCompleted`, right beside the existing
`ApplyDisplayMode()`:

```csharp
        _statusMonitor.ApplyEnabled(_settings.EnabledSources(_codexAvailability.State));
```

`ApplyEnabled` already keeps the state of sources that stay enabled, so calling it on every fetch
cannot blank a banner or the badge for a poll cycle.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SettingsTests|FullyQualifiedName~StatusMonitorTests"`
Expected: PASS

- [ ] **Step 6: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Core/Settings.cs src/ClaudeUsageTray/Tray/TrayApp.cs tests/ClaudeUsageTray.Tests/SettingsTests.cs
git commit -m "feat(codex): watch status.openai.com once Codex is present

The OpenAI source's only badge target is the Codex icons, so its default
flips on with them and stays off without them. An explicit choice in
settings is never overridden."
```

---

### Task 11: The Codex settings tab

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/SettingsDialog.cs` (a fifth page and its controls)
- Test: `tests/ClaudeUsageTray.Tests/SettingsDialogCodexTests.cs` (create)

**Interfaces:**
- Consumes: `Settings.CodexDisplayMode`, `Settings.CodexNotifications` (Task 5), `CodexState` (Task 6).
- Produces: no new public API beyond the dialog's existing constructor shape — extend it with the
  Codex state, defaulted so existing tests are untouched.

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/SettingsDialogCodexTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class SettingsDialogCodexTests
{
    private static TabControl Tabs(Form dialog)
        => dialog.Controls.Find("tabs", searchAllChildren: true).OfType<TabControl>().Single();

    /// <summary>Nothing to configure for a provider that is not there.</summary>
    [Fact]
    public void WithoutCodex_NoCodexTab()
    {
        using var dialog = NewDialog(CodexState.Unavailable);
        dialog.CreateControl();

        Assert.DoesNotContain(Tabs(dialog).TabPages.Cast<TabPage>(), p => p.Text == "Codex");
    }

    [Fact]
    public void WithCodex_HasACodexTab()
    {
        using var dialog = NewDialog(CodexState.Available);
        dialog.CreateControl();

        Assert.Contains(Tabs(dialog).TabPages.Cast<TabPage>(), p => p.Text == "Codex");
    }

    /// <summary>The page must fit without scrolling, like every other page. The dialog sizes itself
    /// from the summed tab-header widths, so a fifth header is a layout change, not just a page.</summary>
    [Fact]
    public void CodexPage_FitsWithoutScrolling()
    {
        using var dialog = NewDialog(CodexState.Available);
        dialog.CreateControl();

        var page = Tabs(dialog).TabPages.Cast<TabPage>().Single(p => p.Text == "Codex");
        Assert.True(page.PreferredSize.Height <= page.ClientSize.Height,
            $"Codex page wants {page.PreferredSize.Height} px, has {page.ClientSize.Height} px");
    }
}
```

`NewDialog` must match however `SettingsDialogTests` already constructs the dialog — read that file
and reuse its helper, adding the Codex state argument. The control name `"tabs"` is a guess: check
what `SettingsDialog` names its `TabControl` and use the real name, or reach it the way the existing
tests do.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SettingsDialogCodexTests`
Expected: FAIL — the dialog constructor takes no Codex state, and there is no Codex page.

- [ ] **Step 3: Add the page**

In `src/ClaudeUsageTray/Tray/SettingsDialog.cs`, take the state in the constructor (defaulted to
`CodexState.Unknown` so existing call sites and tests compile unchanged) and add the page
conditionally, after `status` and before `about`:

```csharp
        _tabs.TabPages.Add(Page("general", "General", BuildGeneralPage()));
        _tabs.TabPages.Add(Page("appearance", "Appearance", BuildAppearancePage()));
        _tabs.TabPages.Add(Page("status", "Status", BuildStatusPage()));
        // Only when there is something to configure: Codex has no enable toggle, so the tab's
        // presence follows the data exactly as the popup section does.
        if (codexState == CodexState.Available)
            _tabs.TabPages.Add(Page("codex", "Codex", BuildCodexPage()));
        _tabs.TabPages.Add(Page("about", "About", BuildAboutPage()));
```

Declare the controls beside the existing ones, mirroring `_modeFive` / `_modeSeven` / `_modeBoth`
and `_notifyUsage` / `_notifyLevel`:

```csharp
    private readonly RadioButton _codexModeFive = new() { Text = "5-hour only", AutoSize = true };
    private readonly RadioButton _codexModeSeven = new() { Text = "7-day only", AutoSize = true };
    private readonly RadioButton _codexModeBoth = new() { Text = "Both", AutoSize = true };
    private readonly CheckBox _codexNotify = new() { Text = "Notify me when a Codex limit turns", AutoSize = true };
    private readonly ComboBox _codexNotifyLevel = new()
        { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
```

Copy the exact property initialisers the Claude equivalents use at lines 100-102 and wherever
`_modeFive` is declared, rather than the abbreviations above — the dialog sets widths and
`DropDownStyle` deliberately.

```csharp
    /// <summary>Codex's own display mode and notification switch. Separate from Claude's on purpose:
    /// a crowded tray may want Claude's pair and only Codex's five-hour badge, and badging Codex
    /// silently while Claude still toasts is a reasonable thing to want.</summary>
    private Control BuildCodexPage()
    {
        var page = PagePanel();

        page.Controls.Add(Heading("Codex badges"));
        page.Controls.Add(Indent(_codexModeFive));
        page.Controls.Add(Indent(_codexModeSeven));
        page.Controls.Add(Indent(_codexModeBoth));
        page.Controls.Add(Indent(new Label
        {
            Text = "Codex badges are rounded squares; Claude's are circles.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        }));

        page.Controls.Add(Heading("Notifications"));
        _codexNotifyLevel.Items.AddRange(LevelLabels);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(16, 0, 0, 2) };
        _codexNotify.Margin = new Padding(0, 4, 4, 0);
        _codexNotifyLevel.Margin = new Padding(0);
        row.Controls.Add(_codexNotify);
        row.Controls.Add(_codexNotifyLevel);
        SetOrder(_codexNotify, _codexNotifyLevel);
        page.Controls.Add(row);

        SetOrder(_codexModeFive, _codexModeSeven, _codexModeBoth, row);
        return page;
    }
```

Wire it into the three existing paths, beside each Claude counterpart:

```csharp
        // load (beside lines 608-610 and 630-632)
        _codexModeFive.Checked = source.CodexDisplayMode == DisplayMode.FiveHour;
        _codexModeSeven.Checked = source.CodexDisplayMode == DisplayMode.SevenDay;
        _codexModeBoth.Checked = source.CodexDisplayMode == DisplayMode.Both;
        _codexNotify.Checked = source.CodexNotifications.Enabled;
        _codexNotifyLevel.SelectedIndex = IndexOf(source.CodexNotifications.Level);
        _codexNotifyLevel.Enabled = _codexNotify.Checked;
```

```csharp
        // event wiring (beside line 682)
        _codexNotify.CheckedChanged += (_, _) => _codexNotifyLevel.Enabled = _codexNotify.Checked;
```

```csharp
        // save (beside lines 711-713 and 722-723)
        draft.CodexDisplayMode = _codexModeFive.Checked ? DisplayMode.FiveHour
            : _codexModeSeven.Checked ? DisplayMode.SevenDay
            : DisplayMode.Both;
        draft.CodexNotifications = new UsageNotificationSettings
        {
            Enabled = _codexNotify.Checked,
            Level = LevelAt(_codexNotifyLevel.SelectedIndex),
        };
```

The three radio buttons need their own group: WinForms groups by container, and
`page.Controls.Add(Indent(...))` wraps each one separately, so check whether the Claude mode radios
are wrapped the same way. If they share one container, put the Codex three in one container too —
otherwise all six end up mutually exclusive, and selecting a Codex mode would clear Claude's.

**Save must run even when the page is absent.** The dialog only builds the Codex page when Codex is
available, but the save path reads those controls unconditionally. Either guard the Codex lines in
the save path with the same condition, or (simpler) leave the controls constructed but unparented —
their values are loaded from settings either way, so an absent page round-trips the stored value
untouched.

In `TrayApp`, pass `_codexAvailability.State` where the dialog is constructed.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SettingsDialog`
Expected: PASS. `EveryPageFitsWithoutScrolling` is known to fail only under parallel solution-level
runs — if it fails, rerun the app project alone before treating it as real.

- [ ] **Step 5: Look at the dialog once**

Open the real dialog (`dotnet run --project src/ClaudeUsageTray`) with Codex available and check the
fifth tab's header does not push the dialog wider than the screen, and that the page's controls line
up with the other pages'.

- [ ] **Step 6: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Tray/SettingsDialog.cs src/ClaudeUsageTray/Tray/TrayApp.cs tests/ClaudeUsageTray.Tests/SettingsDialogCodexTests.cs
git commit -m "feat(codex): add the Codex settings page

Display mode, notification switch and level, on a fifth tab that exists
only when Codex data does — there is nothing to configure for a provider
that is not installed."
```

---

### Task 12: Document it

**Files:**
- Modify: `README.md`
- Modify: `CLAUDE.md`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Describe the behaviour in README.md**

README is the reference for *what* the app does. Add, in the voice the existing sections use: the
rounded-square Codex badges and what distinguishes them from Claude's discs; the Codex popup section;
that Codex appears by itself when the Codex CLI is signed in and needs no configuration; the new
`codexDisplayMode` and `codexNotifications` settings keys, alongside the existing key documentation;
and that `status.openai.com` is watched once Codex is present.

- [ ] **Step 2: Describe the mechanics in CLAUDE.md**

CLAUDE.md covers *how to work on it*. Extend the Data flow section with the Codex pipeline as a
fourth source, and add to the invariants:

- The Codex token is read-only, exactly as Claude's is.
- The Codex payload's `email`, `user_id`, `account_id` and spend-control money amounts never reach
  `fetch.log` or the display; spend control logs as `spend=N%`.
- Codex credits produce no row, and why.
- The Codex endpoint's request budget is **unmeasured**: do not probe it, and test against
  `CodexUsageApiClientTests` with canned responses. Its poll floor is 10 minutes for that reason.

- [ ] **Step 3: Add a CHANGELOG entry**

Add an `## Unreleased` section (or extend the existing one) describing the feature in user-facing
terms. Note that a release will need its own version section — `changelog-section.ps1` hard-fails
without one.

- [ ] **Step 4: Commit**

```bash
git add README.md CLAUDE.md CHANGELOG.md
git commit -m "docs: describe Codex usage support

README gets the user-facing behaviour and the new settings keys;
CLAUDE.md gets the fourth data source, the privacy rules for the Codex
payload, and the warning that the endpoint's budget is unmeasured."
```

---

## Done when

- `dotnet test` passes, and no existing assertion was changed to make it pass.
- On a machine with Codex signed in: two extra rounded-square badges, a Codex popup section with
  5-hour, 7-day and spend-control rows, and no credits row.
- On a machine without Codex: no extra icons, no popup section, no Codex settings tab, and
  `status.openai.com` is not polled.
- Deleting `~/.codex/auth.json` while the app runs removes the Codex badges within three poll cycles
  and leaves Claude's untouched; restoring it brings them back without a restart.
- `fetch.log` contains `codex:` lines with percentages and `spend=N%`, and no email, user id, account
  id, currency or credit amount.
