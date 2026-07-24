# v0.3 Live Usage Fetch — Direct OAuth Usage API Polling

**Problem:** v0.1/v0.2 read only `cachedUsageUtilization` from `%USERPROFILE%\.claude.json`, assuming Claude Code keeps it fresh. It does not: observed 16+ hours stale during active use. The tray therefore shows wrong data most of the time.

**Approach (validated against `realiti4/claude-swap`):** poll the Anthropic OAuth usage endpoint directly, using Claude Code's existing access token, read-only, under a strict polling budget. The cache read remains as fallback.

## Amended constraints (supersedes v0.1 spec §compliance in part)

- The app now makes **one kind of network call to Anthropic**: `GET https://api.anthropic.com/api/oauth/usage`, read-only, authenticated with Claude Code's existing OAuth access token.
- The app reads `%USERPROFILE%\.claude\.credentials.json` **read-only** (key `claudeAiOauth.accessToken`, validity-gated by `claudeAiOauth.expiresAt`). It NEVER writes that file, NEVER refreshes/rotates tokens (Claude Code owns the credentials — on 401 the app degrades, it does not attempt a refresh grant), and NEVER logs, displays, or persists token material anywhere.
- Unchanged: never write `.claude.json`; no other network calls to Anthropic; GitHub Releases remain the only other network use (updates).

## Polling budget (from claude-swap's measured findings)

The endpoint enforces roughly 28–30 requests per rolling hour per token for non-first-party clients. Rules:

- Poll every **5 minutes** (12/hour steady state). First fetch at startup.
- **429**: honor `Retry-After` when present; next poll no sooner than `max(Retry-After, 15 minutes)`. Keep showing last-known-good data (it stays valid until a window resets).
- **401/403**: remember the rejected token string and skip API fetches until `CredentialsReader` returns a *different* token (i.e., Claude Code has refreshed it); meanwhile the `.claude.json` cache path covers.
- Manual "Refresh now" triggers an immediate API fetch, but never more than one API request per **30 seconds**, and a **rolling-hour hard cap of 20 requests** (safety margin under the measured 28–30) bounds all fetches — manual and timed combined.
- A 429 **without** a usable `Retry-After` header (or with the HTTP-date form) must still incur the ≥ 15 minute penalty — rate-limit responses are never treated as generic network errors.
- Timeout 5 s; network errors → exponential backoff (5 → 10 → 20 min, capped) with last-known-good retained.

## Request contract

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <accessToken>
anthropic-beta: oauth-2025-04-20
User-Agent: ClaudeUsageTray/<version>
```

Response (fields we consume; same shape as the cache's `utilization` object):

```json
{ "five_hour": { "utilization": 42, "resets_at": "2026-07-24T18:39:59Z" },
  "seven_day": { "utilization": 13, "resets_at": "2026-07-27T15:59:59Z" } }
```

`utilization` integer percent; `resets_at` ISO-8601. Missing/malformed windows → that window is null (same tolerance as `UsageCacheReader`).

## New units (namespace `ClaudeUsageTray.Core`)

- `static class CredentialsReader` — `static string? TryReadAccessToken(string path, DateTimeOffset now)`: parses the credentials file; returns `claudeAiOauth.accessToken` only when it is a non-empty string AND `expiresAt` (Unix ms) is more than 5 minutes in the future; null on missing file/key, expired token, malformed JSON, or any IO error. Never throws. `FileShare.ReadWrite`, 32 MiB guard (same pattern as `UsageCacheReader`). Default path helper: `%USERPROFILE%\.claude\.credentials.json`.
- `sealed record UsageFetchResult(UsageSnapshot? Snapshot, bool Unauthorized, bool RateLimited, TimeSpan? RetryAfter)` — `Snapshot` non-null on success (with `FetchedAt = now`); `Unauthorized` true on 401/403; `RateLimited` true on any 429 (regardless of header form); `RetryAfter` from the 429's header when present (delta form, or HTTP-date computed against `now`).
- `sealed class FetchScheduler` — pure, unit-testable budget gate owning the 30 s floor, the rolling-hour cap, the ≥ 15 min rate-limit penalty, and the 5/10/20 min failure backoff; `TrayApp` consults it before every fetch and records every outcome.
- `static class UsageApiClient` — `static Task<UsageFetchResult> FetchAsync(HttpClient http, string accessToken, DateTimeOffset now, CancellationToken ct)`: performs the request above, parses per the response contract, maps failures (timeout, network, non-2xx, malformed body) to `Snapshot = null`. Never throws. Testable via injected `HttpMessageHandler`.

## TrayApp integration

- New WinForms timer `_poll` (interval managed per the budget rules). On tick / startup / "Refresh now": read token via `CredentialsReader`; if null, skip (cache fallback covers); else start `FetchAsync` on the thread pool if none is in flight (single-flight flag), and marshal the completion back to the UI thread (`_sync.BeginInvoke`).
- On success: adopt the snapshot and `Render()`.
- **Freshness precedence (new rule, also fixes a latent clobber):** any snapshot adoption — from the API or from the cache path in `Refresh()` — only replaces `_snapshot` when its `FetchedAt` is newer than the current one. (Today the 30 s cache re-read would overwrite a fresher API snapshot with stale cache data.)
- Existing cache watcher/timers/tooltips/staleness UI unchanged. With live polling, staleness (default 15 min) now signals actual fetch problems instead of firing constantly.
- HttpClient: one static instance, 5 s timeout.

## Ride-along changes

- README: "no network calls to Anthropic" claim replaced with an accurate description (read-only usage polling with Claude Code's token; no token storage; no writes) plus the budget summary.
- v0.1 spec (`docs/superpowers/spec/claude-usage-tray.md`): extend the existing amendment note to cover the reversed network constraint (pointer to this spec).
- Version → `0.3.0`.

## Testing

- `CredentialsReaderTests`: valid token, expired token (past `expiresAt`), near-expiry (< 5 min → null), missing file/key, malformed JSON, non-object root — all file-based temp fixtures with dummy token strings (never real tokens).
- `UsageApiClientTests`: fake `HttpMessageHandler` covering 200 with both windows / one window / malformed body, 401, 403, 429 with and without `Retry-After`, timeout (cancellation), network exception. Asserts exact request URL, both auth/beta headers, and mapping to `UsageFetchResult`.
- Snapshot precedence: pure helper tested for newer/older/equal/null combinations.
- TrayApp glue (timer wiring, single-flight, marshaling) remains manually verified, consistent with §10 of the v0.1 spec.

## Acceptance

- With Claude Code installed and logged in: tray shows usage that changes within ~5 minutes of real usage, without Claude Code running.
- With no/expired credentials: identical behavior to v0.2 (cache fallback, neutral state, no crash, no error spam).
- No token material ever appears in logs, UI, settings, or files. All existing tests pass; new units fully covered.
