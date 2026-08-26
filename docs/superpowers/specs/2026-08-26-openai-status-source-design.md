# A second platform status source: OpenAI

Date: 2026-08-26
Supersedes parts of: [2026-08-26-platform-status-design.md](2026-08-26-platform-status-design.md)

## Problem

The tray watches one platform. A developer who runs Claude Code *and* Codex has a second service
whose outages look, from the desk, exactly like the first one's: requests hang, nothing explains
why. The status machinery already built for Claude — poll, banner, incident rows, badge — is
generic in everything but its hardcoded strings.

Two constraints shape the answer. First, an OpenAI outage says nothing about Claude usage
headroom, so it must never mark the usage badge. Second, OpenAI's status page lists 25 components,
most of which (`Sora`, `Ads API`, `FedRAMP`, `ChatGPT Atlas`, `Voice mode`) are noise for a Codex
user; watching the page-wide banner unfiltered would train the user to ignore the indicator.

## Data source

Verified live on 2026-08-26, unauthenticated, no cookies:

```
GET https://status.openai.com/api/v2/summary.json
User-Agent: ClaudeUsageTray/<version>
```

The page is incident.io behind a StatusPage-v2 compatibility shim (its logo URLs resolve to
`incident-io-status-page-logos`), which makes the surface *partial*:

| Field | status.claude.com | status.openai.com |
|---|---|---|
| `status.indicator` / `status.description` | yes | yes, same vocabulary (`none` / `All Systems Operational`) |
| `components[]` with per-component `status` | yes (6) | yes (25) |
| `incidents[]` | yes (`[]` when quiet) | **key absent entirely** |
| `scheduled_maintenances[]` | yes | absent |
| `/api/v2/incidents/unresolved.json` | yes | **404, HTML error page** |
| `/api/v2/incidents.json` | yes | yes, but all states incl. `resolved`, and no `shortlink`, no per-incident `components` |

Consequences taken as given by this design:

- The existing parser already tolerates a missing `incidents` key (`TryGetProperty` throughout), so
  OpenAI yields `Incidents = []` with no code change and no throw.
- Detail rows for OpenAI therefore come from `components[]`, not from incidents. A second request to
  `incidents.json` was rejected: it doubles that source's request rate, needs `resolved`/`postmortem`
  filtering the other page does not, and still carries no shortlink.
- Whether the shim populates `incidents` during a live OpenAI incident could not be verified — there
  was none. The code assumes nothing either way; if the key appears, incident rows take precedence
  automatically.

## Design

### Sources are data

```csharp
public sealed record StatusSource(
    string Id,                                  // "claude" | "openai" — the settings.json token
    string DisplayName,                         // popup header and tooltip suffix
    string SummaryUrl,
    string PageUrl,
    string PageLabel,                           // LinkLabel text, e.g. "status.claude.com"
    bool RaisesBadge,                           // Claude: true. OpenAI: false.
    IReadOnlyList<string> DefaultComponents);   // filter used when settings omit the key
```

`StatusSources.Claude`, `StatusSources.OpenAi`, `StatusSources.All`, and
`StatusSources.ById(string id)` — which matches ids with `StringComparer.OrdinalIgnoreCase` and
returns `null` for anything unknown, because settings normalization depends on that answer.

No user-supplied URLs: the app never fetches a host it does not ship, and every payload shape it
parses is one that was verified by hand. The registry is generic so that `RaisesBadge` and the
watch filter are *data a test can assert* rather than branches inside `UpdateIcons` — not because a
third source is planned. Exactly Claude and OpenAI are supported, accepted, and tested here.

`RaisesBadge` being a *field of the source* is the load-bearing part. "An OpenAI outage never marks
the tray icon" becomes a value a test asserts, not an `if` inside `UpdateIcons`.

### Model

```csharp
public sealed record PlatformComponent(string Name, string Status);

public sealed record PlatformStatus(
    string SourceId,                                  // new: self-describing downstream
    DateTimeOffset FetchedAt, string Indicator, string Description,
    IReadOnlyList<PlatformIncident> Incidents,
    IReadOnlyList<PlatformComponent> Components)      // new
{
    public bool Degraded => Indicator != "none";
}
```

`Components` keeps only entries whose `status` is present and **not** `"operational"` — an all-green
page carries an empty list, so no code path can render a wall of healthy components. `Degraded` is
unchanged, for the reason the original spec gives: an indicator StatusPage has not invented yet must
fail towards visible. `PlatformIncident` is unchanged.

### Fetching

`PlatformStatusApi.FetchAsync(HttpClient, StatusSource, DateTimeOffset, CancellationToken)`. The
source supplies the URL and the result is stamped with `SourceId`. This removes the current
`internal` endpoint-override overload — tests point a throwaway `StatusSource` at their local
listener instead, which is one indirection fewer than today. The parser body changes only to read
`components[]`.

