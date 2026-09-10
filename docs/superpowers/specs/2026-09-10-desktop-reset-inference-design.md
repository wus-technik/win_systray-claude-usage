# Desktop reset inference and the weekly anchor

Date: 2026-09-10 · Issue: [#5](https://github.com/wus-technik/win_systray-claude-usage/issues/5) ·
Builds on: `2026-09-05-desktop-usage-history-design.md`, `2026-09-05-desktop-notifications-design.md`

## Problem

On a machine that runs only the Claude Desktop app, the tray now shows usage percentages (issue #5,
first pass) but **no pacing at all**: no `1.4× pace` text, no elapsed marker in the bars, no
`resets in …`, and `PaceColors = true` silently does nothing. Everything pace-shaped hangs off one
field — `WindowUsage.ResetsAt` — and `plan-usage-history.json` carries no reset timestamp of any
kind, so `DesktopUsageReader` emits null and all of it switches off together. Nothing in the UI says
why, which is the defect as reported.

This is not cosmetic. Measured on `wus-it-1337`'s own history (420 samples, 2026-08-11 to
2026-09-10): at the newest sample (06:52, `fh = 55`) the 5-hour window had started ~05:59, so 17.7 %
of it had elapsed — a pace ratio of **3.10× → Red**, where the absolute rule the tray actually
applied said **Orange**.

### What the file does and does not contain

Measured, not assumed. Every sample has exactly `{org, t, u}`; `u` has exactly `{fh, sd}` (375
samples) or `{fh, sd, xu}` (45). `version` is 2. **There is no reset field.** `DesktopUsageReader`
is right to emit `ResetsAt = null`; there is no quick reader fix.

The boundaries are nevertheless *observable* in the series:

- **5-hour.** `fh` drops 31 times in 30 days. Reading "start = the `0 → >0` transition, boundary =
  the following decrease" reproduces the window at median **4.84 h** over the 13 windows whose
  boundary falls inside the working day, with the start bracketed to a median of 15 min — i.e.
  ±8 min, 2.7 % of the period.
- **7-day.** Four `sd` zeroings sit exactly on a 7-day lattice and place the weekly reset in
  Wed ~15:05 – Thu ~05:52 local (±7.4 h). But two further zeroings, on 2026-09-02 and 2026-09-07,
  are off-lattice and are *genuine* counter restarts — the following samples continue from ~0
  rather than snapping back. A "last drop wins" rule would have been wrong on 2 of 6 detections.
  The leading hypothesis is that `oauth/usage` carries several weekly limits (what `ScopedLimit`
  exists for) while the desktop file has room for one number, so a change of model family looks
  exactly like a reset. **This is unproven** — see Open questions.

## Decisions

1. **Infer the 5-hour reset from the series**, behind confidence gates.
2. **Do not infer the weekly reset.** Let the user state it, since it is visible in Claude's own UI.
   This sidesteps the unresolved `sd` question rather than betting on it.
3. **All of this exists only on a `DesktopHistory` snapshot.** A Claude Code user — cache or live —
   sees today's behaviour byte for byte: no estimate marks, no "no reset time" note, and no anchor
   setting to think about. This is an invariant, not a default.
4. **An inferred reset colours the icon but never raises a toast.** A wrong badge colour is a glance
   you re-check; a wrong toast is an interruption you cannot undo.
5. **A user-stated anchor is not an estimate.** It renders unmarked and may raise toasts. Hence a
   three-valued origin rather than a bool.

## Architecture

The design question is how "this reset time is inferred" travels from the reader to the badge, the
popup and the notifier. The answer: **as a field on `WindowUsage`**, so the snapshot keeps
describing itself completely.

Two alternatives were rejected. Computing the inference at display time in `TrayApp` puts a decision
back into the UI layer and gives the popup and the notifier two different views of the same window.
A side-table of estimates hung off the snapshot leaves `WindowUsage` alone but forces every consumer
to look in a second place, and forgetting is silent — exactly the drift `UsageValues` exists to
prevent.

