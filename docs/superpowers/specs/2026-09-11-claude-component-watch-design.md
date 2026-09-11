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
3. **No component names to show.** `PlatformStatus.Components` carries only the *non-operational*
   entries, so on a healthy day the app cannot tell the user what the page even lists.

## Design

### 1. The payload carries every component name

`PlatformStatus` gains a member, declared as an `init` property rather than a seventh positional
parameter so the eight existing construction sites (one production, seven test) stay as they are and
the default is the safe one:

```csharp
public sealed record PlatformStatus(
    string SourceId, DateTimeOffset FetchedAt, string Indicator, string Description,
    IReadOnlyList<PlatformIncident> Incidents,
    IReadOnlyList<PlatformComponent> Components)            // non-operational only — unchanged
{
    public IReadOnlyList<string> ComponentNames { get; init; } = [];
}
```

`PlatformStatusApi` fills it in the same pass that builds `Components`, with two rules stated here
because the payload shape allows either reading:

- **Order is the array's own order.** StatusPage's `components` is a flat array carrying a
  `position` field and per-group ordering; reproducing the page's visual order would mean sorting by
  group and position for no gain. The list is a reference caption, not a rendering of the page.
- **Entries with `group: true` are dropped.** A group is a heading, not a component: it never
  appears in `Components` (which carries non-operational *children*) and never appears in an
  incident's component list. Offering a group name as a filter token would hand the user a token
  that matches nothing — a filter that silently watches nothing is the exact failure §2's safety
  argument exists to exclude.

Nothing but the settings dialog reads `ComponentNames`. `Components` keeps its "no caller can
render a wall of healthy components" guarantee, and no display path changes shape.

The names come from the payload rather than a `string[]` compiled into `StatusSourceRegistry`
because **labels come from the payload** is a standing invariant of this app: a renamed or added
component must show up with no app update.

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
excluded by name. It cannot hide an outage the page could not classify. One narrow gap survives, and
is accepted: `PlatformStatusApi.ReadComponents` drops entries missing `name` or `status`, so a
payload whose *only* affected component is malformed while an unrelated one is degraded would leave
`Identifies()` true with nothing matching, and the badge off. A malformed StatusPage payload is not
a case worth widening the rule for.

**Two consistency limits are deliberate and stay:**

- **A badge change caused solely by a filter edit is silent.** `NotificationRules.Status.OnStatus`
  rebaselines and returns nothing when its settings fingerprint changes, while `BadgeDegraded()` is
  recomputed from scratch on the same `Render()`. So widening the filter against retained degraded
  data lights the icon with no toast, and narrowing it clears the icon with no toast. That is
  correct — the user's own edit is not news — but it is new behaviour: before this change a filter
  edit moved nothing outside the popup. Same class as the paced-badge/absolute-toast divergence
  already recorded in the repo.
- **Staleness does not reach the badge.** `Accept` keeps the last-known-good status when a fetch
  fails, and `BadgeDegraded()` takes no clock. The popup and tooltip mark that state "· stale"; the
  icon does not, and continues to warn. Unchanged here, and the safer direction — a real outage must
  not vanish because *our* network is down — but it is the one axis on which the badge still differs
  from the popup.

`RaisesBadge` is untouched: an OpenAI outage still never marks the icon, for the unrelated reason
that it says nothing about Claude usage headroom.

### 3. A real "Platform status" group in the dialog

There is no Claude block to mirror today, and the OpenAI one is not a block: `_watchOpenAi` and its
components box are appended to `Heading("Colour thresholds")`, between the pace-colours checkbox and
the preview swatch, while `_notifyOpenAi` sits under `Heading("Notifications")`. This change
introduces the group that was missing and moves the OpenAI pair into it:

```
Platform status
  [x] Watch Claude status
      Components (comma-separated, blank = all)
      [                                        ]
      Page lists: claude.ai, Claude Console (platform.claude.com), …     (greyed)
  [ ] Watch OpenAI status
      Components (comma-separated, blank = all)
      [ codex, responses, login, vs code extension ]
      Page lists: …                                                       (greyed)
```

