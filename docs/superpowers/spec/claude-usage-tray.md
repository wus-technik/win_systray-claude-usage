# Claude Usage Tray — Spec

> **Amended (v0.2):** the icon design changed from a stroked progress ring to a **filled badge** for 16 px readability — see `docs/superpowers/specs/2026-07-24-icon-readability-design.md` (issue #2). Icon-geometry statements below describe the superseded v0.1 design; fill semantics (arc sweep from 12 o'clock, 5h clockwise / 7d counter-clockwise, color = severity) are unchanged.

**Status:** Draft for implementation
**Date:** 2026-07-23
**Owner:** foellmann@wus-technik.com

A small Windows **notification-area (system tray)** application that shows the
current user's Claude **5-hour** and **7-day** usage as percentages, next to the
clock. It is a **passive reader** of the usage data that Claude Code already
caches on the local machine — it performs **no network calls and touches no
credentials**. Values are rendered into tray icons, tinted green/orange/red by
severity, with a right-click switch to show the 5h window, the 7d window, or
both.

---

## 1. Goals & Non-Goals

### Goals
- Give an at-a-glance view of Claude 5h and 7d usage from the Windows tray.
- Stay strictly **ToS-clean**: read only local data that the official Claude
  Code client writes; never call the undocumented usage endpoint, never read or
  use OAuth tokens.
- Easy per-user install with painless auto-updates (no admin rights).

### Non-Goals (v1)
- **Actively refreshing** usage via the OAuth endpoint
  (`api.anthropic.com/api/oauth/usage`). Using the local OAuth token from a
  third-party product violates Anthropic's ToS (as of Feb 2026) and the endpoint
  is undocumented. Explicitly out of scope. See §9.
- Weekly-scoped / per-model (Opus, Sonnet, Fable) sub-limits.
- Spend / extra-usage credit display.
- Non-Windows platforms.
- Browser integration (a browser-extension + native-messaging approach was
  considered and dropped in favour of this native tray app).

---

## 2. Platform & Tech Stack

| Aspect | Decision |
|---|---|
| OS | Windows 10/11 (x64) |
| Language / runtime | C# / .NET (latest LTS), WinForms |
| Tray API | `System.Windows.Forms.NotifyIcon` |
| JSON | `System.Text.Json` |
| Icon rendering | GDI+ (`System.Drawing`) |
| Tests | xUnit |
| Packaging / updates | **Velopack** (`vpk`), per-user install + delta auto-update |

**Why WinForms:** `NotifyIcon` is the standard tray API; WinForms gives the
lightest path to a tray-only app with dynamic icon rendering. WPF was rejected
as heavier with no benefit here; Go+systray was rejected because dynamic
text-in-icon rendering and high-DPI handling are more manual.

---

## 3. Data Source (the contract)

The single source of truth is the file Claude Code maintains at:

```
%USERPROFILE%\.claude.json   →   key: cachedUsageUtilization
```

Relevant shape (only these fields are read):

```jsonc
{
  "cachedUsageUtilization": {
    "fetchedAtMs": 1784815176543,        // when Claude Code last refreshed the cache
    "utilization": {
      "five_hour":  { "utilization": 1,  "resets_at": "2026-07-23T18:39:59Z" },
      "seven_day":  { "utilization": 13, "resets_at": "2026-07-27T15:59:59Z" }
    }
    // other fields (limits[], extra_usage, spend, seven_day_opus, weekly_scoped,
    // accountUuid, …) exist but are IGNORED in v1.
  }
}
```

- `utilization` is an **integer percentage used** (0–100+) for each window.
- `resets_at` is an ISO-8601 UTC timestamp for when that window rolls over.
- `fetchedAtMs` is epoch milliseconds; drives the "last updated / stale" logic.

**Read-only. No writes, no HTTP, no tokens.** The file is written by Claude Code
roughly every few minutes while it is running; when Claude Code is not running,
the values are simply the last cached values and the app surfaces their age.

---

## 4. Architecture (small, testable units)

Core logic is separated from WinForms glue so it can be unit-tested without a
running tray.

| Unit | Responsibility | Testable? |
|---|---|---|
| `ConfigPath` | Resolve `.claude.json` path from `USERPROFILE`; overridable (env/setting) for tests | pure |
| `UsageCacheReader` | Read file → parse `cachedUsageUtilization` → `UsageSnapshot`; tolerate missing file/key | yes (fixtures) |
| `UsageSnapshot` (+ `WindowUsage`) | Domain model: `FetchedAt`, `FiveHour {Percent, ResetsAt}`, `SevenDay {Percent, ResetsAt}` | n/a (data) |
| `Severity` | Pure `(percent, thresholds) → Green \| Orange \| Red` | yes |
| `RelativeTime` | Pure formatting: "resets in 2h 13m", "updated 4m ago" | yes |
| `IconRenderer` | `(window, percent, severity, dpiSize) → Icon` — draws the progress ring + center digit via GDI+; DPI-aware | smoke test |
| `Settings` | Load/save display mode, thresholds, run-at-startup, update-feed | yes (round-trip) |
| `StartupRegistration` | Toggle the per-user `Run` registry key | manual |
| `TrayApp` | WinForms glue: `NotifyIcon`(s), watcher + timer, context menu, popup | manual |

**Data flow:** `FileSystemWatcher`/timer → `UsageCacheReader` → `UsageSnapshot`
→ (`Severity` + `RelativeTime` + `IconRenderer`) → `NotifyIcon` text/icon/tooltip.

---

## 5. Behavior

### 5.1 Refresh strategy
- A `FileSystemWatcher` on `.claude.json` (debounced ~500 ms, since the file may
  be rewritten in bursts) triggers a re-read when Claude Code updates it.
- A fallback timer (~30 s) re-renders so relative strings ("resets in…",
  "updated Xm ago") and staleness stay current even when the file doesn't change.
- "Refresh now" in the context menu forces an immediate re-read.

### 5.2 Severity thresholds
- Default: **< 50 % → green, 50–85 % → orange, > 85 % → red.**
- Thresholds are stored in `Settings` and editable there (no UI editor required
  in v1; documented JSON keys are enough).

### 5.3 Staleness
- Compute `age = now − fetchedAtMs`.
- If `age` exceeds a threshold (default **15 min**), the icon is **dimmed** and
  the tooltip appends the age ("stale · updated 22m ago").
- If a window's `resets_at` has passed while data is stale, the tooltip flags
  "awaiting refresh" (the cached percent may be for the prior window).

### 5.4 Missing / unavailable data
- No file, or no `cachedUsageUtilization` key, or unparseable:
  neutral icon — empty grey ring with a **"—"** in the center — tooltip
  **"No Claude usage data yet — run Claude Code."** No crash, no error dialog.

---

## 6. Tray UI

### 6.1 Icons

Each icon is a **progress ring** rendered into the icon bitmap with three
independent channels:

- **Ring fill = usage.** The arc starts at 12 o'clock and fills proportionally to
  the integer percentage (a thin sliver at low %, nearly closed near 100 %; `>100 %`
  clamps to a full ring). The 5h ring fills **clockwise**, the 7d ring
  **counter-clockwise** — a subtle secondary tell of which window is which.
- **Ring color = `Severity`.** Green `< 50 %`, orange `50–85 %`, red `> 85 %`
  (§5.2). A faint same-hue track shows the unused remainder.
- **Center digit = the window.** A single centered digit — **`5`** for the
  5-hour window, **`7`** for the 7-day window. One glyph keeps the center crisp
  at 16 px, and the digit maps directly to the window name (no legend needed).
- The **exact integer percentage is not drawn in the icon** — it lives in the
  per-icon tooltip and the left-click popup (§6.3). The icon conveys *how full*
  plus severity at a glance.
- Rendering is **DPI-aware**: render at the system small-icon size
  (`SM_CXSMICON`, scaled per monitor) so the ring and digit stay crisp.
- **Order is fixed: 5 (left), 7 (right).** Per-icon tooltip carries the full
  state: `5h · 42% · resets in 3h 10m` / `7d · 13% · resets in 3d 20h`.

### 6.2 Display mode (per-icon context switch)
Right-click radio group: **Show 5h / Show 7d / Show both.**
- "Show both" (default) → two icons.
- "Show 5h" / "Show 7d" → a single icon for that window.
- Selection is persisted in `Settings` and applied by adding/removing the
  corresponding `NotifyIcon`.

### 6.3 Left-click popup
A compact popup near the tray showing:
- Both windows as labeled progress bars with their percentage.
- Reset countdowns ("resets in …").
- "Last updated Xm ago" (with the stale indicator if applicable).

### 6.4 Context menu
- **Display:** ○ Show 5h · ○ Show 7d · ● Show both (radio)
- Open claude.ai usage page (browser)
- Run at startup ✓ (toggles `StartupRegistration`)
- Refresh now
- *(disabled info item)* "Updated Xm ago"
- Quit

### 6.5 Accessibility
- Severity is never conveyed by color alone — it is redundantly encoded by the
  **ring fill amount**, and the tooltip and left-click popup always carry the
  full textual state (window, exact percentage, reset countdown, staleness).

---

## 7. Settings

Persisted to `%APPDATA%\ClaudeUsageTray\settings.json`:

| Key | Meaning | Default |
|---|---|---|
| `displayMode` | `"fiveHour"` \| `"sevenDay"` \| `"both"` | `"both"` |
| `thresholds` | `{ "orange": 50, "red": 85 }` | as shown |
| `stalenessMinutes` | minutes before data is flagged stale | `15` |
| `runAtStartup` | mirror of the registry Run key | `true` |
| `configPathOverride` | optional explicit path to `.claude.json` (mainly tests) | unset |

---

## 8. Packaging & Updates (Velopack)

- **Build/release:** `dotnet publish` → `vpk pack` produces a per-user setup
  `.exe`, delta packages, and a release feed. Version comes from the `.csproj`.
- **Install:** **per-user, no admin rights.** Installs under `%LOCALAPPDATA%`,
  creates a Start-menu shortcut, and writes the per-user run-at-login registry
  key (via `StartupRegistration`). Install/update/first-run hooks are handled by
  `VelopackApp.Build().Run()` as the very first call in `Main`.
- **Auto-update:** on launch and periodically, `UpdateManager` checks the feed,
  downloads **delta** updates, and applies them on next restart (silent).
- **Update feed (confirm during review):** default **GitHub Releases (private
  repo)** — consistent with the org's `gh`-based Git setup; using a token
  (`GH_TOKEN` from `gh auth token`) for CI/non-interactive publishes. A
  **network file share** (`\\server\share\claude-usage-tray`) is a supported
  one-line-swap alternative if a fully-internal feed is preferred.
- **Signing:** optional in v1; signing the setup `.exe` with an org-trusted cert
  removes the SmartScreen warning. Not required to ship.

---

## 9. Compliance & Security Notes

- **ToS-clean by construction.** The app reads only `cachedUsageUtilization`
  from `%USERPROFILE%\.claude.json`, a cache the official Claude Code client
  writes. It does **not** read `.credentials.json`, does **not** read or use any
  OAuth token, and makes **no** network requests to Anthropic (or anywhere).
- The undocumented `api.anthropic.com/api/oauth/usage` endpoint is **explicitly
  not used** — calling it from a third-party product would violate Anthropic's
  ToS. This is the reason active-refresh is a non-goal (§1).
- No secrets are stored or transmitted by the app. Settings contain only display
  preferences.
- The only outbound network traffic the app makes at all is the **Velopack
  update check** to the configured feed (GitHub / file share) — never to
  Anthropic.

---

## 10. Testing

xUnit, focused on the pure/core units:

- `UsageCacheReader` against sample `.claude.json` fixtures:
  valid, missing `cachedUsageUtilization`, missing file, malformed JSON, stale
  (`fetchedAtMs` far in the past), and boundary percentages (0, 100, >100).
- `Severity`: threshold boundaries (49/50/85/86, custom thresholds).
- `RelativeTime`: seconds/minutes/hours/days, "just now", past timestamps.
- `Settings`: JSON round-trip incl. defaults for missing keys.
- `IconRenderer`: smoke test — produces a non-empty `Icon` of the requested size
  for representative inputs.

WinForms glue (`TrayApp`, `NotifyIcon` wiring, popup) is thin and verified
manually.

---

## 11. Open Items / Future (post-v1)

- Active refresh (only if a supported/compliant mechanism becomes available).
- Weekly-scoped / per-model sub-limits and Opus window.
- Spend / extra-usage credit display.
- Settings UI (edit thresholds/staleness without hand-editing JSON).
- Signed release + optional MSI/MSIX packaging if fleet-wide MDM/GPO
  distribution is later required.
