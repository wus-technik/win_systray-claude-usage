# Codex usage alongside Claude

Date: 2026-10-13
Extends: [2026-08-26-openai-status-source-design.md](2026-08-26-openai-status-source-design.md),
[2026-09-05-desktop-notifications-design.md](2026-09-05-desktop-notifications-design.md)

## Endpoint correction — 2026-10-28

The original `/backend-api/codex/usage` URL below was incorrect for the ChatGPT base URL.
OpenAI's [backend-client routing](https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/client/rate_limit_resets.rs)
uses `/wham/usage` for `PathStyle::ChatGptApi`, so the app now requests
`https://chatgpt.com/backend-api/wham/usage`. The original local build received 401/403 even
with a newly signed-in token. The canned HTTP test pins the literal URL to prevent regression.

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
percent + reset shape.

The other two blocks are less of a match than they look. `credits` carries a capability flag and a
null balance, with no percentage anywhere — it does not map onto `CreditUsage` and is out of scope
(see below). `spend_control.individual_limit` has no Claude analogue and is a real signal — on the
account this was verified against it sits at 111 %, already breached — but its `reset_at` is a
calendar-month boundary, which the weekly-by-construction `ScopedLimit` cannot express as shipped.

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
`Core/Codex/`. Codex produces an ordinary `UsageSnapshot`, so `SeverityRules`, `UsageValues`,
`PaceFormat` and `UsageBar` need no knowledge of it, and severity stays single-sourced through
`UsageValues` — Codex's badge, bar and toast cannot disagree with each other any more than Claude's
can.

Downstream reuse is *mostly* by instantiation rather than modification, but not entirely: see
Prerequisites for the shared code that genuinely has to change first. The point of the approach is
that none of those changes carries Codex knowledge.

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
sessions. The Codex fetch therefore gets a `FetchScheduler` with **its own counter** — never sharing
Claude's budget, floors, or backoff.

Being honest about what that means: this ships an unmeasured poll against an endpoint with no
fallback source behind it. Claude's defaults were tuned against a *known* ~28–30 requests/hour
ceiling, so inheriting them here would be borrowing a number from an unrelated budget. Codex starts
at half Claude's rate — one fetch per 10 minutes — and that floor is revisited only if observed
`429`s justify it. A 5-hour window moves slowly enough that 10 minutes costs the user nothing
visible.

## Prerequisites

Review surfaced that several things this design calls "reuse" are in fact changes to shared code
that Claude's paths depend on. Each is independently testable, carries no Codex knowledge, and is
safer to land and verify on its own:

1. **Split `NotificationRules`** into per-provider usage rules and a single status rules object, and
   pass the `UsageNotificationSettings` in rather than reading `Settings.UsageNotifications`.
2. **Per-provider toast tags**, at both the emission and the retraction site.
3. **A period on `ScopedLimit`**, defaulting to seven days, with captions and pacing derived from it.
4. **`RaisesBadge` as a provider affinity**, resolved against a target provider in
   `StatusMonitor.BadgeDegraded` and all three `StatusDetailRows` consumers.
5. **A nullable `StatusSourceSettings.Enabled`**, so "never decided" survives a `Save`.
6. **`BadgeShape` through `Render` and `RenderNeutral`.**
7. **A bounded popup height** with overflow scrolling.

Only then does the Codex pipeline itself become additive.

## Components

New, in `Core/Codex/`. All read-only and clock-free except the last; `Core/` is where this
project's I/O readers already live (`CredentialsReader`, `UsageApiClient`, `UsageCacheReader`,
`PlatformStatusApi`), because the boundary CLAUDE.md defends is WinForms-free and clock-free, not
side-effect-free.

- **`CodexPath`** — resolves the Codex home: `CODEX_HOME` when set, else `~/.codex`. Mirrors
  `ConfigPath.Resolve`. (`CODEX_HOME` is honoured by the Codex CLI; confirmed against the shipped
  binary.)
- **`CodexCredentialsReader`** — reads `auth.json` into `CodexAuth(AccessToken, AccountId,
  ExpiresAt)`, decoding `exp` from the JWT payload so an expired token is a known no-data reason
  rather than a 401 surprise. Read-only, swallows IO/JSON errors, returns null.
- **`CodexUsageJson`** — `Parse(json, fetchedAt)` into the existing `UsageSnapshot`:
  `primary_window` → `FiveHour`, `secondary_window` → `SevenDay`, and
  `spend_control.individual_limit` → a `ScopedLimit("Spend control", …)` carrying its own period
  (see Spend control below). **Credits do not map across** (see below).
- **`CodexDisplay`** — Codex has one source, so this is `SourceSelection`'s one-line counterpart:
  reuse `SourceSelection.Age`, decide `Stale`, return a `DisplayChoice`.