### `Core/StatusMonitor.cs`

Clock-free, thread-free. One entry per enabled source: its watch filter, a `StatusScheduler`, a
last-known-good `PlatformStatus?`, an in-flight flag.

```csharp
public sealed record SourceView(
    StatusSource Source, PlatformStatus? Status, IReadOnlyList<string> Filter);

IReadOnlyList<StatusSource> TakeDue(DateTimeOffset now);                  // gate + floor + backoff +
                                                                          // single-flight; records the attempt
void Accept(string sourceId, PlatformStatus? result, DateTimeOffset now); // null => RecordFailure
PlatformStatus? Status(string sourceId);
IReadOnlyList<SourceView> Sources();                                      // registry order, for the popup
bool BadgeDegraded(DateTimeOffset now);                                   // RaisesBadge sources only
void ApplyEnabled(IReadOnlyList<(StatusSource Source, IReadOnlyList<string> Filter)> enabled);
```

`SourceView` is what the popup and the tooltip builder consume, so neither of them reaches back into
`Settings`. The monitor holds the filters because the tooltip needs them; `BadgeDegraded` does
**not** consult them (see **Component filter**).

Two contracts that are easy to leave implicit and expensive to get wrong:

- **`Accept` validates the id.** A result whose `SourceId` disagrees with the `sourceId` argument is
  dropped and logged, never stored. The id comes from `TakeDue`, so this should be unreachable —
  which is exactly why it must not silently file an OpenAI outage under Claude if it ever happens.
- **`Accept` for a source that is no longer enabled is discarded**, including its in-flight flag and
  scheduler state. `ApplyEnabled` can run (dialog closed) while a fetch is outstanding, and a late
  completion must not resurrect a removed source's entry.

`TakeDue` is named for its mutation on purpose. A pure `DueSources` query paired with a separate
`RecordAttempt` is a call that can be forgotten, and forgetting it means hammering a public endpoint.

`ApplyEnabled` **preserves** the state of sources that stay enabled. Toggling OpenAI on must not
blank Claude's banner — and with it the badge — for a poll cycle. A newly added source is immediately
due, so it fills in within a second of the dialog closing.

`TrayApp` keeps only the HTTP call and the `BeginInvoke` marshalling; `_status`, `_statusScheduler`
and `_statusInFlight` move into the monitor.

### Scheduling

The existing single 60 s timer is unchanged. Each tick, `TakeDue` returns 0–2 sources and `TrayApp`
starts one call per source. Both enabled means 2 requests/minute to two different hosts, each with
its own 30 s floor and its own 1/5/15-minute failure backoff. Per-source schedulers give the
isolation invariant structurally: an OpenAI timeout cannot back off, null, or delay Claude.

`fetch.log` lines gain a source tag: `status[openai]: degraded: indicator=minor components=2`. The
no-money, no-account-data rule is unaffected; status data is public.

### Component filter

Per-source, not an OpenAI special case:

```json
"statusSources": {
  "claude": { "enabled": true,  "components": [] },
  "openai": { "enabled": false, "components": ["codex", "responses", "login", "vs code extension"] }
}
```

- **Case-insensitive substring match** on the component name, not exact names. `"codex"` catches
  `Codex API`, `Codex Web`, and `Codex in ChatGPT Desktop` in one token and survives the renames this
  page demonstrably does (`Codex in ChatGPT Desktop` was created 2026-03).
- **Empty list means watch everything.** That is Claude's default; its six components are all
  relevant. The field exists there for symmetry and costs nothing.
- The OpenAI list above is `DefaultComponents`, used when the settings key is absent.

**Token normalization**, specified so the implementation has nothing left to decide: split the
dialog's text on commas, `Trim()` each token, drop empty and whitespace-only tokens, de-duplicate
with `StringComparer.OrdinalIgnoreCase`. A list that normalizes to zero tokens *is* the empty list
and therefore watches everything. Matching is
`name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0` — ordinal, never culture-sensitive,
because these are product names from a US-English page and a Turkish locale must not change what
`"login"` matches.

### Relevance

The banner is page-wide, so the filter must decide *relevance*, not merely which rows to draw —
otherwise a Sora-only outage paints a coloured "Partial outage" header above zero rows. Three
states, not two:

| Page state | Affected components identifiable? | Watched one affected? | Popup | Tooltip |
|---|---|---|---|---|
| `none` | — | — | grey banner, verbatim | nothing |
| degraded | yes | yes | coloured banner + rows for watched items only | suffix appears |
| degraded | yes | no | grey banner + `· outside your watched components` | nothing |
| degraded | **no** | — | **coloured banner**, no rows | **suffix appears** |

