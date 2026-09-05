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
scoped weekly limits, and null for credits, which carry no reset time and therefore always fall back
to the absolute thresholds.

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

1. **Stale → nothing, and the remembered state is left untouched.** Not merely a skip: because
   nothing is recorded, a crossing that happened during the stale gap is still compared against the
   pre-stale state and fires once when fresh data returns. Recording during staleness would swallow
   it silently.
2. **A different `UsageSource` than last time → re-baseline, notify nothing.** Claude Code and the
   Desktop history are different measurements of different things; switching between them moves a
   number without anything having happened.
3. **`snapshot.FetchedAt` older than the last evaluated one → nothing.** This is the cache-failover
   guard the issue asks for, and it needs no special case: a live fetch failing over to an older
   `.claude.json` can only move the timestamp backwards, so a timestamp comparison catches it along
   with every other form of going backwards in time.
4. Per key, notify when severity crossed **from below the configured level to at or above it**.
5. All crossings from one evaluation coalesce into **one** toast.

**First sight is always baseline.** A key not seen before is recorded without notifying. This covers
startup into an already-red state, and also a scoped limit the payload only just began reporting —
an ambiguous case decided deliberately: the payload beginning to mention a limit is not the limit
having moved, and the alternative fires a toast for something that may have been red for days.

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
- **A failed fetch needs no rule.** `StatusMonitor` already keeps last-known-good on failure, so the
  value does not change and no transition exists. A dead endpoint degrades to stale, never to a
  false recovery.

#### Text

Titles and bodies are the page's own words, never a hardcoded model or component list.

- Degraded: the incident names where the page sends them; component names with their statuses where
  it does not, which is the OpenAI case. Composed through `StatusDetail`, so the toast and the popup
  cannot describe the same outage differently.
- Recovered: "All systems operational".
- Usage: the labels from `UsageValues`, joined — "5-hour limit and Fable weekly are now red".

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
  `tests/ClaudeUsageTray.Tests`. Neither `build-release.ps1` nor either workflow hardcodes a TFM, so
  packaging is unaffected. The NativeAOT setup stub keeps `net10.0-windows` and is untouched — it
  has no WinForms and no notifications.
- **The AUMID is `"velopack." + packId`.** A constant in `AppInfo`, documented as having to match
  `vpk --packId`, since Velopack derives the shortcut's ID that way and nothing at build time checks
  that the two agree.
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
- A limit sitting red across many polls produces exactly one notification.
- Stale readings notify nothing, and a crossing that spans a stale gap still fires once afterwards.
- A snapshot with an older `FetchedAt` notifies nothing (the failover guard).
- A Claude Code → Desktop-history switch re-baselines silently.
- Both notify levels, including green → red under level `Orange` firing once, not twice.
- Several simultaneous crossings coalesce into one notification.
- Status transitions in both directions, per source and independently.
- An OpenAI component outside the watch filter notifies nothing; one inside it notifies.
- Disabling and re-enabling a source notifies nothing.
- `Settings` round-trips the new keys, and invalid values fall back per field.
- `UsageValues` agrees with the severities `UsagePopup` renders.

`ToastPresenter` is not unit-testable and therefore decides nothing. It is verified by hand against
an installed local build using the `Update.exe apply` flow in CLAUDE.md: both triggers, the click
opening the popup, Action Center persistence, and a `dotnet run` staying silent instead of throwing.

## Success criteria

- A limit crossing into red raises exactly one toast, whichever rule decided the colour.
- A platform going down and coming back raises one toast each way, naming the incident in the page's
  own words.
- Launching into an already-red or already-degraded state raises nothing.
- A live-fetch failure that falls back to an older cache raises nothing.
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
