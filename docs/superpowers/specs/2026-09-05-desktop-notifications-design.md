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
| `Core/NotificationRules.cs` | stateful, clock-free | Remembers what it last saw; turns readings into transitions; owns arming |
| `Tray/ToastPresenter.cs` | WinForms/WinRT | Shows a toast; routes a click to the popup |

### `Core/UsageValues.cs`

`Enumerate(snapshot, settings, now)` returns one row per notifiable value:

```csharp
public sealed record UsageValue(string Key, string Label, int Percent, Severity Severity, DateTimeOffset? ResetsAt);
```

`ResetsAt` travels with the value so the toast expiry (*Usage toasts expire*) is a pure function of
the crossing rather than a second lookup in the presenter.

Keys are stable across polls and are what the transition state is filed under: `"5h"`, `"7d"`,
`"credits"`, and each scoped limit's `Label` — which `ScopedLimit` already documents as its dedup
key, so this introduces no new identity scheme. Keys compare `OrdinalIgnoreCase`, matching the dedup
in `UsageJson.ReadScopedLimits` (`UsageJson.cs:58`); otherwise a label whose case changed would read
as one key vanishing and another appearing. Absent values produce no row, upholding the "absent data
means no row" invariant: a limit the account does not have can never be notified about.

`Label` is the popup's caption text — "5-hour window", "7-day window", "{Label} weekly", "Credits" —
so that a toast and the row it refers to cannot be worded differently.

**`UsageValues` enumerates every scoped limit, including ones the popup withholds.** `UsagePopup`
draws only `PopupRows.ForScopedLimits(...).Visible`, capped at four. Notifying on all of them is
correct — CLAUDE.md forbids filtering on `is_active`, and a limit being pushed below a display cap
says nothing about whether it matters — but it means the earlier phrasing "a toast fires exactly
when a row turns red in the popup" is too strong. The guarantee is narrower and still worth having:
**where the popup does draw a value, the toast and the bar agree about its colour.**

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
notifier would make the toast's agreement with the bar beside it a coincidence that holds until
someone edits one of the three. `TrayApp` and `UsagePopup` are therefore rewired onto `UsageValues`
as part of this work — the one place where this feature changes existing code, and the reason it is
in scope. `UsagePopup.ParseSeverity` is private today and moves to `Core` with the rest.

### `Core/NotificationRules.cs`

Holds the remembered state, takes `now` from the caller, returns what to show:

```csharp
public sealed record Notification(string Title, string Body, string Argument, string Tag, DateTimeOffset? ExpiresAt);
```

`Argument` is the toast's activation payload, so a future second toast action does not need a new
plumbing path. Today there is exactly one value. `Tag` and `ExpiresAt` are decided here, not in
`ToastPresenter`, which must decide nothing. `OnUsage` returns them inside a
`NotificationOutcome(Notification?, Log, RemoveUsageToast)` so the log lines and the toast retraction
on leaving red are also decisions made in `Core`. `RemoveUsageToast` is true whenever a notified key
leaves the level **or stops being reported by the payload**; the single usage toast is retracted
whole, since its body may name several keys and any one of them leaving makes the sentence false.

**Arming is decided here, not in `TrayApp`.** `DisplayChoice` carries no cache-versus-live
provenance, so if arming were an `if` in the UI layer none of the arming tests would exercise the
code that ships — the failure mode CLAUDE.md names explicitly. `NotificationRules` therefore takes
`NoteLiveOutcome(LiveOutcome outcome)` with `Snapshot | Unauthorized | RateLimited | Failed |
NoToken`, and `TrayApp` only relays outcomes it already distinguishes at `TrayApp.cs:194-232` and
`:170`.

#### Usage transitions

`OnUsage(DisplayChoice choice, Settings settings, DateTimeOffset now)` returns zero or one
notification, applying these rules in order:

1. **Stale → nothing, and no state is recorded.** First, before anything else. Because nothing is
   recorded, a crossing that happened during the stale gap is still compared against the pre-stale
   state and fires once when fresh data returns; recording during staleness would swallow it
   silently. Running this check *first* also means the unarmed startup phase can never baseline
   itself against an hours-old cache.