The fourth row is the one that matters. A page can report a disruption while every component still
reads `operational` and no incident names a component — incident.io banners do not have to be mapped
to components at all. Classifying that as "outside your watched components" would hide a real outage
behind a filter the user set for noise reduction. **Unclassifiable degradation is always relevant**,
which is the same "fail towards visible" rule the original spec applies to unknown indicators.

`IsRelevant` therefore returns true when the filter is empty, when a watched component is affected,
or when the page is degraded and nothing identifies what it affects.

Incidents are matched by their own `components[]`. **An incident naming no components counts as
watched** — "unclassified" must not mean "invisible".

### The filter never gates the badge

`BadgeDegraded` is `RaisesBadge && Degraded`, full stop. It does not consult the filter for any
source.

For OpenAI this is moot (`RaisesBadge: false`). For Claude it is a deliberate asymmetry with the
tooltip: the Claude filter has no dialog control, so letting it gate the badge would mean a
README-only JSON key could silently disarm the tray's single most important warning. A narrower
tooltip is a preference; an unmarked icon during a Claude outage is a defect. The cost is that a
filtered Claude source can show a warning badge whose tooltip says nothing about status — acceptable,
and it only occurs for a user who hand-edited JSON to ask for exactly that.

### Display — `Core/StatusDetail.cs`

A new pure module, so the WinForms class stops deciding anything:

```csharp
public sealed record StatusRow(string Text, string? Link);

static IReadOnlyList<StatusRow> Rows(PlatformStatus status, IReadOnlyList<string> filter,
                                     DateTimeOffset now, int max);
static int HiddenCount(PlatformStatus status, IReadOnlyList<string> filter, int max);
static string Header(StatusSource source, PlatformStatus? status, bool relevant, bool stale);
static string TooltipSuffix(IReadOnlyList<SourceView> sources, DateTimeOffset now, int stalenessMinutes);
static bool IsRelevant(PlatformStatus status, IReadOnlyList<string> filter);
```

`DescribeIncident` and the take-3 rule move here verbatim from `UsagePopup`, which shrinks to
turning rows into labels.