- **`CodexUsageApiClient`** — the only impure piece. `Authorization: Bearer` plus
  `ChatGPT-Account-Id`. Never throws; returns null on anything unexpected.

Reused by instantiation: a second `FetchScheduler`. `NotificationRules` is **not** reusable by
instantiation as it stands — see Notifications.

`UsageSource` gains a `Codex` member so a snapshot can say honestly where it came from: the popup
asks `Source == DesktopHistory` to decide Claude-Desktop-specific wording, and a Codex snapshot has
to answer that question correctly rather than by accident.

Two justifications considered and dropped, recorded so they are not re-proposed: the member is *not*
needed for `NotificationRules`' rebaseline-on-source-change rule (a per-provider instance never sees
its source change), and *not* needed to fix `LogSourceChange` (`TrayApp.cs:418-426`) — that function
is fed only by `SourceSelection.Choose` over the two Claude slots, so Codex data never reaches its
`else` branch.

### Credits are out of scope for Codex

The Codex `credits` object carries `has_credits`, `unlimited` and `balance: null` — a capability
flag, not a measurement. `CreditUsage` requires a non-nullable `Percent`, and `CreditSeverity`
feeds it straight to `SeverityRules`, so synthesising `Percent: 0` would draw a green 0 % credits
bar. That is precisely the "never render a placeholder `0 %`" invariant.

So: no Codex credits row. If a future payload carries a usable balance or percentage, it becomes a
row then. `has_credits`/`unlimited` alone never produce one.

### Spend control carries its own period

`spend_control.individual_limit.reset_at` decodes to `2026-11-01T00:00:00Z` — a calendar-month
boundary, not a rolling week. Every existing consumer of `ScopedLimit` assumes weekly:
`UsageValues.ScopedSeverity` paces over `SevenDayPeriod`, and both `UsageValues.cs:82` and
`UsagePopup.cs:204` caption it `"{label} weekly"`. Rendering a monthly cap through that path
produces the literal string "Spend control weekly — 111 %", which is false.

`ScopedLimit` therefore gains a period, defaulting to seven days so every Claude construction site
is untouched, and the caption takes its wording from that period. This is a prerequisite change,
not something the spend-control row inherits for free.

### Timers

No new timer. The existing 5-minute `_poll` tick drives both fetches; each provider's scheduler
decides independently whether it may go, and each has its own single-flight flag. Codex gets no
`FileSystemWatcher` and no 30-second re-read, because none of its files are read.

### Availability

The Codex pair exists in exactly one of three states, held in `TrayApp` and recomputed only at the
events named here:

| State | Meaning | Icons |
|---|---|---|
| `Unknown` | No credential read has concluded yet | none |
| `Available` | A snapshot has been obtained at least once this run | shown, dimmed when stale |
| `Unavailable` | Credentials are durably gone | none |

Recompute happens at three events and no others: app start, a Codex fetch completing (either way),
and a settings save. `ApplyDisplayMode` currently derives icons from settings alone
(`TrayApp.cs:436-446`) and must take availability as a second input.

`Available` → `Unavailable` requires a *durable* signal: `auth.json` absent, or credentials
unreadable on **three consecutive** attempts. A single null from `CodexCredentialsReader` is not
enough, because that reader returns null for any IO error — including the transient file lock
while Codex itself rewrites the token. Without that rule the tray changes shape during a routine
token refresh.

Once `Available`, a 401, a 429 or a dead network dims the icons rather than removing them:
*degrade to stale, never to blank*.

## The popup

`UsagePopup`'s constructor builds the whole single-column layout inline. The per-provider block —
two window rows, scoped rows, credits, updated line — is extracted into one private method called
twice. That extraction is the only refactor in scope.

Order: platform-status banner (unchanged), Claude section, Codex section. The Codex section carries
"5-hour window", "7-day window", and the **Spend control** row through the scoped-row renderer. No
credits row (see Credits are out of scope for Codex).

`AddWindowRow` renders a `"{title}: no data"` caption for an absent window — existing behaviour, and
not a placeholder percentage, so the invariant holds. The extracted block keeps it: inside a Codex
section an absent window shows that sentence rather than vanishing, because the section only exists
at all when Codex data exists.

Two rules:

- **Section headers appear only when Codex data exists.** With no Codex the popup is exactly what it
  is today — no header, no separator, no visual change for a Claude-only user. The headers arrive
  with the second section, which is the only moment they carry information.
- **Each section owns its freshness and fetch line.** The two pipelines have independent cadences
  and failure states; a shared footer would misreport one of them. "Claude stale, Codex current"
  must be readable as exactly that.

