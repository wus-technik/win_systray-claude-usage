# Desktop notifications for status changes and limits turning red

Date: 2026-09-05
Issue: [#14](https://github.com/wus-technik/win_systray-claude-usage/issues/14)
Blocked by (resolved): [#17](https://github.com/wus-technik/win_systray-claude-usage/issues/17)

## Problem

The tray is passive. It renders the truth accurately and waits for someone to look at the taskbar,
which is exactly the wrong shape for the two moments that actually matter: the platform going down
while you wonder why requests hang, and a limit crossing into red while you are mid-task. Both are
already computed every poll; neither reaches the user unless the user asks.

Two moments deserve an interruption:

1. **A watched status page changes state**, in both directions.
2. **A limit crosses into red**, whichever rule decided it — the pace ratio or the absolute ceiling.

The hard part is not raising a toast. It is raising exactly one, at the right moment, from data
whose freshness and provenance vary poll to poll. The tray already tracks all of that; the design's
job is to make the notifier respect it rather than re-derive it.

## Why this waited for #17

#14 was written when there was one status page. #17 made status per-source, and that changes the
trigger's shape rather than its wiring: transitions become per-source, the badge rule and the
"is anything wrong" rule stop being the same question (an OpenAI outage deliberately never marks the
badge), OpenAI sends no `incidents` array so incident *names* have to fall back to component
statuses, and the watch filter introduces a third notion of "relevant" between "degraded" and
"shown". Designing the notifier first would have meant defining the transition rule twice.

## Design

### Where the decisions live

Everything that decides is a pure or clock-free type in `Core/`, following `FetchScheduler`,
`SeverityRules` and `StatusMonitor`. `Tray/` raises the toast and decides nothing.

| Type | Kind | Responsibility |
| --- | --- | --- |
| `Core/UsageValues.cs` | pure | Enumerates the notifiable values of a snapshot with their severities |
| `Core/NotificationRules.cs` | stateful, clock-free | Remembers what it last saw; turns readings into transitions |
| `Tray/ToastPresenter.cs` | WinForms/WinRT | Shows a toast; routes a click to the popup |

### `Core/UsageValues.cs`

`Enumerate(snapshot, settings, now)` returns one row per notifiable value:

```csharp
public sealed record UsageValue(string Key, string Label, int Percent, Severity Severity);
```

Keys are stable across polls and are what the transition state is filed under: `"5h"`, `"7d"`,
`"credits"`, and each scoped limit's `Label` — which `ScopedLimit` already documents as its dedup
key, so this introduces no new identity scheme. Absent values produce no row, upholding the
"absent data means no row" invariant: a limit the account does not have can never be notified about.

Severity comes from `SeverityRules.ForSettings`, with the elapsed fraction from
`TimeMarker.ElapsedFraction` — five hours for the 5-hour window, seven days for the 7-day and the
scoped weekly limits.

**Credits are the exception, and `UsageValues` must preserve it.** `UsagePopup.AddCreditRow` uses
`ParseSeverity(credits.PayloadSeverity) ?? SeverityFor(credits.Percent, settings)`: the payload's
own severity wins, because it can encode account state — a spend cap already reached — that a
percentage cannot express, and only falls back to the configured thresholds when the payload says
nothing. Credits carry no reset time, so the fallback has a null elapsed fraction and is purely
absolute. Flattening this into "credits use the thresholds" would make the toast disagree with the
bar next to it, which is the exact drift this type exists to prevent.

**This type exists to prevent drift, not to save typing.** That computation is currently written
out twice, in `TrayApp` (~line 327) and `UsagePopup` (lines 98 and 193). A third copy inside the
notifier would make "a toast fires exactly when a row turns red in the popup" a coincidence that
holds until someone edits one of the three. `TrayApp` and `UsagePopup` are therefore rewired onto
`UsageValues` as part of this work — the one place where this feature changes existing code, and the
reason it is in scope.

### `Core/NotificationRules.cs`

Holds the remembered state, takes `now` from the caller, returns what to show:

```csharp
public sealed record Notification(string Title, string Body, string Argument);
```

`Argument` is the toast's activation payload, so a future second toast action does not need a new
plumbing path. Today there is exactly one value.

#### Usage transitions

`OnUsage(DisplayChoice choice, Settings settings, DateTimeOffset now)` returns zero or one
notification, applying these rules in order:

1. **Not armed yet → record as baseline, notify nothing.** See *Arming* below.
2. **The notification fingerprint changed → re-baseline every key, notify nothing.** See
   *Configuration changes* below.
3. **Stale → nothing, and the remembered state is left untouched.** Not merely a skip: because
   nothing is recorded, a crossing that happened during the stale gap is still compared against the
   pre-stale state and fires once when fresh data returns. Recording during staleness would swallow
   it silently.
4. **A different `UsageSource` than last time → re-baseline, notify nothing.** Claude Code and the
   Desktop history are different measurements of different things; switching between them moves a
   number without anything having happened. This case bypasses the timestamp comparison in rule 5
   entirely: the two streams' timestamps are not comparable.
5. **`snapshot.FetchedAt` not strictly newer than the last evaluated one → nothing.** Equal counts
   as not newer, so the 30 s cache re-read of an unchanged file evaluates nothing.
6. Per key, notify when severity crossed **from below the configured level to at or above it**.
7. Keys absent from this evaluation are **evicted**.
8. All crossings from one evaluation coalesce into **one** toast.

Rule 5 is a belt-and-braces guard rather than the primary defence against a live → cache failover.
The primary defence already exists: cache and live both feed `_cliSnapshot` through
`SnapshotPrecedence.IsNewer`, which is strictly `>`, so an older `.claude.json` read cannot replace
a fresher live snapshot in the first place. **The invariant to state plainly is that notifications
only ever see adopted snapshots.** Rule 5 then covers what precedence does not: the Desktop-history
stream, which is selected by `SourceSelection` rather than merged by precedence.

**First sight is always baseline.** A key not seen before is recorded without notifying. This covers
startup into an already-red state, and also a scoped limit the payload only just began reporting —
an ambiguous case decided deliberately: the payload beginning to mention a limit is not the limit
having moved, and the alternative fires a toast for something that may have been red for days.

Combined with rule 7, a scoped limit that vanishes from the payload and returns is therefore
re-baselined silently. `ScopedLimit.Label` stays the key, as the code already documents it as the
dedup key — but it is payload-derived, so a **renamed** label reads as one key vanishing and another
appearing. The cost of that is one missed toast after a rename, which is the right side to fail on:
inventing a second identity scheme out of `ModelId` and the surface would make the notifier disagree
with the popup about what counts as the same limit.

#### Arming

The startup baseline cannot simply be "the first evaluation". `TrayApp`'s constructor calls
`Refresh()` — which renders from the `.claude.json` cache — and only then `StartApiFetch()`. A cache
reading hours old that says 40 %, followed seconds later by a live fetch that says 91 %, is two
evaluations with an advancing timestamp and an unchanged source: rules 3–5 all pass and the user is
toasted about a limit that was already red when the app launched. That is precisely the storm the
issue forbids.

The usage trigger is therefore **armed only once the first live fetch cycle has settled** — the
first `OnApiFetchCompleted`, whatever its outcome — or immediately if no live fetch can be attempted
at all (no credentials file, or a rejected token), since then the cache or the Desktop history is
all there will ever be. Everything before that point records baseline and notifies nothing.

#### Configuration changes

Severity is recomputed from live settings on every evaluation, so a settings edit can move a value
across the line without the value having moved. Changing `StalenessMinutes` upward can un-stale an
old snapshot and make its old numbers notifiable; changing `Thresholds` or `PaceColors` can change
every severity at once; changing `NotifyLevel` from `Red` to `Orange` makes an already-orange value
cross a line that just moved under it.

All of these are the same failure, so they get one fix rather than three. `NotificationRules` holds
a **fingerprint** of the settings that can change a verdict — thresholds, pace mode, staleness
allowances, and the notify level — and when it changes, every key is silently re-baselined against
the new rules. The user's own edit is never news.

**Leaving the level is remembered but silent.** Red is one-way. A limit leaves red mainly because
its window reset — a clock event the popup already predicts — and announcing it would roughly double
the toast count for no decision the user has to make. The exit is still recorded, so the next
crossing fires again.

Level is `NotifyLevel { Orange, Red }`. `Red` (the default) means a crossing into `Severity.Red`;
`Orange` means a crossing into `Orange` **or** `Red` — so a value going green → red under level
`Orange` notifies once, not twice, and a value going orange → red under level `Orange` does not
notify a second time. The state is the level-relative predicate "at or above", not the raw severity.

#### Status transitions

`OnStatus(SourceView view, Settings settings)` returns zero or one notification per source, keyed by
`Source.Id`.

The state is **`StatusDetail.IsRelevant(status, filter)`**, not `status.Degraded`. For Claude the
filter is empty by default, so the two are identical. For OpenAI it means a Sora or Ads API outage
stays silent while a Codex one notifies — which is the entire purpose of the watch filter, and
without it the OpenAI opt-in trains the user to dismiss us. `IsRelevant` also carries the
"nothing in the payload identifies what is affected" fail-towards-visible rule, which the notifier
therefore inherits rather than re-implements.

This is a deliberate divergence from #17's rule that **the filter never gates the badge**, and the
reasons that rule exists do not transfer. The badge is the tray's single always-visible warning and
Claude's filter has no dialog control, so a README-only JSON key must not be able to disarm it. A
notification is not always-visible, is separately switchable, and for OpenAI the filter *is*
dialog-controlled — the user who narrowed it said what they wanted to hear about.

- **First reading per source is baseline.** Launching into a degraded platform is not a transition.
- **A disabled source drops its state**, so re-enabling re-baselines instead of firing a toast about
  an outage that started while the source was off.
- **A changed watch filter re-baselines that source.** `StatusMonitor.ApplyEnabled` deliberately
  keeps a source's `PlatformStatus` across a settings change, so widening the OpenAI filter to
  include `codex` would otherwise flip `IsRelevant` from false to true against an unchanged payload
  and toast about an outage the user merely started watching. The per-source state is therefore
  re-baselined whenever that source's filter, `enabled`, or `notify` value changes — the status-side
  counterpart of the usage fingerprint.
- **A failed fetch needs no rule.** Verified against `StatusMonitor.Accept`: a null result records a
  failure on the scheduler and returns without touching `entry.Status`, so last-known-good survives,
  the value does not change, and no transition exists. A dead endpoint degrades to stale, never to a
  false recovery.

#### Text

Titles and bodies are the page's own words, never a hardcoded model or component list.

- Degraded: the incident names where the page sends them; component names with their statuses where
  it does not, which is the OpenAI case. Composed through `StatusDetail`, so the toast and the popup
  cannot describe the same outage differently.
- Usage: the labels from `UsageValues`, joined — "5-hour limit and Fable weekly are now red".

**Recovery text must not overclaim.** `IsRelevant` going true → false does not mean the page is
healthy: a watched Codex incident can end while an unrelated Sora incident is still open, leaving
`status.Degraded` true. Saying "All systems operational" there would be a plain falsehood about a
page the user can go and read. The two cases are therefore distinguished by `status.Degraded`:

- `Degraded == false` → "All systems operational".
- `Degraded == true` → the watched disruption is over but the page is not clear; the text says so,
  naming what remains only to the extent `StatusDetail` already words it.

Per the existing logging rule, notification text may name limits and percentages but never money
amounts, currency, or account-specific model names beyond the payload labels already shown on
screen.

### Settings

Per-source notify lives with the source, so a source's configuration stays in one place:

```jsonc
"statusSources": {
  "claude": { "enabled": true, "notify": true, "components": [] },
  "openai": { "enabled": false, "notify": true, "components": ["codex", "responses", "login", "vs code extension"] }
}
```

`notify` defaults to `true`: someone who enabled OpenAI did it to learn about OpenAI outages, and a
second opt-in to reach the obvious outcome is friction, not safety. It is stored independently of
`enabled` so that turning a source off and on again does not silently discard the user's choice.

The usage trigger gets its own object, because it carries a level:

```jsonc
"usageNotifications": { "enabled": true, "level": "red" }
```

`NotifyLevel` is a new enum, camelCase-serialized like `DisplayMode`. It deliberately does not reuse
`Severity`, whose `Green` member would be meaningless as a notification level.

Both follow the established per-field fallback: an invalid `level` resets to `red`, a missing or
malformed `notify` to `true`, and every other setting survives untouched.

**Quiet hours are Windows' job.** A real toast already obeys Focus Assist and Do Not Disturb, and
the AUMID gives the app its own entry in Windows notification settings, so the user can mute or
un-mute us with the OS control they already know. Duplicating that in-app would mean a second clock,
its own scheduling logic, and two more dialog rows to reimplement a feature the platform ships.

### Dialog

`SettingsDialog` is explicit rather than generic — Claude's source has no controls, OpenAI has a
checkbox plus a components box. A new "Notifications" group follows that shape:

- ☑ Notify when a limit turns `[Red only ▾]` — the combo offers "Red only" and "Orange and red"
- ☑ Notify when Claude platform status changes
- ☑ Notify when OpenAI platform status changes — disabled when "Watch OpenAI status" is off, the way
  `_openAiComponents.Enabled = _watchOpenAi.Checked` already works

### `Tray/ToastPresenter.cs`

A real Windows toast, not `NotifyIcon.ShowBalloonTip`. The AUMID that normally makes this expensive
is already present: Velopack's Start Menu shortcut carries `velopack.WusTechnik.ClaudeUsageTray`.

Measured against the installed app on a developer machine before this design was written:

| Check | Result |
| --- | --- |
| Shortcut carries an AUMID | `velopack.WusTechnik.ClaudeUsageTray` (`Get-StartApps`) |
| Toast shows for an unpackaged app under it | Yes, `ToastNotifier.Setting` = `Enabled` |
| Persists in Action Center | Yes, read back via `History.GetHistory` |
| Click activation without a COM activator | **Yes** — in-process `Activated`, arguments delivered |
| `Dismissed` reaches the running process | Yes (`TimedOut`) |
| Builds on `net10.0-windows10.0.19041.0` | Yes, no warnings |
| Unregistered AUMID (a `dotnet run`) | No toast; `COMException` on one run, silent no-op on another |

In-process activation is what removes the usual cost. The documented route for an unpackaged app is
a registered COM activator with its own CLSID, needed so the shell can *launch* a stopped app. The
tray is always running, so `ToastNotification.Activated` fires directly on the live object and the
COM server, the CLSID and the packaging are all unnecessary.

Consequences for the implementation:

- **TFM bump** to `net10.0-windows10.0.19041.0` for `src/ClaudeUsageTray` and
  `tests/ClaudeUsageTray.Tests`. The repo-wide default is `net10.0-windows` in
  `Directory.Build.props`, so these are two deliberate per-project overrides, not a change of the
  default. Overriding the default is what keeps `src/ClaudeUsageTraySetupStub` and its test project
  on `net10.0-windows`: the NativeAOT stub has no WinForms and no notifications, and pulling the
  Windows SDK projection into an ILC build buys nothing. Neither `build-release.ps1` nor either
  workflow hardcodes a TFM, so packaging is unaffected. The test project needs the bump only because
  it references the app; nothing in it touches WinRT.
- **The AUMID is `"velopack." + packId`.** `AppInfo` gains a `PackId` constant and derives the AUMID
  from it. Velopack builds the shortcut's ID that way, and **nothing today checks that `AppInfo` and
  `vpk --packId` agree** — a silent mismatch costs every notification with no other symptom. A test
  asserts that the pack id in `build-release.ps1` and `release.yml` matches `AppInfo.PackId`, so the
  two cannot drift apart unnoticed.
- **Activation is marshalled onto the UI thread.** The WinRT `Activated` callback does not arrive on
  the WinForms thread, and `ShowPopup()` touches WinForms state directly. `ToastPresenter` therefore
  hands activation back through the same `_sync.BeginInvoke` path `TrayApp` already uses for fetch
  completions, wrapped in the same `catch (InvalidOperationException)` for the shutting-down case.
- **Live toast objects are held.** The `ToastNotification` and its event subscriptions are kept
  referenced until a terminal event (`Activated`, `Dismissed`, `Failed`), so a click on a toast the
  GC has collected cannot silently do nothing.
- **Every WinRT call is wrapped**, and on any failure the presenter becomes a permanent no-op. The
  probe returned two different failure modes for an unregistered AUMID across two runs, so neither
  can be relied on and both must be survivable. This is the existing "nothing in the read paths
  throws" invariant applied to a write path: a dev run, a stripped shortcut, or a future Windows
  change must cost the notifications and nothing else.
- **`Group = "claudeusagetray"`, `Tag = "usage"` or `"status:{sourceId}"`**, so a newer toast of a
  kind replaces its predecessor in Action Center rather than stacking a history of superseded states.
- `Activated` calls `TrayApp.ShowPopup()`.

### Where it runs

One call site: `TrayApp.Render()`, immediately after it computes `choice` via
`SourceSelection.Choose` and reads `_statusMonitor.Sources()`. Both triggers' inputs are already
there, and the guards above make repeated evaluation on an unchanged reading harmless, so a single
site is safer than sprinkling calls through `Refresh`, `OnApiFetchCompleted` and
`OnStatusFetchCompleted` and hoping none is ever forgotten.

`Render()` is reached from startup, the 30 s cache tick, the `FileSystemWatcher` debounce, both
fetch completions, a settings save, the update-restart path and a manual refresh. Every one of those
is either a genuine data change or covered by a guard: the timestamp rule absorbs the repeated
reads, and the fingerprint rule absorbs the settings save. Notably, the 30 s tick means evaluation
happens on **time-only** changes too — which is intended, because pace severity genuinely moves with
the clock, and a value crossing into red purely because the window elapsed is a real crossing.

## Error handling

- A failed status fetch produces no transition; `StatusMonitor` keeps last-known-good.
- A stale snapshot produces no notification and no state change.
- A malformed settings file falls back per field; notifications default to on.
- Any WinRT failure disables toasts for the process lifetime and touches nothing else.
- The remembered state is in-memory only and does not survive a restart. This is correct rather than
  a limitation: after a restart the first reading is a baseline, so an auto-update restart in the
  middle of a red period cannot re-announce it.

## Testing

`Core` tests carry the whole of the behaviour:

- Baseline on first sight — at startup, and for a scoped limit that appears mid-session.
- **Arming:** a cache read showing green followed by a first live fetch showing red notifies
  nothing; a *second* live fetch that then crosses does notify. Also the no-credentials case, where
  arming happens immediately.
- A limit sitting red across many polls produces exactly one notification.
- Stale readings notify nothing, and a crossing that spans a stale gap still fires once afterwards.
- A snapshot with an older `FetchedAt` notifies nothing, and one with an **equal** `FetchedAt`
  notifies nothing (the 30 s re-read of an unchanged cache file).
- A Claude Code → Desktop-history switch re-baselines silently, in both directions.
- **Configuration changes re-baseline silently:** raising `StalenessMinutes` so an old snapshot
  becomes fresh; lowering `Thresholds.Red` under an existing value; toggling `PaceColors`; and
  changing `NotifyLevel` from `Red` to `Orange` while a value is already orange.
- Both notify levels, including green → red under level `Orange` firing once, not twice.
- Several simultaneous crossings coalesce into one notification.
- A scoped limit that vanishes and returns is re-baselined, not announced (key eviction).
- Status transitions in both directions, per source and independently.
- An OpenAI component outside the watch filter notifies nothing; one inside it notifies.
- Disabling and re-enabling a source notifies nothing; **widening the watch filter over an unchanged
  payload notifies nothing.**
- **Recovery text:** `IsRelevant` false while `Degraded` is still true does *not* say "All systems
  operational".
- `Settings` round-trips the new keys, and invalid values fall back per field.
- `UsageValues` agrees with the severities `UsagePopup` renders, **including the credit row's
  preference for `PayloadSeverity` over the configured thresholds.**
- `AppInfo.PackId` matches the pack id used by `build-release.ps1` and `release.yml`.

`ToastPresenter` is not unit-testable and therefore decides nothing. It is verified by hand against
an installed local build using the `Update.exe apply` flow in CLAUDE.md: both triggers, the click
opening the popup, Action Center persistence, and a `dotnet run` staying silent instead of throwing.

## Success criteria

- A limit crossing into red raises exactly one toast, whichever rule decided the colour.
- A platform going down and coming back raises one toast each way, naming the incident in the page's
  own words.
- Launching into an already-red or already-degraded state raises nothing — including the case where
  the stale cache reads green and the first live fetch reveals the red.
- A live-fetch failure that falls back to an older cache raises nothing.
- Editing settings — thresholds, pace, staleness, notify level, watch filter — raises nothing on its
  own, whatever it does to the colours on screen.
- A recovery toast never claims the platform is healthy while its page still reports a disruption.
- Every trigger is individually switchable, and the usage trigger's level is configurable.
- Clicking a toast opens the popup.
- A `dotnet run` from source raises no toast and no exception.

## Out of scope

- Notifications for leaving red. One-way by decision, recorded above.
- Actionable toast buttons beyond the click-to-open. `Argument` exists so adding one later is not a
  re-plumbing.
- In-app quiet hours or a minimum interval between toasts. Delegated to Focus Assist and the
  per-app Windows notification settings the AUMID provides.
- Notifying about the Claude Desktop history source's own values as a separate stream. It feeds the
  same snapshot and is covered by the source-switch re-baseline.
- Sound or custom toast imagery.
- Persisting the transition state across restarts.