2. **The notification fingerprint changed → clear all state, notify nothing.** Cleared rather than
   re-recorded, so the next admissible evaluation becomes an ordinary first-sight baseline through
   rule 4. See *Configuration changes*.
3. **A different `UsageSource` than last time → clear all state, notify nothing.** Claude Code and
   the Desktop history are different measurements of different things; switching between them moves
   a number without anything having happened.
4. **Not armed yet → record as baseline, notify nothing.** See *Arming*.
5. Per key, notify when severity crossed **from below the configured level to at or above it**, and
   the key is *re-armed* only after it drops back to `Green`. See *Hysteresis*.
6. Keys absent from this evaluation are **evicted**.
7. All crossings from one evaluation coalesce into **one** toast.

**There is deliberately no rule comparing `FetchedAt` against the last evaluated one.** An earlier
draft had one as the live → cache failover guard; it was wrong twice over. It was redundant — both
streams are already guarded by `SnapshotPrecedence.IsNewer` before they are stored (`_cliSnapshot`
in `TrayApp.Refresh`/`OnApiFetchCompleted`, `_desktopSnapshot` at `TrayApp.cs:139`), and the switch
*between* streams is rule 3 — so **notifications only ever see adopted snapshots**, which is the
invariant that actually does the work. And it was harmful: treating an unchanged timestamp as
"nothing to do" would have made a pace crossing driven by the clock alone unnotifiable, which is a
genuine crossing and the main reason evaluation belongs on the 30 s tick. Edge-triggering on the
remembered per-key predicate already makes re-evaluating unchanged data a no-op.

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

Arming happens on the first **terminal** live outcome, which is not the same as the first outcome:

- a snapshot (adopted or not), or `401`/`403` — the token's owner will not refresh it soon — or no
  usable token at the first `StartApiFetch` (no credentials file): **arms**;
- `429` or a network error: **does not arm.** A rate-limited first fetch is the documented common
  case (CLAUDE.md, *Working with the live usage endpoint*), and arming on it would restore the exact
  bug this rule exists to prevent: baseline from cache, then the first *successful* fetch minutes
  later reveals a limit that was already red and toasts about it;
- as a backstop, the **third concluded attempt** arms regardless, so a permanently offline machine
  still gets usage notifications from cache or Desktop history rather than being silent forever.
  With the 5/10/20-minute backoff this bounds the unarmed window at roughly 15 minutes.

The evaluation that arms is itself a baseline: arming records state and returns no notification, so
the first live reading can never be a transition. Note that a *rejected* token cannot arm at
startup — `_rejectedToken` is only set inside `OnApiFetchCompleted` (`TrayApp.cs:216`), so that path
has by definition already produced a terminal outcome.

With rule 1 running first, arming is not the only thing standing between a stale cache and a false
toast — a startup cache older than `StalenessMinutes` records nothing at all. Arming is what covers
the remaining case: a cache that is *fresh* by timestamp but behind the live figure.

#### Hysteresis

The pace ratio is `percent / (elapsed × 100)`, so it **falls as the window elapses**. A value sitting
near `RedRatio` (1.75) therefore crosses into red, drops back to orange minutes later on the clock
alone, and crosses again on the next usage — repeatedly, within one window. `TrayApp.cs:325` notes
the badge deliberately has no hysteresis because a flickering badge is cheap; a flickering toast is
not, and "a limit that sits red for four hours must produce exactly one toast" would be violated by
the clock rather than by the user.

**A key that has notified is re-armed only when it returns to `Green`** — not merely when it drops
below the configured level. Leaving is still silent. This is exit hysteresis rather than a cooldown
timer: it needs no duration constant, stays a pure function of the severities already computed, and
expresses the actual intent, which is that the situation genuinely recovered rather than that enough
minutes passed.

#### Configuration changes

Severity is recomputed from live settings on every evaluation, so a settings edit can move a value
across the line without the value having moved. Changing `StalenessMinutes` upward can un-stale an
old snapshot and make its old numbers notifiable; changing `Thresholds` or `PaceColors` can change
every severity at once; changing `NotifyLevel` from `Red` to `Orange` makes an already-orange value
cross a line that just moved under it.

