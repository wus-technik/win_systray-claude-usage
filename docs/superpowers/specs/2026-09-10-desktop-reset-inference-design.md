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
   setting to think about. This is an invariant, not a default. It does **not** rest on the
   `ResetOrigin` default alone — that only guarantees the *data* is `Reported`. It rests on every
   new presentation branch testing `Source == DesktopHistory`, which is why Presentation below
   enumerates all of them rather than describing the effect.
4. **An inferred reset never influences a toast.** It colours the icon; the notifier evaluates the
   same value as if no reset time existed, which is what it does today. Absolute-threshold toasts
   therefore continue to fire on desktop data — the inference simply cannot be the cause of one.
   A wrong badge colour is a glance you re-check; a wrong toast is an interruption you cannot undo.
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
| `Core/WeeklyAnchor.cs` | Pure: `TryParse("Thu 03:00")`, `Format`, `NextReset(anchor, now, TimeZoneInfo zone)` |

### Changed types

```csharp
public enum ResetOrigin { Reported, Inferred, Stated }

public sealed record WindowUsage(int Percent, DateTimeOffset? ResetsAt)
{
    public ResetOrigin Origin { get; init; } = ResetOrigin.Reported;
}
```

An `init` property, **not** a third positional parameter: a positional member would change
`Deconstruct` and the generated `Equals`, and the point is that existing call sites are untouched,
not merely that they still compile. Every Claude Code path is `Reported` by construction rather than
by remembering to pass it.

- `UsageValue` gains a second severity (see Notifications). It does **not** carry `Origin`: the
  popup reads `WindowUsage` directly and the notifier reads the second severity, so a third copy of
  the origin would have no consumer.
- `Settings` gains `WeeklyResetAnchor` (`weeklyResetAnchor`, default null).
- `DesktopUsageReader.Read(path, now, WeeklyAnchor? anchor = null)` **and**
  `ReadFirst(byFreshness, now, WeeklyAnchor? anchor = null)` — `ReadFirst` is the production entry
  point (`TrayApp.cs:142`), so an anchor plumbed only into `Read` would pass its unit tests and be
  silently absent in the running tray. `TryRead` keeps its two-argument signature; it is a test and
  convenience shim and never sees an anchor.

`TimeMarker`, `SeverityRules`, `SnapshotPrecedence` and `SourceSelection` are **untouched**. They
already take a nullable `ResetsAt` and never ask where it came from, which is why the badge fix
costs almost nothing.

## The 5-hour inference

**Eligibility is shared with the percentages.** The inference sees exactly the samples the reader's
existing pass accepts — object-shaped, numeric `t` within the `DateTimeOffset` bounds, and **not**
beyond `now + SourceSelection.FutureTolerance`. That last filter is load-bearing and already
documented in the reader: a single corrupt far-future timestamp would otherwise win permanently.
Sharing it is what guarantees the bracket and the displayed percentages come from the same series,
and that "the newest sample" means the same object in both.

Eligible samples are sorted by `t` defensively — ascending order has held on every machine measured
but nothing guarantees it — and only the newest 20 000 by `t` (not by array position) participate,
so a pathological file cannot turn a 30 s tick into a long sort.

**Org handling: contiguous segments, not a filter.** Partition the ordered series into runs of
consecutive samples sharing one `org`, and consider only the run containing the newest sample. A
plain "keep samples matching the newest org" filter would pair two same-org samples *across* an
excluded other-org sample, so a drop the design claims to exclude would be read as a boundary with a
bracket narrow enough to pass gate 1. A missing `org` is its own value and breaks a run like any
other change.

Walk consecutive pairs within that run. A window started in the interval `(prev.At, cur.At]` whenever:

```
cur.fh > 0 && (prev.fh == 0 || cur.fh < prev.fh)
```

The second clause covers a user working straight through a boundary, where `fh` never touches 0.
Take the **last** such bracket, `start = midpoint(prev.At, cur.At)`, `resetsAt = start + 5h`.

The rule assumes every `fh` decrease is a boundary, which the file does not state. The evidence that
it holds: the 13 in-day windows reconstructed *from these very decreases* came out at 4.55–5.19 h.
A decrease that was not a boundary would have produced a wildly non-5 h length, and none did. The
residual risk is also self-limiting — a false start is one bad badge colour, re-evaluated on the
next tick, not a persistent state.

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

Setting `weeklyResetAnchor`, e.g. `"Thu 03:00"`: weekday plus `HH:mm`.