**Height needs a policy, not a spot check.** `PopupRows.Cap` does *not* bound the Claude section:
`ForScopedLimits` returns `[.. active, .. inactive.Take(slots)]`, so every active limit renders past
the cap by design — "the cap exists to bound a list of background limits, and must never be the
reason the limit actually throttling the user is invisible". The cap bounds only *inactive* rows.
With `AutoSize` and a `PositionNearCursor` that clamps position but not size, a two-provider popup
has no upper bound on height.

The popup therefore gains a maximum height derived from the working area of the screen it opens on,
with overflow scrolling. Deciding this by eye on one machine is what the old wording assumed, and
that assumption was built on a bound that does not exist.

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

`IconRenderer.Render` **and `RenderNeutral`** both gain an optional `BadgeShape shape =
BadgeShape.Disc`, added the way `warning` was, so every Claude call site is untouched. `RenderNeutral`
matters as much as `Render` here: `TrayApp.Apply` calls it whenever `usage is null`
(`TrayApp.cs:374-384`), which is exactly the startup and partial-payload state a newly detected
provider sits in — a disc-shaped neutral badge would drop the provider encoding precisely when the
user is first looking for it.

`Draw` swaps `FillEllipse`/`DrawEllipse` for a rounded-rectangle `GraphicsPath`, and the usage wedge
requires `g.SetClip(path)` before `FillPie` or its corners spill outside the silhouette. The corner
radius at 16 px is chosen by sampling in `IconRendererTests`, not asserted here. `BadgeShape` lives
in `Tray/` beside the renderer: no pure decision depends on it, and `Core/` stays provider-agnostic.

Up to four `NotifyIcon`s — `_iconFive`, `_iconSeven`, `_codexIconFive`, `_codexIconSeven` — the
Codex pair gated by `codexDisplayMode` and by availability.

Codex tooltips name the provider and, as a correctness matter rather than a cosmetic one, must not
carry Claude's platform-status suffix. `WithStatus` becomes provider-scoped.

### The badge rule generalises

`Core/StatusSource.cs` already ships an OpenAI source, watch-filtered to
`["codex", "responses", "login", "vs code extension"]` and carrying `RaisesBadge: false` with the
comment *"an OpenAI outage never marks the tray icon"*. That was correct because no Codex icon
existed.

`RaisesBadge` therefore stops being an absolute flag and becomes a **provider affinity evaluated
against a target provider**. `StatusMonitor.BadgeDegraded(provider)` takes that target: a Claude
outage draws the exclamation on the Claude icons, an OpenAI outage on the Codex icons, and neither
crosses over.

The flag has three further consumers in `StatusDetailRows`, all of which must resolve against the
same target rather than against the old absolute sense:

- `OrderByDescending(v => v.Source.RaisesBadge)` (line 61) — suffix ordering.
- The never-dropped-suffix rule (line 71) — which source's text may be cut when space runs out.
- `ComposeTooltip`'s reserved character budget (lines 88, 94).

On a Codex icon the protected suffix is OpenAI's; on a Claude icon it is Claude's. Leaving this
implicit is how a tooltip ends up reserving budget for a source that cannot mark the icon it is on.

#### Auto-enabling the OpenAI source

The intent is: enabled once Codex usage data is detected, never overriding a value the user
explicitly saved. **That is not expressible in the current settings model.** `NormalizeFields`
materialises an entry for every registry source, defaulting `Enabled` to `source.EnabledByDefault`,
and it runs on `Save` as well as `Load` (`Settings.cs:204, 210`). So the first settings save of any
kind writes an explicit `openai: { enabled: false }`, after which an inherited default and a
deliberate opt-out are byte-identical.

`StatusSourceSettings.Enabled` therefore becomes nullable: `null` means "never decided", and
`EnabledSources()` resolves `null` against the registry default *plus* Codex availability.
`NormalizeFields` must stop materialising a non-null `Enabled` for an entry it invented. Recompute
and re-apply through `StatusMonitor` at the same three availability events listed above.

An OpenAI outage badge that nothing can draw on is not worth polling for; one that can be drawn is
worth having without configuration.

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

A second `NotificationRules` **cannot** simply be instantiated. Three things block it, and each is a
required change rather than a detail:

**1. The rules read Claude's settings internally.** `Fingerprint.Of` reads `s.UsageNotifications.Level`
(line 59), and `OnUsage` reads the same object for the level (line 124) and for suppression (line
182). A second instance handed the same `Settings` would run Codex on Claude's level and Claude's
on/off switch. The chosen `UsageNotificationSettings` must be passed in rather than reached for,
and the fingerprint must close over the one actually in use.

