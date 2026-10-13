# Codex usage alongside Claude

Date: 2026-10-13
Extends: [2026-08-26-openai-status-source-design.md](2026-08-26-openai-status-source-design.md),
[2026-09-05-desktop-notifications-design.md](2026-09-05-desktop-notifications-design.md)

## Problem

The tray shows Claude's limits. The same machine runs OpenAI Codex against a second set of limits
that fill independently and are invisible until Codex refuses a turn. The question this design
answers is whether Codex exposes the same metrics the app is built around, and what the smallest
honest way to show them is.

It does. `GET https://chatgpt.com/backend-api/codex/usage`, authenticated with the token Codex
stores in `auth.json`, returns:

```json
{
  "plan_type": "team",
  "rate_limit": {
    "primary_window":   { "used_percent": 42, "limit_window_seconds": 18000,  "reset_at": 1791932468 },
    "secondary_window": { "used_percent": 21, "limit_window_seconds": 604800, "reset_at": 1792474516 }
  },
  "credits": { "has_credits": true, "unlimited": false, "balance": null },
  "spend_control": {
    "reached": true,
    "individual_limit": { "unit": "credit", "limit": "5", "used": "5.573…",
                          "used_percent": 111, "reset_at": 1793491200 }
  }
}
```

Verified live once, on 2026-10-13, against a `team` plan. `primary_window` is the 5-hour window and
`secondary_window` the 7-day one — the same two rolling windows the app already draws, in the same
percent + reset shape. Credits map across directly. `spend_control.individual_limit` has no Claude
analogue and is a real signal: on the account this was verified against it sits at 111 %, already
breached.

## Decisions

| Question | Decision |
|---|---|
| Tray surface | Codex gets its own badge icons; one popup shows both providers |
| Icon count | Codex has its own `DisplayMode`, independent of Claude's — up to four icons |
| Spend control | Shown as a full row with its own severity |
| Absent Codex | Auto-detected; no toggle. Icons exist only once data exists |
| Notifications | Yes, behind a separate on/off and level |
| Naming | Unchanged. The app stays "Claude Usage" everywhere |
| Data source | Live API only |

## Approach: a parallel pipeline over shared types

Three options were considered.

**Generalise the existing pipeline** — thread a provider through `SourceSelection`,
`SnapshotPrecedence`, `PopupRows` and `NotificationRules`. Rejected: those state machines encode
Claude's specific three-source contest (CLI cache vs. live API vs. Desktop history). Every Claude
path would grow a dimension it does not need, and a Codex parsing bug could reach Claude's display —
the opposite of the rule the status sources are built on.

**An `IUsageProvider` interface** — rejected as speculative. It refactors working code for a third
provider that does not exist, and nothing here forecloses it later.

**Chosen: a separate Codex pipeline producing the existing types.** New I/O and new state live in
`Core/Codex/`; everything downstream is reused *by instantiation*, not by modification. Codex
produces an ordinary `UsageSnapshot`, so `SeverityRules`, `UsageValues`, `PaceFormat`, `UsageBar` and
`IconRenderer` need no knowledge of it. Severity stays single-sourced through `UsageValues`, so
Codex's badge, bar and toast cannot disagree with each other any more than Claude's can.

This mirrors the rule already load-bearing for platform status: one source's failure must never
null, clobber, or delay another's data.

### Live API only

Codex has a second readable source: every `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` carries
`token_count` events with an embedded `rate_limits` block of the same shape. It is deliberately not
used.

Its only value is covering an expired token or an offline machine. The token is valid for days and
Codex refreshes it on use, so a Codex user's token is fresh by construction; and the chosen
behaviour for "no data" is "no icon" rather than a stale fallback. Against that, the rollout path
costs a newest-file-in-a-dated-tree scan and a watcher that cannot point at a single path. Deferred,
not forgotten: if expired-token cases show up in practice, this is the fallback to build.

### Request budget

Unknown, and deliberately not measured. Claude's `/oauth/usage` budget was established by
observation at a cost documented in CLAUDE.md; the same probing here would degrade real Codex
sessions. The Codex fetch therefore gets a `FetchScheduler` with Claude's conservative defaults and
**its own counter** — never sharing Claude's budget, floors, or backoff.

## Components

New, in `Core/Codex/`. All pure except the last.