New controls: `watchClaude`, `claudeComponents`, `claudeComponentsCaption`,
`claudeComponentsHint`, and `openAiComponentsHint` for symmetry. The notify checkboxes stay where
they are, under **Notifications**, where `notifyClaude` and `notifyOpenAi` already sit together —
moving them would split the notification settings across two groups to fix a smaller asymmetry than
it creates. `_preview` and `_previewCaption` stay under **Colour thresholds**, where they belong.

`Layout()` builds `TabIndex` from a hand-written control array; the new controls are inserted there
in visual order, Claude before OpenAI.

Wiring matches OpenAI exactly. Unchecking `watchClaude` **disables** `claudeComponents` and
`notifyClaude` rather than clearing them, so a user who turns the page off and on again keeps their
choices. `Save` now writes all three fields of the `claude` entry instead of preserving `enabled`
and `components` from the clone.

Turning Claude status off entirely becomes reachable from the UI. It was already reachable by hand
in `settings.json`; the dialog no longer pretends otherwise. With it off there is no badge at all —
which is the user's explicit choice, made in a checkbox labelled with what it does.

### 4. The names are a reference caption, not a prefill

`StatusSourceRegistry.Claude.DefaultComponents` stays `[]`. Nothing changes for a user who never
opens the dialog, no component list ships in the binary, and **blank = all** stays literally true:
the box shows exactly what is stored, and what is stored is exactly what the box shows.

Discovery comes from a greyed caption under each box listing the page's current components, so the
user can see what there is to exclude and type or paste the ones that apply. The caption keeps
working *after* the user narrows the list — it still names a component Anthropic adds later, which
a one-time prefill into the box could not. When no names are known the caption reads
`Page lists: not fetched yet` and the box behaves as before.

The names reach the dialog as a new constructor parameter, frozen at open time for the same reason
`desktopSource` is frozen — a caption that rewrites itself under a half-made edit is worse than one
briefly out of date:

```csharp
public SettingsDialog(Settings settings, bool canRunAtStartup, bool runAtStartup,
    Func<Settings, bool> save, UpdateOptions updates, bool desktopSource,
    IReadOnlyDictionary<string, IReadOnlyList<string>> componentNames)
```

`TrayApp` supplies it from a small per-source cache of the last `ComponentNames` seen, updated
whenever a status fetch is filed, **not** read live from `StatusMonitor`. `StatusMonitor` holds no
entry for a disabled source, so `Status("claude")` is null exactly when the user has opened the
dialog to turn Claude back on and narrow it — the moment the names are most wanted.

One limitation of the comma grammar is worth recording, since §1's whole argument is that Anthropic
owns these labels: `ComponentFilter.Parse` splits on commas with no escaping, so a future component
name containing a comma cannot be entered whole. Matching is substring, so any comma-free fragment
of that name works; nothing else is needed.

### Data flow after the change

```
summary.json ──► PlatformStatusApi ──► PlatformStatus { Components, ComponentNames }
                                              │
StatusMonitor.Entry { Source, Filter, Status } ┤
     │                                         │
     ├── BadgeDegraded() ──── IsRelevant ──────► tray icon marker       (changed)
     ├── Sources() ────────── IsRelevant ──────► popup rows, tooltip    (unchanged)
     ├── Sources() ────────── IsRelevant ──────► NotificationRules      (unchanged)
     │
     └─ TrayApp component-name cache ──────────► SettingsDialog caption (new)
```

`Settings.EnabledSources()` already feeds `StatusMonitor.ApplyEnabled`, which keeps a surviving
source's `Status` while replacing its `Filter`, and `TrayApp.Refresh()` ends in `Render()` — so
editing the filter re-evaluates the badge on the next render with no refetch. That holds for a
source that stays enabled. A source **disabled and re-enabled** loses its entry and comes back with
`Status = null`, so the badge is off until the next fetch lands, up to one poll cycle. Accepted: the
user just told the app to stop watching and start again, and the alternative is resurrecting a
status of unknown age.