`NextReset(WeeklyAnchor anchor, DateTimeOffset now, TimeZoneInfo zone)` returns the next occurrence
**strictly after** `now`. The zone is a parameter, never `TimeZoneInfo.Local` read inside `Core/`:
the layer is deliberately ambient-free, and the DST cases below cannot be tested against a function
with no zone to vary. `TrayApp` supplies `TimeZoneInfo.Local`. Two DST rules, stated concretely
because "the next valid instant" is not an implementation:

- **Spring forward, wall time does not exist:** use the first instant after the gap — i.e. the gap's
  start plus its delta.
- **Fall back, wall time occurs twice:** use the earlier of the two, constructed with that day's
  pre-transition offset.

Applied to `SevenDay` only, only on a `DesktopHistory` snapshot, with `Origin = Stated`. Unset or
unparseable → `ResetsAt` stays null and the 7-day row carries the honest note. A fabricated weekly
reset is worse than none.

`Settings.NormalizeFields` is where an invalid value becomes null and a valid one becomes canonical
(`TryParse` then `Format`). That method already runs on both `Load` and `Save` and is the
established place for per-field fallbacks, so putting the rule anywhere else would let the dialog
and the loader disagree about what `"thu 3:00"` means.

No lattice fit, no outlier rejection, no phase-locking. The user can read their real weekly reset
off Claude's own UI, which makes the weekly window exact and needs no inference at all.

## Where origins are assigned

`DesktopUsageReader.Read` remains the only thing that produces a `DesktopHistory` snapshot, so it is
the only place that assigns an origin. It gains a second pass over `samples[]` to materialise
`DesktopSample`s, calls `DesktopResetInference` for the 5-hour window, and applies the caller-supplied
`WeeklyAnchor?` to the 7-day one. `ReadFirst` forwards the anchor unchanged; `TrayApp` passes
`WeeklyAnchor.TryParse(settings.WeeklyResetAnchor)` and the local time zone.

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

Two call sites change, both gated on `snapshot.Source == UsageSource.DesktopHistory`:

- **`UsagePopup.AddWindowRow`** (`UsagePopup.cs:97`) builds `resets` as `""` when `ResetsAt` is
  null — today a desktop row simply has no reset fragment. The "no reset time" text is therefore a
  **new branch**, not a changed string, and it is the one place that has to reconcile with the
  "absent data means no row" invariant: the row exists because the percentage exists, and the
  fragment explains why the rest of it is missing. The tilde is prepended inside the same fragment.
- **`TrayApp`'s tooltip** (`TrayApp.cs:378-401`) appends `estimated from Claude Desktop history`
  when any displayed window has `Origin == Inferred`.

Settings gains a **Claude Desktop** group holding the weekly-anchor field, plus a note on the
pace-colours checkbox that it needs a reset time. `SettingsDialog` currently receives no snapshot
(`SettingsDialog.cs:94`), so the gate is a new `bool desktopSource` constructor argument, **frozen
at open time**: `SourceSelection.Choose` can flip source on any 30 s tick, and a group that appears
or vanishes under an open dialog — possibly discarding a half-typed anchor — is worse than a group
that is briefly out of date. With no snapshot at all the group is hidden.

This costs something and the cost is accepted deliberately: a user who has Claude Code today and
will fall back to desktop data tomorrow cannot set the anchor in advance. The rule that a Claude
Code user is never shown anything about anchors or estimates wins over that case, because the far
more common outcome is a CLI user puzzling over a setting that does nothing for them. The field
reappears the next time the dialog is opened while desktop data is the active source.

## Notifications

`UsageValue` carries two severities, both computed in the same `UsageValues` call from the same
inputs — which is the anti-drift property that matters, not the field count:

- `Severity` — what the badge and the bar draw, paced as today.
- `NotifySeverity` — `ForSettings(…, elapsedFraction: Origin == Inferred ? null : fraction)`.

So an inferred reset cannot influence a toast; `Stated` and `Reported` both can; and on Claude Code
data the two values are equal by construction. A `null` fraction still falls back to the absolute
thresholds, so an inferred-reset desktop value at 90 % toasts exactly as it does today — the
inference is simply not what got it there.

**Every notification-state decision reads `NotifySeverity`; `Severity` is reserved for drawing.**
That is four sites in `NotificationRules`, not one, and naming them is the point of this paragraph:

| Line | Role | Why it must switch |
|---|---|---|
| `:141` | `AtOrAbove(value.Severity, level)` | the crossing test itself |
| `:156` | `if (value.Severity == Severity.Green) state.Notified = false` | the latch would release on a green the notifier never acted on |
| `:211` | `crossed.Where(v => v.Severity == severity)` | a value could qualify under one verdict and be grouped under the other |
| `:219` | `crossed.Any(v => v.Severity == Severity.Red) ? "red" : "orange"` | the toast would be worded from a verdict it was not raised on |

Implement as one accessor used at all four, so a fifth site cannot be added reading the wrong one.

`Fingerprint` (`NotificationRules.cs:55`) gains `WeeklyResetAnchor`. It is exactly the kind of
setting that record exists for — one that changes a verdict without the value moving. Editing the
anchor from `Thu 03:00` to `Wed 15:00` shifts the 7-day elapsed fraction and can flip Green to Red,
and without the fingerprint entry the user's own edit fires the toast.

Everything else in `2026-09-05-desktop-notifications-design.md` — arming, hysteresis, the stale
history clear — is unchanged.

## Testing

The rejection paths matter more than the happy path — every one of them is a case where the app
would otherwise show a pace number the data cannot support.

- `DesktopResetInferenceTests` — clean `0 → >0` start; work-through-boundary decrease; bracket wider
  than 20 min rejected; expired window rejected; newest `fh = 0`; unsorted input; empty and
  single-sample input; a sample beyond `FutureTolerance` excluded from the bracket as well as from
  the percentages; the 20 000 cap applied by `t`, not array position.
- Org cases specifically — a run break is not a boundary, **and** a brief excursion to another org
  between two same-org samples does not produce a bracket pairing across it (the segment rule);
  a missing `org` breaks a run.
- `WeeklyAnchorTests` — parse round-trip; junk, out-of-range and whitespace → null; canonical
  re-`Format` through `NormalizeFields` on both load and save; next occurrence when `now` sits
  exactly on the anchor; spring-forward gap; fall-back ambiguity. All with an explicit
  `TimeZoneInfo`, so they run identically on any machine and in CI.
- `UsageValuesTests` — `Inferred` yields a paced `Severity` and an absolute `NotifySeverity`;
  `Stated` and `Reported` yield the same value for both; an `Inferred` value above `redAbove` still
  produces a red `NotifySeverity`, since that is absolute.
- `NotificationRulesTests` — an `Inferred` value that is paced-Red but absolute-Orange does not
  raise a red toast and is not worded as red; the hysteresis latch keys off `NotifySeverity`;
  changing `weeklyResetAnchor` alone changes the fingerprint.
- `DesktopUsageReaderTests` — a fixture reproducing the measured case (06:52, `fh = 55`, start
  bracketed 05:52–06:07) asserting **Red** where the absolute rule gives Orange. This is a
  **synthetic arithmetic regression, not empirical validation**: it is built from the summary
  figures in the issue-#5 comment, since `wus-it-1337`'s file is not in this repo. It pins the
  formula, and would not catch the field semantics being wrong.
- A stale-but-eligible snapshot (`FetchedAt` up to `DesktopStalenessHours` old) paced against a live
  `now`, asserting the understating direction described below.
- Regression — a Claude Code snapshot renders with no tilde, no note, and an unchanged tooltip; and
  a `SettingsDialog` opened without a desktop source shows no Claude Desktop group.

## Considered and left alone

Three things were weighed and deliberately not addressed. Recorded so the next reader knows they
were not missed.

**Staleness understates rather than overstates.** `DesktopStalenessHours = 3` means a snapshot up to
three hours old can be paced against a `now`-based elapsed fraction. The direction is safe: a stale
percentage over a larger elapsed fraction yields a *smaller* ratio, and `ForPace` still returns Red
unconditionally above `redAbove`. Already true for Claude Code `resets_at` today.

**`start + 5h` is treated as exact, but measured windows span 4.55–5.19 h.** Combined with up to
10 min of midpoint error, an estimate can outlive the real reset by roughly half an hour at the tail
of a short window — showing a `~resets in 8m` that has already passed. The pace direction is again
the safe one (an over-long window understates the ratio), and gate 2 plus `TimeMarker`'s null-above-1
rule cancel the estimate shortly after. Not worth a further gate.

**No new logging.** `fetch.log` carries outcomes and percentages only, and `org` is a uuid that must
never reach it. Rather than invent a line, the inference logs nothing; if diagnosis later needs it,
the right shape is a transition-only fixed-token line such as
`desktop inference: 5h=available|no-bracket|wide-bracket|expired`, with no timestamps and no org.

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