All of these are the same failure, so they get one fix rather than three. `NotificationRules` holds
a **fingerprint** of the settings that can change a verdict — thresholds, pace mode, both staleness
allowances, and the notify level — and when it changes, all state is cleared and rebuilt from the
next admissible evaluation. The user's own edit is never news. A genuine crossing that coincides
exactly with a settings save is swallowed; that is one missed toast in exchange for never toasting
about an edit, and it is the same trade already accepted for a renamed scoped limit.

**The on/off switches are deliberately not in the fingerprint.** `usageNotifications.enabled` and a
source's `notify` do not change any verdict, so `OnUsage` and `OnStatus` always evaluate and always
record; the flag only suppresses the returned notification at the end. Turning notifications off and
on again therefore cannot fire a toast for a crossing that happened while they were off — which is
what would happen if evaluation stopped and the state went stale.

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

- **A null `Status` is not a reading.** `Render()` runs from the constructor before the first status
  fetch completes, and `StatusMonitor.Sources()` yields entries whose `Status` is null until then.
  Nothing is recorded and no transition is derived for such a source. Collapsing null into "not
  relevant" — the shape `UsagePopup.cs:116` uses for drawing — would make the first fetch of an
  already-degraded page a false → true transition and toast at startup.
- **First reading per source is baseline**, where "reading" means the first non-null
  `PlatformStatus`. Launching into a degraded platform is not a transition.
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
- `Degraded == true` → the watched disruption is over but the page is not clear. The body is exactly
  `StatusDetail.Header(source, status, relevant: false, stale: false)`, which already words this
  state ("OpenAI status: Partial outage · outside your watched components") — and is the same string
  the popup shows, so the two cannot disagree. `StatusDetail.Rows` is not usable here: it filters by
  the watch filter and so returns nothing at all for the unwatched remainder.

### Logging

"I never got a toast" is the report this feature will generate, and none of its decisions are
visible today. `fetch.log` therefore gains one line per emitted notification and per suppression,
naming the reason — unarmed, stale, fingerprint change, source switch, hysteresis, switched off, or
Windows-side disabled.

**The existing log rule is unchanged and constrains this.** The log may carry percentages, outcomes,
and the fixed keys `5h`, `7d` and `credits` — but **never scoped-limit labels**, which are exactly
the account-specific model names the rule forbids, nor money amounts or currency. A suppressed or
emitted notification about a scoped limit is logged by count, not by name. The toast itself may name
the limit: it is shown to the user who owns the account, on their own screen, and the popup already
shows the same label.

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
  workflow hardcodes a TFM, so the build scripts run unchanged. The test project needs the bump only
  because it references the app; nothing in it touches WinRT. The bump also sets the supported-OS
  floor at Windows 10 2004 (10.0.19041).
- **The bump costs 24.2 MB, measured, and that is accepted.** A self-contained `win-x64` publish goes
  from **118.6 MB to 142.8 MB (+20 %)**, entirely from `Microsoft.Windows.SDK.NET.dll` (23.7 MB) and
  `WinRT.Runtime.dll` (0.5 MB). Velopack deltas mean a user pays it once, on first install or when
  that assembly changes, not on every update. It buys Action Center persistence — the toast you
  missed while away is still there, which is much of the point of the feature — and a per-app entry
  in Windows notification settings. The alternatives were weighed and rejected: `ShowBalloonTip`
  costs 0 MB but loses both of those, and hand-written activation-factory interop costs 0 MB but
  puts ~200 lines of the least reviewable code in the repo into the layer that is supposed to decide
  nothing.
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
  GC has collected cannot silently do nothing. At most one live toast is held per tag, and replacing
  a tag drops the previous reference rather than waiting for a `Dismissed` that a replacement may
  never deliver. `Dismissed` and `Failed` arrive off-thread like `Activated`, so the bookkeeping is
  marshalled the same way.