A filter edited while a fetch is in flight is safe without further work: `ApplyEnabled` keeps the
same `Entry` and swaps only `Filter`, so the completion files normally and is evaluated against the
new filter. The pre-existing disable/re-enable-during-fetch behaviour documented in
`StatusMonitor.Accept` is unchanged and out of scope here.

## Testing

Pure functions and named controls, as everywhere else in this codebase.

- **`StatusMonitorTests`** — the reversal, on a payload modelled on the live one (banner `minor`,
  `Claude Cowork` degraded): filter `["Claude Code"]` → no badge; empty filter → badge; filter
  `["Cowork"]` → badge; an OpenAI outage under any filter → no badge; `watchClaude` off (source not
  in `EnabledSources`) → no badge during a full outage; a source re-enabled → no badge until a fetch
  is accepted. Plus the fail-towards-visible pair: degraded page with no components and no incidents
  under a narrow filter → badge; incident naming no components under a narrow filter → badge.
- **`PlatformStatusApiTests`** — `ComponentNames` carries all six including the operational ones, in
  array order; entries with `group: true` are excluded; `Components` still carries only the
  non-operational entry; a payload with no `components` key yields both empty and does not throw.
- **`NotificationRulesStatusTests`** + **`StatusMonitorTests`** as a pair — a filter widened against
  a retained degraded payload turns the badge on while `OnStatus` rebaselines and returns no toast;
  `Notify = false` with a matching filter gives a badge and no toast.
- **`SettingsDialogTests`** — `watchClaude` unchecked disables `claudeComponents` and `notifyClaude`
  without clearing them; the box shows the stored filter verbatim and is blank for `[]`; the hint
  caption lists the supplied names and falls back to "not fetched yet" for an empty list; saving
  stores exactly the typed tokens; saving with `watchClaude` off stores `enabled: false` and keeps
  the components; a hand-written six-name filter survives an untouched open/save round-trip
  unchanged.
- **`SettingsTests`** — a `claude` entry with `enabled: false` and a components list survives a
  load/save round-trip; a malformed `claude` entry still degrades to defaults without throwing.

No live calls to either status page; canned payloads only.

## Rejected alternatives

- **Leave the badge unfiltered, filter only the popup and toasts.** Safest reading of "the main
  warning must never be disarmed", and what the code does today. Rejected because it does not solve
  the problem: the complaint is the icon marking for Cowork, and a filter the user sets in the
  dialog that visibly does nothing to the icon is worse than no filter at all.
- **Prefill the box with the live component names**, collapsing an untouched prefill back to `[]` on
  save so a later-added component is still watched. Rejected on two counts: the box and
  `settings.json` would disagree after a save, invisibly; and the discovery it buys lasts exactly
  until the user narrows the list, after which a newly added component appears nowhere in the
  dialog. The caption in §4 gives the same discovery permanently and needs no save-time transform.
- **Ship the six names as `Claude.DefaultComponents`.** No payload field, no constructor parameter.
  Rejected: it hardcodes a label list the page owns, breaks on the first rename, and writes today's
  six names into every user's settings on first save.
- **Read the caption's names live from `StatusMonitor`.** One less field in `TrayApp`. Rejected:
  the monitor drops the entry for a disabled source, so the names would be missing precisely when
  the user opens the dialog to re-enable and narrow.
- **Per-component severity instead of a filter** — derive the badge from the worst *watched*
  component's own status rather than from the page banner. Rejected again, for the reason the
  original platform-status design gives: the banner is what the user sees at status.claude.com, and
  a rule that disagrees with the page is a rule nobody can check. The filter narrows *which*
  disruptions count; the banner still decides how loud they are.

## Documentation to update

- `README.md` — the `statusSources` table row and section: `claude.components` is no longer
  "JSON-only" and no longer "never the badge"; the paragraph under **Only Claude's status can mark
  the tray icon** needs rewriting, and the dialog path is now **Settings → Platform status**.
- `docs/superpowers/specs/2026-08-26-platform-status-design.md` — the *Warning semantics* bullet
  that rejects per-component filtering records the superseded decision and points here.
- `CHANGELOG.md` — behaviour change: a Claude disruption outside your watched components no longer
  marks the tray icon.