### New files

| File | Purpose |
|---|---|
| `Core/DesktopSample.cs` | `record DesktopSample(DateTimeOffset At, string? Org, int? FiveHour, int? SevenDay)` |
| `Core/DesktopResetInference.cs` | Pure: `DateTimeOffset? FiveHourReset(IReadOnlyList<DesktopSample>, string? org, DateTimeOffset now)` |
| `Core/WeeklyAnchor.cs` | Pure: `TryParse("Thu 03:00")`, `Format`, `NextReset(anchor, now)` |

### Changed types

```csharp
public enum ResetOrigin { Reported, Inferred, Stated }

public sealed record WindowUsage(
    int Percent, DateTimeOffset? ResetsAt, ResetOrigin Origin = ResetOrigin.Reported);
```

The default keeps every existing call site compiling and makes every Claude Code path `Reported` by
construction rather than by remembering to pass it.

- `UsageValue` gains `Origin` and a second severity (see Notifications).
- `Settings` gains `WeeklyResetAnchor` (`weeklyResetAnchor`, default null).
- `DesktopUsageReader.Read(path, now, WeeklyAnchor? anchor = null)`.

`TimeMarker`, `SeverityRules`, `SnapshotPrecedence` and `SourceSelection` are **untouched**. They
already take a nullable `ResetsAt` and never ask where it came from, which is why the badge fix
costs almost nothing.

## The 5-hour inference

Input is the full sample array the reader already parses. Samples are sorted by `t` defensively —
ascending order has held on every machine measured but nothing guarantees it — and only the newest
20 000 samples participate, so a pathological file cannot turn a 30 s tick into a long sort.

Only samples whose `org` equals the newest sample's `org` participate. An org switch interleaves two
series; a drop across that boundary is not a reset.

Walk consecutive participating pairs. A window started in the interval `(prev.At, cur.At]` whenever:

```
cur.fh > 0 && (prev.fh == 0 || cur.fh < prev.fh)
```

The second clause covers a user working straight through a boundary, where `fh` never touches 0.
Take the **last** such bracket, `start = midpoint(prev.At, cur.At)`, `resetsAt = start + 5h`.

### Gates

Each gate means *emit nothing*, never *emit something weaker*:

1. **Bracket width ≤ 20 min.** The median sample gap is 15 min, so this is the measured ±8 min
   accuracy. A wider bracket is not a boundary observation, it is a guess about a gap.
2. **`now < resetsAt`.** An expired window says nothing about whatever may have started since.
3. **Newest `fh > 0`.** No window is running, and pace at 0 % is meaningless anyway.
4. **No bracket found.** History opens mid-window, or the only start sits behind a long gap.

Gate 4 is the common case on a fresh install and must be quiet, not an error.

`Origin = Inferred` when a value survives all four.

## The weekly anchor

Setting `weeklyResetAnchor`, e.g. `"Thu 03:00"`: weekday plus `HH:mm`, interpreted in local time.
`WeeklyAnchor.NextReset` returns the next occurrence **strictly after** `now`. A wall time that does
not exist on a spring-forward day rolls to the next valid instant; an ambiguous fall-back time takes
the first occurrence.

Applied to `SevenDay` only, only on a `DesktopHistory` snapshot, with `Origin = Stated`. Unset or
unparseable → `ResetsAt` stays null and the 7-day row carries the honest note. An unparseable value
normalises to null, never to a default guess: a fabricated weekly reset is worse than none.

No lattice fit, no outlier rejection, no phase-locking. The user can read their real weekly reset
off Claude's own UI, which makes the weekly window exact and needs no inference at all.

## Where origins are assigned

`DesktopUsageReader.Read` remains the only thing that produces a `DesktopHistory` snapshot, so it is
the only place that assigns an origin. It gains a second pass over `samples[]` to materialise
`DesktopSample`s, calls `DesktopResetInference` for the 5-hour window, and applies the caller-supplied
`WeeklyAnchor?` to the 7-day one. `TrayApp` passes `WeeklyAnchor.TryParse(settings.WeeklyResetAnchor)`.