- **Stale toasts are cleared at startup.** Toasts persist in Action Center across process exit, so
  after a Quit or the update restart (`TrayApp.cs:441`, `:510`) a click activates the shortcut,
  launches a second instance, and `SingleInstance.TryAcquire` exits it — the click appears to do
  nothing. Clearing this AUMID's history on startup means no toast in Action Center is ever older
  than the running process, and therefore none is ever un-actionable.
- **Usage toasts expire.** `ExpirationTime` is set to the window's `ResetsAt` where known, and a few
  hours otherwise, so Action Center stops asserting a red 5-hour window days after it reset. When a
  key leaves red under the *Hysteresis* rule, its toast is removed from history — the silent exit
  stays silent, but it stops leaving a false claim behind.
- **Failure is graduated, not absolute.** A failure to *create* the notifier is permanent (that is
  the unregistered-AUMID case the probe measured, and it does not heal). A failure of an individual
  `Show()` — the notification platform restarting, say — disables toasts only after three
  consecutive failures, each logged. Both still guarantee nothing throws.
- **Windows-side disablement is visible in the log.** `ToastNotifier.Setting` returning
  `DisabledForApplication`, `DisabledForUser` or `DisabledByGroupPolicy` is not an exception —
  `Show()` simply does nothing, leaving an in-app checkbox that lies. The value is logged once per
  session whenever it is not `Enabled`.
- **Every WinRT call is wrapped**, and on any failure the presenter becomes a permanent no-op. The
  probe returned two different failure modes for an unregistered AUMID across two runs, so neither
  can be relied on and both must be survivable. This is the existing "nothing in the read paths
  throws" invariant applied to a write path: a dev run, a stripped shortcut, or a future Windows
  change must cost the notifications and nothing else.
- **`Group = "claudeusagetray"`, `Tag = "usage"` or `"status:{sourceId}"`**, so a newer toast of a
  kind replaces its predecessor in Action Center rather than stacking a history of superseded states.
- `Activated` calls `TrayApp.ShowPopup()`.
- **`Windows.UI.Notifications` declares its own `Notification` type.** `ToastPresenter.cs` resolves
  the collision with a file-local alias to the Core record; the record keeps its name.

### Where it runs

One call site: `TrayApp.Render()`, immediately after it computes `choice` via
`SourceSelection.Choose` and reads `_statusMonitor.Sources()`. Both triggers' inputs are already
there, and the guards above make repeated evaluation on an unchanged reading harmless, so a single
site is safer than sprinkling calls through `Refresh`, `OnApiFetchCompleted` and
`OnStatusFetchCompleted` and hoping none is ever forgotten.

`Render()` is reached from startup, the 30 s cache tick, the `FileSystemWatcher` debounce, both
fetch completions, a settings save, the update-restart path and a manual refresh. Every one of those
is either a genuine data change or absorbed by a guard: edge-triggering makes a repeated read of
unchanged data a no-op, and the fingerprint rule absorbs the settings save. The 30 s tick means
evaluation happens on **time-only** changes too, which is required rather than merely tolerated:
pace severity moves with the clock, so a value crossing into red purely because the window elapsed
is a real crossing, and the *Hysteresis* rule is what stops the same clock from flapping it.

## Error handling

- A failed status fetch produces no transition; `StatusMonitor` keeps last-known-good.
- A stale snapshot produces no notification and no state change.
- A malformed settings file falls back per field; notifications default to on.
- A WinRT failure disables toasts — permanently if the notifier could not be created, after three
  consecutive failures if `Show()` is what failed — and touches nothing else.
- The remembered state is in-memory only and does not survive a restart. This is correct rather than
  a limitation: after a restart the first reading is a baseline, so an auto-update restart in the
  middle of a red period cannot re-announce it.

## Testing

`Core` tests carry the whole of the behaviour:

- Baseline on first sight — at startup, and for a scoped limit that appears mid-session.
- **Arming:** a cache read showing green followed by a first live fetch showing red notifies
  nothing; a *second* live fetch that then crosses does notify. A first fetch that returns `429` or
  a network error does **not** arm — a later successful fetch showing red still notifies nothing —
  while `401` and the no-credentials case arm immediately, and the third concluded attempt arms
  regardless.
