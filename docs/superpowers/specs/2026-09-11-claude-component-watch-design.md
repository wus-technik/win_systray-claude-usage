# Watching only the Claude components you use

Date: 2026-09-11
Supersedes parts of: [2026-08-26-platform-status-design.md](2026-08-26-platform-status-design.md),
[2026-08-26-openai-status-source-design.md](2026-08-26-openai-status-source-design.md)

## Problem

`status.claude.com` lists six components, and they no longer describe one product:

```
claude.ai                              operational
Claude Console (platform.claude.com)   operational
Claude API (api.anthropic.com)         operational
Claude Code                            operational
Claude Cowork                          degraded_performance   ← live on 2026-09-11
Claude for Government                  operational
```

A Claude Code user does not care that Cowork is degraded; a Cowork user does not care that the
Console is. Either one currently gets the same thing: the page banner flips to `minor`, the tray
icon takes the warning marker, and a toast fires. The badge is the app's single loudest signal, and
today an outage in a product the user has never opened is enough to raise it. Train that, and the
marker stops meaning anything.

The machinery to fix this already exists and already runs for Claude. `ComponentFilter`,
`StatusSource.DefaultComponents`, `StatusDetail.IsRelevant`, and the per-source `components` key in
`settings.json` were built for OpenAI's 25 components and are generic. Three things are missing:

1. **No UI.** The `claude` filter is a JSON-only setting the README calls "advanced"; nobody edits
   it, and nothing tells the user the component names exist.
2. **The badge ignores the filter.** `StatusMonitor.BadgeDegraded()` consults only
   `status.Degraded`, deliberately — the comment says so, and the reason it gives is precisely that
   the Claude filter has no dialog control. The popup rows, the tooltip suffix and the toasts all go
   through `IsRelevant`; the badge is the one place that does not.
3. **No component names to work from.** `PlatformStatus.Components` carries only the
   *non-operational* entries, so on a healthy day the app does not know what the page even lists.

## Design

### 1. The payload carries every component name

`PlatformStatus` gains a second list:

```csharp
public sealed record PlatformStatus(
    string SourceId, DateTimeOffset FetchedAt, string Indicator, string Description,
    IReadOnlyList<PlatformIncident> Incidents,
    IReadOnlyList<PlatformComponent> Components,      // non-operational only — unchanged
    IReadOnlyList<string> ComponentNames);            // every component, in page order
```

`PlatformStatusApi` fills `ComponentNames` in the same pass that builds `Components`. Nothing but
the settings dialog reads it; `Components` keeps its "no caller can render a wall of healthy
components" guarantee, and no display path changes shape.

This exists so the dialog can offer real names without a hardcoded list in the binary. **Labels come
from the payload** is a standing invariant of this app, and a renamed or added component must still
show up with no app update — a `string[]` of today's six names compiled into
`StatusSourceRegistry` would break that on the first rename.

### 2. The badge honours the watch filter

```csharp
/// <summary>Whether the tray icon should carry the warning marker: a badge-raising source whose
/// disruption is relevant under its own watch filter. The filter reaches the badge as of
/// 2026-09-11 — before that it was deliberately bypassed, because the Claude filter had no dialog
/// control and a JSON-only key must not disarm the tray's main warning. It has one now, and a
/// filter the user set in the dialog that narrows the popup but not the icon is a filter that does
/// not work.</summary>
public bool BadgeDegraded()
    => _entries.Any(e => e.Source.RaisesBadge
                         && e.Status is { } s && StatusDetail.IsRelevant(s, e.Filter));
```

`IsRelevant` already returns false for a healthy page, so the `Degraded` check is subsumed rather
than dropped. After this change the badge, the popup rows, the tooltip and the toasts all decide
relevance with the same function; a state the user can see in the popup as "outside your watched
components" can no longer be marking the icon at the same time.

The fail-towards-visible rules inside `IsRelevant` are what make this safe, and they are unchanged:

- an empty filter watches everything (still the default — see §4);
- an incident that names no components counts as watched;
- a degraded page whose payload identifies nothing at all counts as watched.

So the filter can only ever suppress a disruption the page itself attributed to a component the user
excluded by name. It cannot hide an outage the page could not classify.

`RaisesBadge` is untouched: an OpenAI outage still never marks the icon, for the unrelated reason
that it says nothing about Claude usage headroom.

### 3. The dialog mirrors the OpenAI block

Under the Claude heading, the same three controls the OpenAI block has, with the same names and
wiring:

| Control | Name | Behaviour |
|---|---|---|
| `CheckBox` "Watch Claude status" | `watchClaude` | writes `statusSources.claude.enabled` |
| `TextBox` + caption "Components (comma-separated, blank = all)" | `claudeComponents` | writes `statusSources.claude.components` |
| `CheckBox` "Notify when Claude platform status changes" | `notifyClaude` | exists today; moves under the new checkbox |

Unchecking `watchClaude` **disables** the text box and `notifyClaude` rather than clearing them —
the same rule as OpenAI, so a user who turns the page off and on again keeps their choices. `Save`
now writes all three fields of the `claude` entry instead of preserving `enabled` and `components`
from the clone.

Turning Claude status off entirely is a real option in the UI as of this change. It was already
reachable by hand in `settings.json`; the dialog no longer pretends otherwise.

### 4. Prefill from the live page, store "all" when untouched