**2. The type owns the status machinery too.** `NotificationRules` is one `sealed partial class`
across `NotificationRules.cs` and `NotificationRules.Status.cs`; the latter's `OnStatus` iterates
*all* sources and is called once from `TrayApp.Notify`. A second instance wired symmetrically would
emit every platform-status toast twice. The usage half and the status half are split into separate
types before the second instance exists: per-provider usage rules, a single status rules object.

**3. The toast tag is a const, on both ends.** `Compose` stamps `UsageTag` (`"usage"`, line 32) into
every notification, and `TrayApp.Notify` retracts with `_toasts.Remove(NotificationRules.UsageTag)`
(`TrayApp.cs:366`). Per-provider tags — `usage:claude`, `usage:codex` — must land at the *removal*
site as well, or a Codex retraction pulls Claude's toast out of Action Center. Both directions get a
test.

With those three done, each provider has its own arming state, its own fingerprint and its own
level, and toast text names the provider.

`UsageValues.Enumerate` already walks `ScopedLimits`, so the spend-control row picks up severity and
notification behaviour once it carries its real period (see Spend control carries its own period).
On an account already at 111 % that row is red from the first render; the existing arming rule is
what prevents a toast at startup.

## Error handling

Nothing in the Codex read path throws. `CodexCredentialsReader`, `CodexUsageJson` and
`CodexUsageApiClient` swallow IO, JSON and HTTP errors and return null. A malformed `auth.json`
degrades the Codex display and cannot affect Claude's.

The token is read-only. It is never written, never refreshed, and never logged — the same invariant
the Claude credentials path carries.

`fetch.log` gains `codex:`-prefixed lines carrying outcomes and percentages only. The payload also
returns `email`, `user_id`, `account_id`, and spend-control `limit` / `used` / `balance`; none of
these is ever logged.

Spend control is logged as `spend=111%`, which `Describe` cannot currently produce: it names only
`5h`, `7d` and `credits`, bucketing everything else as `scoped=N` because scoped labels are
account-specific model names. `"Spend control"` is a fixed string the app supplies, not a payload
label, so naming it is log-safe — but `Describe` needs the explicit exception. Without that change
the line reads `scoped=1`.

## Testing

All canned; no live calls, for the reason given under Request budget.

- `CodexUsageJsonTests` — the full verified payload, absent spend control, a null balance, garbage,
  and that a `credits` block never yields a credits row.
- `CodexCredentialsReaderTests` — missing file, malformed JSON, expired `exp`.
- `CodexDisplayTests` — staleness boundaries.
- **Availability** — three consecutive credential failures drop the icons; two do not; a 401 or 429
  never does.
- `IconRendererTests` — rounded-square silhouette in all three states (`Render`, `RenderNeutral`,
  and with `warning`): corners transparent, wedge clipped inside the shape.
- **Notifications** — per-provider emission *and* retraction tags (a Codex retraction leaves
  Claude's toast standing); Codex's level and on/off read from `codexNotifications`, not Claude's;
  platform-status toasts fire once, not once per provider.
- **Scoped period** — a monthly spend-control limit captions as monthly and paces over its own
  period; existing weekly limits are unchanged.
- `Describe` — a spend-control value logs as `spend=N%` while a payload-labelled scoped limit still
  logs as `scoped=N`.
- `StatusMonitor` / `StatusDetailRows` — an OpenAI outage marks Codex icons only; a Claude outage
  marks Claude icons only; tooltip ordering and the reserved budget resolve against the icon's own
  provider.
- `Settings` — a `null` `Enabled` survives a `Save`/`Load` round trip and is still distinguishable
  from an explicit `false`.

## Success criteria

- A Codex user sees 5h, 7d and spend-control percentages without configuring anything.
- A Claude-only user sees no change whatsoever: same popup, same icons, same settings pages.
- Killing either provider's data leaves the other's display, cadence and toasts untouched.
- No credential material, identifier, or money amount reaches `fetch.log`.

## Review note

An independent review (Codex, 2026-10-13) raised the placement of `CodexCredentialsReader` and
`CodexUsageApiClient` in `Core/` as a boundary violation. It is not, and the record is kept here so
the question is not reopened: `Core/` already contains `CredentialsReader`, `UsageApiClient`,
`UsageCacheReader`, `PlatformStatusApi`, `FetchLog`, `DesktopUsageReader` and `ConfigPath`. The rule
in CLAUDE.md is WinForms-free, clock-free and thread-free — testability, not functional purity.

Every other finding from that review is folded into the sections above.

## Out of scope

- The rollout-file fallback (see Live API only).
- Any rename of the app, package id, or repository.
- A third provider, and the `IUsageProvider` abstraction that would serve one.
- Measuring the Codex endpoint's request budget.