- A limit sitting red across many polls produces exactly one notification.
- **Clock-only crossing:** the same snapshot evaluated at an advancing `now` until the pace ratio
  passes `RedRatio` produces exactly one notification. This is the case the deleted timestamp rule
  would have made impossible.
- **Hysteresis:** red → orange (by the clock) → red produces one notification, not two; red → green
  → red produces two.
- Stale readings notify nothing and record nothing, and a crossing that spans a stale gap still
  fires once afterwards.
- A Claude Code → Desktop-history switch re-baselines silently, in both directions.
- **Configuration changes re-baseline silently:** raising `StalenessMinutes` so an old snapshot
  becomes fresh; lowering `Thresholds.Red` under an existing value; toggling `PaceColors`; and
  changing `NotifyLevel` from `Red` to `Orange` while a value is already orange.
- Both notify levels, including green → red under level `Orange` firing once, not twice.
- Several simultaneous crossings coalesce into one notification.
- A scoped limit that vanishes and returns is re-baselined, not announced (key eviction).
- **Switching notifications off and on again** notifies nothing for a crossing that happened while
  they were off (evaluation continues; only the output is suppressed).
- A null status followed by a degraded status notifies nothing; a degraded status followed by a
  different degraded status notifies nothing.
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
an installed local build using the `Update.exe apply` flow in CLAUDE.md:

- both triggers fire, and the click opens the popup — **from the toast banner and from Action
  Center**, since the two differ in who holds the foreground;
- the popup takes focus and closes on click-away. `UsagePopup` positions at `Cursor.Position` and
  closes in `OnDeactivate`, so a popup shown without foreground rights would never receive the
  deactivate that closes it and would sit there `TopMost`. If Windows refuses foreground to a
  background process here, the behaviour is documented rather than fought;
- the popup appears on the monitor the toast was on, at the right DPI;
- toasts persist in Action Center, and stale ones from a previous process are gone after a restart;
- a `dotnet run` from source stays silent instead of throwing;
- with Windows notifications switched off for the app, nothing throws and `fetch.log` says why.

### Wiring the settings through

Three hand-written copy paths silently drop new settings fields and must be listed in the plan:
`SettingsDialog.Draft()` rebuilds the `openai` entry from scratch (`SettingsDialog.cs:427-431`),
`SettingsDialog`'s clone copies fields one by one (`:481-488`), and `TrayApp.ApplySettings`
(`TrayApp.cs:540-547`) copies the edited values across. All three need the new `notify` and
`usageNotifications` fields, and a round-trip test through the dialog catches it if one is missed.

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
- A limit hovering at the pace boundary produces one toast, not one every few minutes.
- Clicking a toast left over from a previous process never launches a second instance that exits.
- Every trigger is individually switchable, and the usage trigger's level is configurable.
- Clicking a toast opens the popup.
- A `dotnet run` from source raises no toast and no exception.

## Out of scope

- Notifications for leaving red. One-way by decision, recorded above.
- **Notifying about a change *within* an ongoing disruption.** A second incident opening, or an
  impact going minor → major, leaves `IsRelevant` true and is silent. The transition being watched is
  "is something wrong", not "what exactly is wrong".
- **Suppressing the re-baseline when the display source flaps.** A user with both Claude Code and
  the Desktop history will flip between them whenever Claude Code idles past `StalenessMinutes` while
  the desktop history is still inside its own allowance. Each flip clears state (rule 3) and swallows
  any crossing that coincides with it. Accepted: the alternative is comparing numbers that measure
  different things.
- Actionable toast buttons beyond the click-to-open. `Argument` exists so adding one later is not a
  re-plumbing.
- In-app quiet hours or a minimum interval between toasts. Delegated to Focus Assist and the
  per-app Windows notification settings the AUMID provides.
- Notifying about the Claude Desktop history source's own values as a separate stream. It feeds the
  same snapshot and is covered by the source-switch re-baseline.
- Sound or custom toast imagery.
- Persisting the transition state across restarts.