`StatusSourceRegistry.Claude.DefaultComponents` stays `[]`. Nothing changes for a user who never
opens the dialog, and no component list ships in the binary.

The dialog prefills the text box with the live component names when the stored filter is empty
(i.e. "watch everything"), so the user sees what there is to exclude and deletes the lines that do
not apply. The names arrive as a new constructor parameter:

```csharp
public SettingsDialog(Settings settings, bool canRunAtStartup, bool runAtStartup,
    Func<Settings, bool> save, UpdateOptions updates, bool desktopSource,
    IReadOnlyList<string> claudeComponentNames)
```

frozen at open time from `StatusMonitor.Status("claude")?.ComponentNames ?? []`, for the same reason
`desktopSource` is frozen: a control that rewrites itself under a half-made edit is worse than one
briefly out of date. If the app has never fetched the page the box is blank, and the caption still
explains that blank means all.

**On save, a prefill the user did not touch is stored as `[]`, not as six literal names.** The
comparison is order-insensitive and case-insensitive against the same list that was prefilled. This
is the rule that keeps the prefill from quietly freezing the page's 2026-09-11 shape into every
user's settings file: a stored explicit list can never match a component Anthropic adds later, so an
untouched prefill would silently stop watching a future "Claude Desktop" the day it appears. A user
who deliberately narrows the list accepts that trade for the components they named; a user who just
clicked Save did not.

### Data flow after the change

```
summary.json ──► PlatformStatusApi ──► PlatformStatus { Components, ComponentNames }
                                              │
StatusMonitor.Entry { Source, Filter, Status } ┤
     │                                         │
     ├── BadgeDegraded() ──── IsRelevant ──────► tray icon marker      (changed)
     ├── Sources() ────────── IsRelevant ──────► popup rows, tooltip   (unchanged)
     ├── Sources() ────────── IsRelevant ──────► NotificationRules     (unchanged)
     └── Status("claude").ComponentNames ──────► SettingsDialog prefill (new)
```

`Settings.EnabledSources()` already feeds `StatusMonitor.ApplyEnabled`, which keeps a surviving
source's `Status` while replacing its `Filter` — so editing the filter in the dialog re-evaluates the
badge on the next render without a refetch. `NotificationRules.Status` already re-fingerprints on a
settings change, so widening the filter against an unchanged payload does not toast; that stays
true, and now covers the badge implicitly since both read the same `Filter`.

## Testing

Pure functions and named controls, as everywhere else in this codebase.

- **`StatusMonitorTests`** — the reversal, on a payload modelled on the live one (banner `minor`,
  `Claude Cowork` degraded): filter `["Claude Code"]` → no badge; empty filter → badge; filter
  `["Cowork"]` → badge; an OpenAI outage with any filter → no badge. Plus the fail-towards-visible
  pair: degraded page with no components and no incidents under a narrow filter → badge; incident
  naming no components under a narrow filter → badge.
- **`PlatformStatusApiTests`** — `ComponentNames` carries all six including the operational ones,
  in page order; `Components` still carries only the non-operational one; a payload with no
  `components` key yields both empty and does not throw.
- **`SettingsDialogTests`** — `watchClaude` unchecked disables `claudeComponents` and `notifyClaude`
  without clearing them; the box prefills from the passed names when the stored filter is empty and
  from the stored filter otherwise; saving an untouched prefill stores `[]`; saving an edited list
  stores exactly the edited tokens; saving with `watchClaude` off stores `enabled: false` and keeps
  the components.
- **`SettingsTests`** — a `claude` entry with `enabled: false` and a components list survives a
  load/save round-trip, and a malformed `claude` entry still degrades to defaults without throwing.

No live calls to either status page; canned payloads only.

## Rejected alternatives

- **Leave the badge unfiltered, filter only the popup and toasts.** Safest reading of "the main
  warning must never be disarmed", and what the code does today. Rejected because it does not solve
  the problem: the complaint is the icon marking for Cowork, and a filter that the user sets in the
  dialog and that visibly does nothing to the icon is worse than no filter at all.
- **Ship the six names as `Claude.DefaultComponents`.** Discoverable with no new constructor
  parameter and no payload field. Rejected: it hardcodes a label list the page owns, breaks on the
  first rename, and writes today's six names into every user's settings on first save — the failure
  the §4 normalisation exists to prevent.
- **Store the prefill verbatim when the user saves it unchanged.** Simpler, no comparison. Rejected
  for the silent-stop-watching failure in §4: a new component would never badge, and nothing in the
  UI would say why.
- **Per-component severity instead of a filter** — derive the badge from the worst *watched*
  component's own status rather than from the page banner. Rejected again, for the reason the
  original platform-status design gives: the banner is what the user sees at status.claude.com, and
  a rule that disagrees with the page is a rule nobody can check. The filter narrows *which*
  disruptions count; the banner still decides how loud they are.

## Documentation to update

- `README.md` — the `statusSources` table row and section: `claude.components` is no longer
  "JSON-only" and no longer "never the badge"; the paragraph under **Only Claude's status can mark
  the tray icon** needs rewriting, and the dialog path is now **Settings → Watch Claude status**.
- `docs/superpowers/specs/2026-08-26-platform-status-design.md` — the *Warning semantics* bullet
  that rejects per-component filtering records the superseded decision and points here.
- `CHANGELOG.md` — behaviour change: a Claude disruption outside your watched components no longer
  marks the tray icon.
