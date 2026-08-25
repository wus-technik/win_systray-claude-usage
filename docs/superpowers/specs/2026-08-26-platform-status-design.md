# Claude.ai platform status and taskbar outage indicator

Date: 2026-08-26
Issue: [#12](https://github.com/wus-technik/win_systray-claude-usage/issues/12)

## Problem

The tray shows *the user's* usage state, but not *Claude's* service state. When claude.ai or the
API is down or degraded, the user finds out the slow way — by hitting errors in Claude Code — and
the tray's percentages stop changing for reasons that look exactly like a fetch problem. A platform
outage should be visible at a glance, without opening the dialog.

## Solution

Poll the public Claude status page (`status.claude.com`, an Atlassian StatusPage) once a minute in
the background. When the page's own banner says anything other than "All Systems Operational",
show a warning badge on every tray icon and a status/incident section in the dialog. Everything is
best-effort: status fetches share no state with the usage fetches, and a dead status endpoint
degrades to "unknown" without touching usage behaviour.

## Data source

One unauthenticated `GET` per refresh, no token, no cookies:

```
GET https://status.claude.com/api/v2/summary.json
User-Agent: ClaudeUsageTray/<version>
```

StatusPage v2, public API. Verified shape (fields we consume; `summary.json` returns the whole page
state in one call, which is why it is preferred over `status.json` + `incidents/unresolved.json`):

```json
{
  "page":    { "id": "tymt9n04zgry", "name": "Claude", "url": "https://status.claude.com", "updated_at": "2026-08-25T22:14:37.529Z" },
  "status":  { "indicator": "none", "description": "All Systems Operational" },
  "components": [
    { "id": "rwppv331jlwc", "name": "claude.ai", "status": "operational" },
    { "id": "0qbwn08sd68x", "name": "Claude Console (platform.claude.com)", "status": "operational" },
    { "id": "k8w3r06qmzrp", "name": "Claude API (api.anthropic.com)", "status": "operational" },
    { "id": "yyzkbfz2thpt", "name": "Claude Code", "status": "operational" }
  ],
  "incidents": [
    {
      "name": "Elevated error rates on claude.ai",
      "status": "investigating",
      "impact": "major",
      "shortlink": "https://sta.us/xxxx",
      "updated_at": "2026-08-26T10:00:00Z",
      "components": [ { "name": "claude.ai" } ]
    }
  ],
  "scheduled_maintenances": []
}
```

- `status.indicator`: `"none"` | `"minor"` | `"major"` | `"critical"` — the page's own banner.
- `status.description`: human banner text ("All Systems Operational", "Minor outage", …).
- `incidents[]`: currently unresolved incidents only. `status` is
  `"investigating"` | `"identified"` | `"monitoring"`; `impact` is
  `"none"` | `"minor"` | `"major"` | `"severe"` | `"critical"`.
- `components[]` inside an incident: which components the incident touches (names shown as-is).

## Warning semantics

**Degraded = `status.indicator` is present and not `"none"`.** The page banner is the single source
of truth — exactly what the user would see at status.claude.com — which keeps the rule free of
per-component judgement calls. Consequences:

- An unknown/changed indicator value (StatusPage adding a new one) is treated as degraded: fail
  towards visible, not invisible.
- A `"minor"` incident on any component on the page (including one this user may not use, e.g.
  "Claude for Government") shows the badge. Rejected alternative: per-component filtering — it
  would require deciding which of the six components matter to which user, encoding that in
  settings, and re-evaluating on every component rename. The banner is coarser but unambiguous,
  and the dialog names the affected components anyway.
- **Recovery** is a successful fetch with `indicator: "none"`; that is what clears the badge.
- **Staleness** reuses `settings.StalenessMinutes`. When the last successful status fetch is older
  than that, the state is still *displayed* (a real outage must not vanish because *our* network
  is down) but marked "stale" in the tooltip and dialog. Known edge case: if the user's network is
  down for a long time while the platform is up, a stale badge persists until the next successful
  fetch. That is the safer direction to err.
- **Never fetched** (offline first launch, endpoint blocked): no badge; the dialog shows
  "Claude status: unavailable". The app has never asserted a disruption, so nothing is shown as one.

## Polling and failure handling

- New WinForms timer `_statusPoll`, interval **60 s** (StatusPage's recommended polling cadence),
  plus an initial fetch in the constructor and on "Refresh now".
- **Single-flight** via `_statusInFlight` (UI thread only), same pattern as `StartApiFetch`.
- Shares the existing static `HttpClient` (5 s timeout). No auth headers.
- **Failure = anything that does not yield a parseable `status` object** — timeout, network error,
  non-2xx, malformed body. Handling: keep the last-known-good `PlatformStatus`, log, and back off
  **1 → 5 → 15 min** (capped), reset on success. No rate-limit handling: this is a public endpoint
  with no per-client budget, so there is nothing to honour.
- **`StatusScheduler`** is a new pure Core class (same shape as `FetchScheduler`): `CanFetch(now)`,
  `RecordAttempt(now)`, `RecordSuccess()`, `RecordFailure(now)`; 30 s floor between attempts so a
  manual "Refresh now" cannot spam. `FetchScheduler` is deliberately *not* reused: its rolling-hour
  cap (20/h) is tuned to the Anthropic per-token budget and would block a 60/h status poll, and its
  429/`Retry-After` bookkeeping is irrelevant here.
- Completion is marshaled to the UI thread via `_sync.BeginInvoke`, exactly like the usage fetch.
- Status state (`_status`, its scheduler, its in-flight flag) is fully independent of the usage
  snapshot path: a status failure can never null, clobber, or delay usage data.

## New units (namespace `ClaudeUsageTray.Core`)

```csharp
public sealed record PlatformIncident(
    string Name, string Status, string? Impact, string? Shortlink,
    DateTimeOffset? UpdatedAt, IReadOnlyList<string> Components);

public sealed record PlatformStatus(
    DateTimeOffset FetchedAt, string Indicator, string Description,
    IReadOnlyList<PlatformIncident> Incidents)
{
    public bool Degraded => Indicator != "none";
}
```

- `sealed class StatusScheduler` — pure, clock-injected budget gate as above.
- `static class PlatformStatusApi` — `static Task<PlatformStatus?> FetchAsync(HttpClient http,
  DateTimeOffset now, CancellationToken ct)`. Mirrors `UsageApiClient`: never throws; returns null
  on timeout, network error, non-2xx, non-object root, or missing/invalid `status.indicator`.
  Parsing tolerance per incident: skip entries without a non-empty `name`; missing `status` →
  `"unknown"`; missing/empty `impact` → null; malformed `updated_at` → null (the incident survives);
  `components` names collected from the nested array, unnamed entries dropped.

## TrayApp integration

- Fields: `PlatformStatus? _status`, `StatusScheduler _statusScheduler`, `Timer _statusPoll`
  (60 s), `bool _statusInFlight`.
- `StartStatusFetch()` mirrors `StartApiFetch()` (gate check → log skip, single-flight,
  `RecordAttempt`, `Task.Run`, `BeginInvoke` back).
- `OnStatusFetchCompleted(PlatformStatus? result)`: null → `RecordFailure` + log; non-null →
  `RecordSuccess`, `_status = result`, log, `Render()`.
- `Render()` computes `bool degraded = _status is { Degraded: true }` and
  `bool statusStale` (against `StalenessMinutes`); both flow into `Apply` and the tooltip.
- "Refresh now" menu item: `Refresh(); StartApiFetch(); StartStatusFetch();`
- `Dispose`: `_statusPoll.Dispose()` alongside the other timers.

## Tray icon warning badge

`IconRenderer.Render` and `IconRenderer.RenderNeutral` gain a `bool warning = false` parameter
(plumbed through the private `Draw`). When set, a corner badge is drawn **last** (over the rim),
so it works on all five base states: green/orange/red badges, dimmed badges, and the neutral
grey `—` icon (an outage with no usage data must still be visible).

Geometry, all relative to icon size `s` (badge diameter `d = 0.45s`, i.e. 7.2 px at the 16 px
system size):

- A circle centred at `(s − d/2, s − d/2)` — inscribed in the bottom-right corner, the conventional
  notification-badge position, away from the centred digit.
- Fill `Color.White`; 1 px rim `Color.FromArgb(30, 30, 30)` so the badge separates from both the
  host disc and the taskbar (white + dark rim reads on dark *and* light taskbars, the same
  dual-legibility rule the digit halo was built for).
- The exclamation mark is **drawn as shapes, not text**: a 0.22d-wide stem spanning 0.40d of the
  badge's vertical centre, and a 0.22d dot below it, both in the rim colour. A 5–6 px font would be
  below the legibility floor this project already established for the digit — small marks are
  verified at 1:1, and a shape-drawn `!` is the only form guaranteed to read at 16 px.
- The badge is **never dimmed**, even when `dimmed: true`: dimming encodes stale *usage* data, and
  the service state is fresh from its own fetch — a real outage must not fade.

Verification: render all base states with and without the badge at 16, 20, and 24 px and inspect at
1:1 (extend `docs/icon-preview.html` with the variants), per the standing lesson that magnified
previews flatter small marks.

## Tooltip

- Degraded: append ` · Claude: {description}` to the existing tooltip (e.g.
  `5h · 42% · resets in 1h 3m · Claude: Minor outage`), plus `(stale)` when the status state is
  stale. Applies to both the data-bearing tooltip and the fixed "No Claude usage data yet" tooltip.
- Not degraded, or never fetched: nothing appended — normal operation stays unobtrusive.
- The existing 127-char trim handles the longer lines.

## Dialog (UsagePopup)

`UsagePopup` gains a `PlatformStatus? platformStatus = null` parameter. A status block is added at
the **top** of the popup, above the usage rows, so a disruption is the first thing seen (and it
still shows in the "no usage data" state):

- **Never fetched:** grey label `Claude status: unavailable`.
- **Operational** (`indicator: "none"`): grey label `Claude status: All Systems Operational`
  (the page's own `description`).
- **Degraded:** label `Claude status: {description}` in `Color.DarkOrange` for `minor`,
  `Color.Firebrick` for `major`/`critical`/unknown, followed by the active incidents:
  - Up to **3** incident rows, each
    `{Name} — {status}{impact}{components} · updated {RelativeTime.Ago(UpdatedAt)}`, e.g.
    `Elevated error rates on claude.ai — investigating · major · claude.ai · updated 4m ago`.
    `impact` omitted when `none`/missing; `components` as `· claude.ai, Claude API` when present.
    Incident `status` shown with initial capital; unknown raw values shown as-is.
  - `+N more` line when truncated (same pattern as the scoped-limit rows).
  - A `LinkLabel` to `https://status.claude.com` (and the incident `shortlink` when present) opened
    via `Process.Start(UseShellExecute: true)`, wrapped so it can never throw.
- **Stale** (any state): append ` · stale`, using `settings.StalenessMinutes`.

## Observability

Status fetches go to the existing `FetchLog` (`%APPDATA%\ClaudeUsageTray\fetch.log`), same
skip/attempt/outcome discipline as the usage fetch:

- `status: skip: budget/backoff gate not open yet`
- `status: attempt: GET summary.json`
- `status: ok: indicator=none (All Systems Operational) incidents=0`
- `status: degraded: indicator=major incidents=2: <name1>, <name2>`
- `status: error: no usable response; backing off`

Incident names are public status-page content (never account data), so they are loggable under the
existing "never account-specific data" rule.

## Ride-along changes

- README: a "platform status" row in *What you get*, a third bullet in *Where the data comes from*
  (public status page, no auth, no token involved), and a design-doc table entry.
- `docs/icon-preview.html`: warning-badge variants for 1:1 verification.
- Version → `0.7.0` (new feature).

## Testing

- `PlatformStatusApiTests` (fake `HttpMessageHandler`, mirroring `UsageApiClientTests`):
  - 200 all-operational (empty `incidents`) → non-degraded, description carried through.
  - 200 degraded with two incidents: one with components and timestamps, one without → parsed
    fields exact, `Degraded` true.
  - 200 with an incident missing `name` → that incident skipped, the rest kept.
  - 200 with malformed `updated_at` → incident kept with `UpdatedAt` null.
  - 200 missing `status` object / non-object root / malformed body → null.
  - Unknown indicator value → `Degraded` true (fail towards visible).
  - 404, 503, timeout (cancellation), network exception → null, never throws.
  - Asserts exact URL, `User-Agent` set, and **no** `Authorization` header.
- `StatusSchedulerTests`: 30 s floor (second attempt at +29 s blocked, at +30 s allowed), failure
  backoff 1 → 5 → 15 min, cap at 15 min on continued failures, success resets the streak.
- `IconRenderer` smoke tests extended: `Render`/`RenderNeutral` with `warning: true` produce the
  requested size and are not blank (consistent with the existing drawing-not-asserted convention).
- TrayApp glue (timer wiring, single-flight, marshaling) is manually verified, consistent with
  prior specs.

## Acceptance

- [ ] Claude.ai status is retrieved from `status.claude.com` (public API, no credentials).
- [ ] Current service state is visible in the dialog in all states (operational, degraded,
      unavailable, stale).
- [ ] Active incident details (name, status, impact, affected components, age) are shown when
      applicable.
- [ ] Status refreshes automatically every 60 s in the background.
- [ ] While a disruption exists, every visible tray icon carries the warning badge — including the
      neutral no-data icon — and the tooltip names the disruption.
- [ ] The badge disappears automatically on the first successful fetch reporting `none`.
- [ ] Status endpoint/network failures keep last-known-good state, back off, log, and never affect
      usage data, icons, or the rest of the app.

## Out of scope

- Per-component filtering or user-selectable components to watch (banner is the scope).
- Scheduled maintenances — `summary.json` carries them, but maintenance is not a disruption; the
  data is available in the response if this is ever wanted.
- OS-level notifications (toast/WinRT) on outage start; the badge plus dialog covers the issue's
  "immediately visible" requirement.
- A settings toggle or configurable interval for status polling.
- Push-based updates; the StatusPage v2 API offers no subscription mechanism.