The reader keeps its existing contract: never throws, degrades to null.

## Presentation

On a `DesktopHistory` snapshot only:

| State | Popup row | Tooltip |
|---|---|---|
| `Inferred` | `~resets in 3h 05m` | adds `estimated from Claude Desktop history` |
| `Stated` | `resets in 2d 4h` — unmarked | unchanged |
| `ResetsAt` null | `no reset time` | explains that the desktop history carries none |

The tilde carries the hedge in the 240 px row; the sentence lives in the tooltip, where there is
room for it. `Stated` is deliberately indistinguishable from `Reported`: the user asserted it, and
marking their own answer as doubtful is noise.

Settings gains a **Claude Desktop** group holding the weekly-anchor field, plus a note on the
pace-colours checkbox that it needs a reset time. The group is shown only while the active snapshot
is `DesktopHistory`.

## Notifications

`UsageValue` carries two severities, both computed in the same `UsageValues` call:

- `Severity` — what the badge and the bar draw, paced as today.
- `NotifySeverity` — `ForSettings(…, elapsedFraction: Origin == Inferred ? null : fraction)`.

So an inferred reset colours the icon but can never raise a toast; `Stated` and `Reported` both can;
and on Claude Code data the two values are equal by construction. `NotificationRules` switches to
reading `NotifySeverity` and is otherwise unchanged — the arming, hysteresis and settings-fingerprint
logic in `2026-09-05-desktop-notifications-design.md` all still apply.

## Testing

- `DesktopResetInferenceTests` — synthetic series: clean `0 → >0` start; work-through-boundary
  decrease; bracket wider than 20 min rejected; expired window rejected; newest `fh = 0`; org switch
  is not a reset; unsorted input; empty and single-sample input.
- `WeeklyAnchorTests` — parse round-trip; junk and out-of-range → null; next occurrence when `now`
  sits exactly on the anchor; DST spring-forward gap; fall-back ambiguity.
- `UsageValuesTests` — `Inferred` yields a paced `Severity` and an absolute `NotifySeverity`;
  `Stated` and `Reported` yield the same value for both.
- `DesktopUsageReaderTests` — a fixture reproducing the measured case (06:52, `fh = 55`, start
  bracketed 05:52–06:07) asserting **Red** where the absolute rule gives Orange. Built from the
  figures in the findings; `wus-it-1337`'s file is not available in this repo.
- Regression — a Claude Code snapshot renders with no tilde, no note, and an unchanged tooltip.

## Considered and left alone

`DesktopStalenessHours = 3` means a snapshot up to three hours old can be paced against a `now`-based
elapsed fraction, which *understates* pace. This is already true for Claude Code `resets_at` today,
so it is not changed here — recorded so the next reader knows it was weighed, not missed.

## Not doing

- The 7-day lattice fit with outlier rejection. It needs 2–3 weeks of history and rests on the
  unproven `sd`-continuity question.
- Decrypting the desktop app's DPAPI-protected token. Rejected in the original design record.
- Treating the newest sample's `t` as a window start. It is a sample time, not a boundary, and would
  produce confidently wrong pacing.

## Open questions

1. **Is `sd` one continuous series?** The two off-lattice zeroings (2026-09-02, 2026-09-07) are real
   counter restarts. The multiple-weekly-limits hypothesis fits but is unproven. The one experiment
   that settles it is a CLI snapshot (`limits[]` with real `resets_at`) and a desktop history
   captured on the same machine within the same hour. Not possible on the author's machine: the
   desktop history there was last written 2026-07-31. This design does not depend on the answer, but
   a future weekly inference would.
2. **Whether `xu`-carrying samples are structurally different** (full write vs. partial). 45 of 420
   carry it, both anomalous zeroings do, and `xu` takes only two values across the 30 days. Worth a
   look only if question 1 is pursued.