- **`CodexPath`** — resolves the Codex home: `CODEX_HOME` when set, else `~/.codex`. Mirrors
  `ConfigPath.Resolve`. (`CODEX_HOME` is honoured by the Codex CLI; confirmed against the shipped
  binary.)
- **`CodexCredentialsReader`** — reads `auth.json` into `CodexAuth(AccessToken, AccountId,
  ExpiresAt)`, decoding `exp` from the JWT payload so an expired token is a known no-data reason
  rather than a 401 surprise. Read-only, swallows IO/JSON errors, returns null.
- **`CodexUsageJson`** — `Parse(json, fetchedAt)` into the existing `UsageSnapshot`:
  `primary_window` → `FiveHour`, `secondary_window` → `SevenDay`, `credits` → `CreditUsage`, and
  `spend_control.individual_limit` → a single `ScopedLimit("Spend control", null, used_percent,
  reset_at, IsActive: true)`. No new record types.
- **`CodexDisplay`** — Codex has one source, so this is `SourceSelection`'s one-line counterpart:
  reuse `SourceSelection.Age`, decide `Stale`, return a `DisplayChoice`.
- **`CodexUsageApiClient`** — the only impure piece. `Authorization: Bearer` plus
  `ChatGPT-Account-Id`. Never throws; returns null on anything unexpected.

Reused by instantiation: a second `FetchScheduler`, a second `NotificationRules`.

`UsageSource` gains a `Codex` member. It already means "which program produced the data", and
`NotificationRules` already rebaselines when `Source` changes.

### Timers

No new timer. The existing 5-minute `_poll` tick drives both fetches; each provider's scheduler
decides independently whether it may go, and each has its own single-flight flag. Codex gets no
`FileSystemWatcher` and no 30-second re-read, because none of its files are read.

### Availability

The Codex icons appear once a first snapshot exists, and then obey the project's existing law:
*degrade to stale, never to blank*. A 401, a 429 or a dead network dims them. They are removed only
when `auth.json` itself stops yielding credentials. The tray does not change shape on a transient
failure.

## The popup

`UsagePopup`'s constructor builds the whole single-column layout inline. The per-provider block —
two window rows, scoped rows, credits, updated line — is extracted into one private method called
twice. That extraction is the only refactor in scope.

Order: platform-status banner (unchanged), Claude section, Codex section. The Codex section carries
"5-hour window", "7-day window", the **Spend control** row through the existing scoped-row renderer,
and credits when present.

Two rules:

- **Section headers appear only when Codex data exists.** With no Codex the popup is exactly what it
  is today — no header, no separator, no visual change for a Claude-only user. The headers arrive
  with the second section, which is the only moment they carry information.
- **Each section owns its freshness and fetch line.** The two pipelines have independent cadences
  and failure states; a shared footer would misreport one of them. "Claude stale, Codex current"
  must be readable as exactly that.

Height: Claude's part is bounded by `PopupRows.Cap` (4 scoped rows) and Codex adds at most four
more. The form is `AutoSize` and `PositionNearCursor` clamps position but not size, so the
both-providers-maxed case is the tallest this popup has been and is worth checking on a real screen.

## Icons

The existing badge already spends every channel it has: colour is severity, the digit is the window,
arc direction is 5h vs. 7d, dimming is staleness, and the bottom-right corner is the
platform-degraded marker. The provider needs a channel none of those use.

**Claude stays a disc; Codex is a rounded square.** Silhouette survives dimming, every severity
colour, and the warning badge. It is also the only encoding that works regardless of tray order:
`ApplyDisplayMode` already notes that Windows decides notification-area order and overflow
placement, so position cannot identify a provider.

Rejected: a top-left corner dot (at 16 px it reads as "marked", not as an identity, and spends the
last free corner); filled-vs-hollow (a transparent centre is what made the digit unreadable on light
taskbars in v0.1); an outer halo ring (costs 2 px of a 16 px budget and collapses at 100 % DPI).

`IconRenderer.Render` gains an optional `BadgeShape shape = BadgeShape.Disc`, added the way
`warning` was, so every Claude call site is untouched. `Draw` swaps `FillEllipse`/`DrawEllipse` for a
rounded-rectangle `GraphicsPath`, and the usage wedge requires `g.SetClip(path)` before `FillPie` or
its corners spill outside the silhouette. The corner radius at 16 px is chosen by sampling in
`IconRendererTests`, not asserted here. `BadgeShape` lives in `Tray/` beside the renderer: no pure
decision depends on it, and `Core/` stays provider-agnostic.