**Row precedence per source — applied *after* filtering, not before:** incidents that survive the
filter (today's rows, with the `Details` shortlink), otherwise watched non-operational components
(`Responses — Degraded performance`; snake_case unfolded, page vocabulary kept), otherwise header
only. Claude keeps its richer output, OpenAI gets components, and neither path is source-specific
code.

Filtering first is what makes the precedence correct: a page can carry incidents that all fall
outside the watch list while a watched component *is* non-operational. Choosing the list before
filtering would show that source as degraded with zero rows — the same empty-header failure the
relevance rule exists to prevent.

**Popup layout:** one block per enabled source, registry order, Claude first. Headers read
`Claude status: …` / `OpenAI status: …`. Two healthy sources means two grey lines above the usage
rows. Collapsing healthy sources into one combined line was rejected: each banner is quoted verbatim
from its page, and merging two pages' wording invents a sentence neither page wrote.

**Tooltip:** relevant-degraded sources append in order, `RaisesBadge` first —
`… · Claude: Partial outage · OpenAI: Minor service disruption`.

Ordering alone does **not** protect the Claude suffix from the 127-character `NotifyIcon.Text` limit:
`TrimTooltip` cuts the finished string, so a long usage tooltip plus two suffixes can truncate both.
The rule is therefore a drop, not a trim — **if appending the non-badge suffixes would exceed the
limit, they are omitted entirely** before `TrimTooltip` runs. A half-cut `· OpenAI: Minor serv…` is
worse than no suffix, and the badge-raising source's suffix must survive because it is the text that
explains the marker on the icon.

**Badge:** `IconRenderer.Render(warning: monitor.BadgeDegraded(now))`. `IconRenderer` does not
change; the warning marker keeps meaning "Claude is degraded, which is why your numbers may have
stopped moving."

### Settings

```csharp
public sealed class StatusSourceSettings
{
    public bool Enabled { get; set; }
    public List<string>? Components { get; set; }   // null => the source's DefaultComponents
}

public Dictionary<string, StatusSourceSettings> StatusSources { get; set; } = new();
```

Per-entry fallback needs a tolerant read; plain deserialization cannot deliver it.
`Settings.Load` catches `JsonException` and returns *full* defaults, so with a straight
`Dictionary<string, StatusSourceSettings>` a single bad `components` value would also reset
thresholds, display mode and staleness. The property is therefore deserialized as
`Dictionary<string, JsonElement>` and normalized entry by entry:

- Unknown ids (no `StatusSources.ById` match) are dropped.
- Missing ids are filled from the registry defaults: `claude` enabled with an empty filter, `openai`
  disabled with `DefaultComponents`.
- An entry whose `enabled` or `components` value has the wrong shape resets **that entry** to its
  registry default; the other entry and every unrelated setting survive.

An existing `settings.json` with no `statusSources` key keeps today's runtime behaviour exactly —
Claude watched, OpenAI off, badge unchanged. The next `Save` writes the key with both entries, so
the file changes even though nothing the user sees does.

**Dialog:** a `Watch OpenAI status` checkbox beside the existing options, plus an enabled-when-checked
text field *Components (comma-separated, blank = all)* pre-filled with the default. No live component
list in the dialog: it would need a fetch at open time and would break on rename.

The Claude entry has **no** dialog control. Its filter is an advanced, README-documented JSON key
that narrows popup rows and the tooltip and never the badge; the dialog round-trip must preserve it
untouched rather than resetting it to the default on save.

## Error handling

Unchanged invariants, now per source:

- Nothing in the read path throws. A missing `components` key, a non-array, an entry without a name,
  a missing `status` — each degrades that entry, never the fetch.
- A failed source keeps its last-known-good state, backs off on its own schedule, and logs.
- A status failure never nulls, clobbers, or delays usage data.
- The two sources share no failure state.

## Testing

Pure Core, so all of it is unit-testable:

- `PlatformStatusApiTests` — canned OpenAI payload: indicator parsed, `incidents` key absent yields
  `[]`, `components[]` filtered to non-operational, `SourceId` stamped.
- `StatusMonitorTests` — per-source floor and backoff; an OpenAI failure leaves Claude's next-due time
  and last-known-good untouched; `ApplyEnabled` preserves surviving sources and makes a new one
  immediately due; single-flight per source; a result with a mismatched `SourceId` is dropped; a
  completion for a source disabled while in flight is discarded and does not recreate its entry;
  `BadgeDegraded` ignores the filter and ignores non-`RaisesBadge` sources.
- `StatusDetailTests` — row precedence applied after filtering (incidents all filtered out but a
  watched component non-operational still yields rows), substring matching incl. the three `codex`
  components, token normalization (whitespace-only tokens, duplicates, a list that normalizes to
  empty watches all), an incident with no components counts as watched, all four relevance rows
  including unclassifiable-degraded, and the tooltip dropping the non-badge suffix whole rather than
  letting it be truncated.
- `SettingsTests` — absent key yields today's runtime behaviour; unknown source id dropped; a
  malformed `statusSources` entry resets that entry while thresholds, display mode and staleness
  survive; the dialog round-trip preserves a hand-written Claude filter.
- `UsagePopupTests` — offscreen render (`CreateControl()`, never `Show()`) with two sources, one
  degraded-and-relevant, one degraded-but-filtered-out.

## Success criteria

- [ ] With OpenAI off, visible and runtime behaviour is unchanged: one poll, one banner, same badge
      rule. The persisted `settings.json` gains a `statusSources` key on the next save; nothing else
      about the file changes.
- [ ] With OpenAI on, the popup shows both banners, Claude first, each verbatim from its page.
- [ ] An OpenAI disruption affecting a watched component colours its banner, lists the affected
      components, and adds a tooltip suffix — and leaves the tray icon unmarked.
- [ ] An OpenAI disruption outside the watched components shows a grey banner saying so, with no
      tooltip suffix and no rows.
- [ ] An OpenAI disruption that identifies no affected components at all is treated as relevant:
      coloured banner and a tooltip suffix, despite the filter.
- [ ] A Claude filter set by hand narrows the popup rows and the tooltip but still leaves the badge
      warning during a Claude outage.
- [ ] A Claude disruption still raises the badge and still lists incidents with `Details` links.
- [ ] Killing one source's network (bad host) leaves the other source's cadence and data untouched.
- [ ] An existing `settings.json` upgrades in place with no visible change.

## Out of scope

- User-supplied status page URLs. Curated sources only.
- A third source. The registry supports it; nothing else is designed for it.
- Scheduled maintenances — Claude sends them, OpenAI does not; maintenance is not a disruption.
- OS-level notifications on outage start.
- `incidents.json` as a fallback for OpenAI incident names, for the reasons under **Data source**.
- Configurable poll interval.

## Supersession

The platform-status design of 2026-08-26 lists "per-component filtering or user-selectable components
to watch (banner is the scope)" as out of scope. This design overturns that for a specific reason
that did not exist then: OpenAI's page carries 25 components across products a Codex user does not
use, so an unfiltered page-wide banner would be noise rather than signal. The banner remains the
scope for *what is shown*; the filter governs only *what is emphasised*.