Up to four `NotifyIcon`s — `_iconFive`, `_iconSeven`, `_codexIconFive`, `_codexIconSeven` — the
Codex pair gated by `codexDisplayMode` and by availability.

Codex tooltips name the provider and, as a correctness matter rather than a cosmetic one, must not
carry Claude's platform-status suffix. `WithStatus` becomes provider-scoped.

### The badge rule generalises

`Core/StatusSource.cs` already ships an OpenAI source, watch-filtered to
`["codex", "responses", "login", "vs code extension"]` and carrying `RaisesBadge: false` with the
comment *"an OpenAI outage never marks the tray icon"*. That was correct because no Codex icon
existed.

`RaisesBadge` therefore stops meaning "does this mark the tray" and becomes "which provider's icons
does this mark". `StatusMonitor.BadgeDegraded()` takes a provider: a Claude outage draws the
exclamation on the Claude icons, an OpenAI outage on the Codex icons, and neither crosses over. The
documented limitation becomes the general rule, and is testable as exactly that.

The OpenAI source's effective default flips to **enabled when Codex usage data is detected**, and
never overrides a value the user has explicitly saved. An OpenAI outage badge that nothing can draw
on is not worth polling for; one that can be drawn is worth having without configuration.

## Settings

Two new keys, both mirroring existing shapes so tolerant parsing comes for free:

- `codexDisplayMode` — its own `DisplayMode`, defaulting to `Both` as Claude's does.
- `codexNotifications` — a second `UsageNotificationSettings` through
  `TolerantUsageNotificationsConverter`, so a bad level resets that level alone.

No `codexAuthPathOverride`: `CODEX_HOME` covers a relocated Codex home and tests pass paths
directly.

The dialog gains a fifth tab, **Codex**, present only when Codex is detected — the same rule as the
popup section, and there is nothing to configure for an absent provider. Two cautions: the dialog
sizes itself by summing tab-header widths, and the "every page fits without scrolling" test is
already known to be flaky under parallel solution-level runs.

## Notifications

`NotificationRules.UsageTag` is a single const `"usage"`. With two providers that is a defect in
waiting: a Codex toast would replace a Claude toast in Action Center instead of sitting beside it.
The tag becomes per-provider.

Each provider gets its own `NotificationRules` instance, its own arming state, and a fingerprint
carrying its own level. Toast text names the provider.

`UsageValues.Enumerate` already walks `ScopedLimits`, so the spend-control row inherits severity and
notification behaviour with no new code. On an account already at 111 % that row is red from the
first render; the existing arming rule is what prevents a toast at startup.

## Error handling

Nothing in the Codex read path throws. `CodexCredentialsReader`, `CodexUsageJson` and
`CodexUsageApiClient` swallow IO, JSON and HTTP errors and return null. A malformed `auth.json`
degrades the Codex display and cannot affect Claude's.

The token is read-only. It is never written, never refreshed, and never logged — the same invariant
the Claude credentials path carries.

`fetch.log` gains `codex:`-prefixed lines carrying outcomes and percentages only. The payload also
returns `email`, `user_id`, `account_id`, and spend-control `limit` / `used` / `balance`; none of
these is ever logged. Spend control is logged as `spend=111%`.

## Testing

All canned; no live calls, for the reason given under Request budget.

- `CodexUsageJsonTests` — the full verified payload, absent credits, absent spend control, a null
  balance, and garbage.
- `CodexCredentialsReaderTests` — missing file, malformed JSON, expired `exp`.
- `CodexDisplayTests` — staleness boundaries.
- `IconRendererTests` — rounded-square silhouette: corners transparent, wedge clipped inside the
  shape.
- `NotificationRules` — per-provider tags, and that one provider's rebaseline does not disturb the
  other's.
- `StatusMonitor` — an OpenAI outage marks Codex icons only; a Claude outage marks Claude icons
  only.

## Success criteria

- A Codex user sees 5h, 7d and spend-control percentages without configuring anything.
- A Claude-only user sees no change whatsoever: same popup, same icons, same settings pages.
- Killing either provider's data leaves the other's display, cadence and toasts untouched.
- No credential material, identifier, or money amount reaches `fetch.log`.

## Out of scope

- The rollout-file fallback (see Live API only).
- Any rename of the app, package id, or repository.
- A third provider, and the `IUsageProvider` abstraction that would serve one.
- Measuring the Codex endpoint's request budget.
