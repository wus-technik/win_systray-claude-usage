# Desktop Reset Inference and the Weekly Anchor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give a Claude-Desktop-only machine real pacing — the elapsed marker, the `1.4× pace` gloss, `resets in …` and `PaceColors` — by inferring the 5-hour window boundary from the history series and letting the user state the weekly one, without an inferred value ever raising a toast and without a Claude Code user seeing any of it.

**Architecture:** Two new pure types in `Core/` decide everything. `DesktopResetInference` walks the reader's own eligible samples, brackets the last 5-hour window start between two consecutive samples of the newest org *run*, and emits `start + 5h` only behind four gates. `WeeklyAnchor` parses a user-stated `"Thu 03:00"` and projects the next occurrence in an explicitly supplied `TimeZoneInfo`. Both results travel as a `ResetOrigin` field on `WindowUsage`, so the snapshot keeps describing itself completely; `UsageValue` gains a second severity so the notifier can ignore an inferred reset while the badge uses it.

**Tech Stack:** .NET 10 on TFM `net10.0-windows10.0.19041.0` (app + `tests/ClaudeUsageTray.Tests` only), C# 13, WinForms, `System.Text.Json`, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-10-desktop-reset-inference-design.md` — read it first. Every rule below is argued there; this plan only sequences it.

**Issue:** [#5](https://github.com/wus-technik/win_systray-claude-usage/issues/5). Every commit body ends with `Refs #5`; the last task's commit uses `Closes #5`.

## Global Constraints

- **Pure logic goes in `src/ClaudeUsageTray/Core/`**, WinForms only in `src/ClaudeUsageTray/Tray/`. No clocks, no threads and no ambient time zone in `Core/`: every time-dependent function takes a caller-supplied `DateTimeOffset now`, and `WeeklyAnchor.NextReset` takes a caller-supplied `TimeZoneInfo`. `TimeZoneInfo.Local` appears only in `Tray/`.
- **Nothing in the read paths throws.** `DesktopUsageReader` keeps its `try`/`catch` contract and degrades to null. `Settings.Load` keeps its per-field fallback: an unparseable `weeklyResetAnchor` resets that field alone.
- **Everything new is gated on `snapshot.Source == UsageSource.DesktopHistory`.** A Claude Code user — cache or live — must see today's behaviour byte for byte: no tilde, no "no reset time" note, no tooltip suffix, no Claude Desktop settings group. This is an invariant, not a default, and every new presentation branch tests the source explicitly. `ResetOrigin.Reported` being the default is *not* sufficient on its own.
- **An inferred reset never decides whether a toast fires, or at what level.** `NotificationRules` reads `UsageValue.NotifySeverity` at *every* verdict site; `UsageValue.Severity` is reserved for drawing. The one place an inferred reset still reaches the notifier is `Compose`'s `ExpiresAt` (`NotificationRules.cs:224`), which uses `UsageValue.ResetsAt` to set the Action Center slot's lifetime. That is deliberate and recorded in Task 11: a slightly wrong expiry shortens or lengthens a notification that was already correctly raised, which is not an interruption the user cannot undo.
- **A stated anchor is not an estimate.** It renders unmarked (indistinguishable from `Reported`) and may raise toasts.
- **The 5-hour period is `UsageValues.FiveHourPeriod`** (`TimeSpan.FromHours(5)`); never a fresh literal.
- **Sample eligibility is shared with the percentages**: object-shaped, numeric `t` inside the `DateTimeOffset` bounds, `t <= now + SourceSelection.FutureTolerance`, and an object-shaped `u`. The inference must see exactly this set, so "the newest sample" means the same object in both passes.
- **Gate values, verbatim:** max bracket width `20 min`; sample cap `20 000`, applied by `t` after sorting, not by array position; emit only when `now < resetsAt` and the newest `fh > 0`.
- **No new logging.** `org` is a uuid and must never reach `fetch.log`. The inference logs nothing.
- **Absent data means no row.** The "no reset time" text is a *fragment inside an existing row*, never a synthesised row.
- **Test commands:** `dotnet test` (all), `dotnet test --filter FullyQualifiedName~ClassName` (one class), `dotnet test --filter "FullyQualifiedName~ClassName.Method"` (one test). CI runs `dotnet test -c Release`. **`dotnet test` output on this machine is German — grep for `Bestanden!` / `Fehler:`, not `Passed!`.**
- **Style:** match surrounding code; XML doc comments explain *why*, not *what*. No linter, no formatter. Commits: conventional prefix, `Refs #5` in the body, no AI co-author trailer.

---

### Task 1: `ResetOrigin` on `WindowUsage`

**Files:**
- Modify: `src/ClaudeUsageTray/Core/UsageSnapshot.cs:6-7`
- Test: `tests/ClaudeUsageTray.Tests/UsageSnapshotTests.cs`

**Interfaces:**
- Produces: `enum ClaudeUsageTray.Core.ResetOrigin { Reported, Inferred, Stated }`; `WindowUsage.Origin` (`ResetOrigin`, `init`, default `ResetOrigin.Reported`).

- [ ] **Step 1: Write the failing test**

Append to `tests/ClaudeUsageTray.Tests/UsageSnapshotTests.cs`:

```csharp
    [Fact]
    public void WindowUsage_Origin_DefaultsToReported()
    {
        Assert.Equal(ResetOrigin.Reported, new WindowUsage(50, null).Origin);
    }

    [Fact]
    public void WindowUsage_Origin_IsInitOnlyAndNotPositional()
    {
        // Positional shape is unchanged: two-argument construction and two-part deconstruction
        // still work, so no existing call site had to be touched.
        var (percent, resetsAt) = new WindowUsage(50, null) { Origin = ResetOrigin.Inferred };
        Assert.Equal(50, percent);
        Assert.Null(resetsAt);
    }

    [Fact]
    public void WindowUsage_Origin_ParticipatesInEquality()
    {
        var reported = new WindowUsage(50, null);
        var inferred = reported with { Origin = ResetOrigin.Inferred };
        Assert.NotEqual(reported, inferred);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~UsageSnapshotTests`
Expected: FAIL — compile error, `ResetOrigin` does not exist.

- [ ] **Step 3: Write minimal implementation**

In `src/ClaudeUsageTray/Core/UsageSnapshot.cs`, replace the `WindowUsage` declaration:

```csharp
/// <summary>Where a window's ResetsAt came from. Reported is the payload's own value (Claude Code,
/// cache or live). Inferred is reconstructed from the Claude Desktop history and is marked in the UI
/// and withheld from the notifier. Stated is the user's own weekly anchor — an assertion, not an
/// estimate, so it renders unmarked and may notify.</summary>
public enum ResetOrigin { Reported, Inferred, Stated }

/// <summary>Usage for one rolling window. Percent is the raw integer from the cache (may exceed 100).</summary>
public sealed record WindowUsage(int Percent, DateTimeOffset? ResetsAt)
{
    /// <summary>An init property rather than a third positional member on purpose: a positional one
    /// would change Deconstruct and force every construction site to pass a value, and the point is
    /// that the existing sites are untouched, not merely that they still compile. Every Claude Code
    /// path is Reported by construction rather than by remembering to pass it. (It does participate
    /// in the generated Equals either way — that is what the equality test below pins.)</summary>
    public ResetOrigin Origin { get; init; } = ResetOrigin.Reported;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: all pass (`Bestanden!`). Nothing else in the tree changes — that is the point of the init property.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/UsageSnapshot.cs tests/ClaudeUsageTray.Tests/UsageSnapshotTests.cs
git commit -m "feat: add ResetOrigin to WindowUsage

Refs #5"
```

---

### Task 2: `DesktopSample` and the 5-hour inference

**Files:**
- Create: `src/ClaudeUsageTray/Core/DesktopSample.cs`
- Create: `src/ClaudeUsageTray/Core/DesktopResetInference.cs`
- Test: `tests/ClaudeUsageTray.Tests/DesktopResetInferenceTests.cs`

**Interfaces:**
- Consumes: `SourceSelection.FutureTolerance`, `UsageValues.FiveHourPeriod` (both existing).
- Produces:
  - `sealed record DesktopSample(DateTimeOffset At, string? Org, int? FiveHour, int? SevenDay)`
  - `static class DesktopResetInference` with
    `public static DateTimeOffset? FiveHourReset(IReadOnlyList<DesktopSample> samples, string? org, DateTimeOffset now)`,
    `public const int MaxSamples = 20_000;` and `public static readonly TimeSpan MaxBracket = TimeSpan.FromMinutes(20);`

The `org` parameter is the newest eligible sample's org, passed in by the reader; the function does
not re-derive it, so the reader and the inference cannot disagree about which sample is newest.

- [ ] **Step 1: Write the failing test**

Create `tests/ClaudeUsageTray.Tests/DesktopResetInferenceTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The rejection paths matter more than the happy path: each is a case where the tray would
/// otherwise show a pace number the data cannot support.</summary>
public class DesktopResetInferenceTests
{
    private static readonly DateTimeOffset Base = new(2026, 9, 10, 6, 0, 0, TimeSpan.Zero);
    private const string Org = "11111111-1111-1111-1111-111111111111";

    private static DesktopSample S(double minutes, int? fh, string? org = Org)
        => new(Base.AddMinutes(minutes), org, fh, 20);

    [Fact]
    public void CleanStart_BracketsTheZeroToPositiveTransition()
    {
        // 0 at +0, 40 at +10 → start midway at +5, reset at +5 min + 5 h.
        var samples = new[] { S(0, 0), S(10, 40), S(20, 45) };
        var reset = DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(25));
        Assert.Equal(Base.AddMinutes(5) + UsageValues.FiveHourPeriod, reset);
    }

    [Fact]
    public void WorkThroughBoundary_DecreaseIsAStart()
    {
        // fh never touches 0: 88 → 12 is the boundary. Midpoint of +10 and +20 is +15.
        var samples = new[] { S(0, 80), S(10, 88), S(20, 12) };
        var reset = DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(30));
        Assert.Equal(Base.AddMinutes(15) + UsageValues.FiveHourPeriod, reset);
    }

    [Fact]
    public void LastBracketWins_NotTheFirst()
    {
        // Two clean boundaries; the newer one at +290/+300 decides.
        var samples = new[] { S(0, 0), S(10, 40), S(290, 40), S(300, 5) };
        var reset = DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(320));
        Assert.Equal(Base.AddMinutes(295) + UsageValues.FiveHourPeriod, reset);
    }

    [Fact]
    public void Gate1_BracketWiderThanTwentyMinutes_IsRejected()
    {
        // 21 min apart: not a boundary observation, a guess about a gap.
        var samples = new[] { S(0, 0), S(21, 40) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(30)));
    }

    [Fact]
    public void Gate1_AWideLaterBracket_SuppressesAnEarlierNarrowOne()
    {
        // The last boundary is the one that matters, and it is only poorly observed. Falling back to
        // the earlier narrow bracket would emit a reset for a window that has already turned over —
        // so the gate applies to the last candidate, not to each in turn.
        var samples = new[] { S(0, 0), S(10, 40), S(200, 40), S(290, 5) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(300)));
    }

    [Fact]
    public void Gate1_ExactlyTwentyMinutes_IsAccepted()
    {
        var samples = new[] { S(0, 0), S(20, 40) };
        Assert.Equal(Base.AddMinutes(10) + UsageValues.FiveHourPeriod,
            DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(30)));
    }

    [Fact]
    public void Gate2_ExpiredWindow_IsRejected()
    {
        var samples = new[] { S(0, 0), S(10, 40) };
        // now is past start + 5 h: the window said nothing about whatever started since.
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddHours(6)));
    }

    [Fact]
    public void Gate2_NowExactlyAtTheReset_IsRejected()
    {
        var samples = new[] { S(0, 0), S(10, 40) };
        Assert.Null(DesktopResetInference.FiveHourReset(
            samples, Org, Base.AddMinutes(5) + UsageValues.FiveHourPeriod));
    }

    [Fact]
    public void Gate3_NewestFhIsZero_IsRejected()
    {
        var samples = new[] { S(0, 0), S(10, 40), S(20, 0) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(25)));
    }

    [Fact]
    public void Gate3_NewestFhIsNull_IsRejected()
    {
        var samples = new[] { S(0, 0), S(10, 40), S(20, null) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(25)));
    }

    [Fact]
    public void Gate4_NoBracket_IsQuiet()
    {
        // History opens mid-window and only rises: the common fresh-install case, not an error.
        var samples = new[] { S(0, 30), S(10, 40), S(20, 55) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(25)));
    }

    [Fact]
    public void UnsortedInput_IsSortedByT()
    {
        var samples = new[] { S(20, 45), S(0, 0), S(10, 40) };
        Assert.Equal(Base.AddMinutes(5) + UsageValues.FiveHourPeriod,
            DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(25)));
    }

    [Fact]
    public void EmptyAndSingleSample_ProduceNothing()
    {
        Assert.Null(DesktopResetInference.FiveHourReset([], Org, Base));
        Assert.Null(DesktopResetInference.FiveHourReset([S(0, 40)], Org, Base.AddMinutes(5)));
    }

    [Fact]
    public void OrgRunBreak_IsNotABoundary()
    {
        // The drop from 88 to 12 coincides with the org change; a different account's counter is not
        // this account's reset. The newest run is the b-run alone, which has no bracket of its own.
        var samples = new[] { S(0, 80), S(10, 88), S(20, 12, "b"), S(30, 20, "b") };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, "b", Base.AddMinutes(35)));
    }

    [Fact]
    public void ExcursionToAnotherOrg_DoesNotPairAcrossIt()
    {
        // A plain "keep samples whose org matches the newest" filter would pair +10 (88) with +30
        // (12) and read a 20-minute bracket. The segment rule sees the newest run as +30..+40 only.
        var samples = new[] { S(0, 80), S(10, 88), S(20, 5, "b"), S(30, 12), S(40, 18) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(45)));
    }

    [Fact]
    public void MissingOrg_BreaksARunLikeAnyOtherChange()
    {
        var samples = new[] { S(0, 80), S(10, 88, null), S(20, 12), S(30, 18) };
        Assert.Null(DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(35)));
    }

    [Fact]
    public void SampleCap_AppliesByTimestampNotArrayPosition()
    {
        // 20 000 old samples in ascending array order, then the useful bracket. Taking the first
        // MaxSamples off the array keeps only the old ones and finds nothing; taking the newest
        // MaxSamples by timestamp keeps the bracket. The array is already sorted, so the only thing
        // this can distinguish is which end the cap is applied to.
        var samples = new List<DesktopSample>();
        for (int i = 20_000; i >= 1; i--) samples.Add(S(-i * 60, 50));
        samples.Add(S(0, 0));
        samples.Add(S(10, 40));
        Assert.Equal(Base.AddMinutes(5) + UsageValues.FiveHourPeriod,
            DesktopResetInference.FiveHourReset(samples, Org, Base.AddMinutes(25)));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~DesktopResetInferenceTests`
Expected: FAIL — compile error, `DesktopSample` and `DesktopResetInference` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/ClaudeUsageTray/Core/DesktopSample.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>One eligible sample of the Claude Desktop history, already filtered by the reader's own
/// rules (object shape, timestamp bounds, future tolerance). The inference sees exactly the samples
/// the displayed percentages come from, so "the newest sample" means the same object in both.</summary>
public sealed record DesktopSample(DateTimeOffset At, string? Org, int? FiveHour, int? SevenDay);
```

Create `src/ClaudeUsageTray/Core/DesktopResetInference.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>
/// Reconstructs the current 5-hour window's boundary from the Claude Desktop history, which carries
/// no reset field of any kind. A window started in the interval between two consecutive samples
/// whenever the newer one is above zero and either the older was zero or the value decreased; the
/// start is taken as the midpoint and the reset as start + 5 h.
///
/// The rule assumes every fh decrease is a boundary, which the file does not state. The evidence: the
/// 13 in-day windows reconstructed from these very decreases on a 30-day, 420-sample history came out
/// at 4.55–5.19 h. A decrease that was not a boundary would have produced a wildly non-5 h length, and
/// none did. The residual risk is self-limiting — a false start is one bad badge colour, re-evaluated
/// on the next tick, not a persistent state.
///
/// Every gate means emit nothing, never emit something weaker. The common case on a fresh install is
/// gate 4, and it must be quiet rather than an error.
/// </summary>
public static class DesktopResetInference
{
    /// <summary>The median sample gap is 15 min, so a bracket this wide is the measured ±8 min
    /// accuracy — 2.7 % of the period. Anything wider is not a boundary observation, it is a guess
    /// about a gap.</summary>
    public static readonly TimeSpan MaxBracket = TimeSpan.FromMinutes(20);

    /// <summary>Only the newest this many samples by timestamp participate, so the pair walk on a
    /// pathological file stays bounded. By timestamp, not by array position: ascending order has
    /// held on every machine measured, but nothing guarantees it, and capping the wrong end would
    /// silently discard exactly the samples the boundary is in.</summary>
    public const int MaxSamples = 20_000;

    /// <param name="org">The newest eligible sample's org, supplied by the reader rather than
    /// re-derived here, so the two cannot disagree about which sample is newest.</param>
    public static DateTimeOffset? FiveHourReset(IReadOnlyList<DesktopSample> samples, string? org,
        DateTimeOffset now)
    {
        if (samples.Count < 2) return null;

        // Sort defensively, then keep the newest MaxSamples — the tail of the sorted series, which
        // is what "by t, not by array position" means. The sort itself is unavoidable since order is
        // not guaranteed; the cap is what bounds the walk below.
        var sorted = samples.OrderBy(s => s.At).ToList();
        int first = Math.Max(0, sorted.Count - MaxSamples);

        // Gate 3 — no window is running, and pace at 0 % is meaningless anyway.
        if (sorted[^1].FiveHour is not > 0) return null;

        // Contiguous run, not a filter: a plain "keep samples matching the newest org" would pair two
        // same-org samples across an excluded other-org sample, and read a drop this design claims to
        // exclude as a boundary with a bracket narrow enough to pass gate 1. A missing org is its own
        // value and breaks a run like any other change.
        int runStart = sorted.Count - 1;
        while (runStart > first && sorted[runStart - 1].Org == org) runStart--;

        // The *last* qualifying pair, taken unconditionally — gate 1 then judges that pair alone.
        // Falling back to an earlier, narrower bracket when the newest boundary is poorly observed
        // would emit a reset for a window that has already turned over.
        (DesktopSample Previous, DesktopSample Current)? last = null;
        for (int i = runStart + 1; i < sorted.Count; i++)
        {
            var previous = sorted[i - 1];
            var current = sorted[i];
            if (current.FiveHour is not { } fh || fh <= 0) continue;
            // The second clause covers a user working straight through a boundary, where fh never
            // touches 0.
            if (previous.FiveHour is not { } before || !(before == 0 || fh < before)) continue;
            last = (previous, current);
        }

        // Gate 4 — history opens mid-window, or the only start sits behind a long gap.
        if (last is not { } pair) return null;

        var width = pair.Current.At - pair.Previous.At;
        // Gate 1 — a wider interval is not a boundary observation, it is a guess about a gap.
        if (width > MaxBracket) return null;

        var resetsAt = pair.Previous.At + width / 2 + UsageValues.FiveHourPeriod;
        // Gate 2 — an expired window says nothing about whatever may have started since.
        return now < resetsAt ? resetsAt : null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~DesktopResetInferenceTests`
Expected: PASS, 17 tests.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/DesktopSample.cs src/ClaudeUsageTray/Core/DesktopResetInference.cs tests/ClaudeUsageTray.Tests/DesktopResetInferenceTests.cs
git commit -m "feat: infer the 5-hour reset from the desktop history series

Refs #5"
```

---

### Task 3: `WeeklyAnchor`

**Files:**
- Create: `src/ClaudeUsageTray/Core/WeeklyAnchor.cs`
- Test: `tests/ClaudeUsageTray.Tests/WeeklyAnchorTests.cs`

**Interfaces:**
- Produces:
  - `sealed record WeeklyAnchor(DayOfWeek Day, TimeOnly TimeOfDay)`
  - `static WeeklyAnchor? WeeklyAnchor.TryParse(string? text)`
  - `string Format()` (instance) — canonical `"Thu 03:00"`
  - `static DateTimeOffset NextReset(WeeklyAnchor anchor, DateTimeOffset now, TimeZoneInfo zone)`

- [ ] **Step 1: Write the failing test**

Create `tests/ClaudeUsageTray.Tests/WeeklyAnchorTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Every case supplies an explicit TimeZoneInfo, so these run identically on any machine
/// and in CI — which is the whole reason Core never reads TimeZoneInfo.Local.</summary>
public class WeeklyAnchorTests
{
    // Central European Time: +01:00 winter, +02:00 summer, transitions on the last Sunday in
    // March (02:00 → 03:00) and October (03:00 → 02:00).
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Theory]
    [InlineData("Thu 03:00", DayOfWeek.Thursday, 3, 0)]
    [InlineData("thu 3:00", DayOfWeek.Thursday, 3, 0)]
    [InlineData("  WEDNESDAY   15:05  ", DayOfWeek.Wednesday, 15, 5)]
    [InlineData("Sun 00:00", DayOfWeek.Sunday, 0, 0)]
    [InlineData("Mon 23:59", DayOfWeek.Monday, 23, 59)]
    public void Parses(string text, DayOfWeek day, int hour, int minute)
    {
        var anchor = WeeklyAnchor.TryParse(text);
        Assert.NotNull(anchor);
        Assert.Equal(day, anchor.Day);
        Assert.Equal(new TimeOnly(hour, minute), anchor.TimeOfDay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Thu")]
    [InlineData("03:00")]
    [InlineData("Donnerstag 03:00")]
    [InlineData("Thu 25:00")]
    [InlineData("Thu 03:60")]
    [InlineData("Thu 03:00:00")]
    [InlineData("Xyz 03:00")]
    [InlineData("Thu 3pm")]
    public void JunkAndOutOfRange_ProduceNull(string? text) => Assert.Null(WeeklyAnchor.TryParse(text));

    [Fact]
    public void FormatIsCanonicalAndRoundTrips()
    {
        var anchor = WeeklyAnchor.TryParse("wednesday 3:05")!;
        Assert.Equal("Wed 03:05", anchor.Format());
        Assert.Equal(anchor, WeeklyAnchor.TryParse(anchor.Format()));
    }

    [Fact]
    public void NextReset_IsStrictlyAfterNow()
    {
        // now sits exactly on the anchor: the answer is a week later, not this instant.
        var anchor = WeeklyAnchor.TryParse("Thu 03:00")!;
        var now = new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero);   // a Thursday
        Assert.Equal(now.AddDays(7), WeeklyAnchor.NextReset(anchor, now, Utc));
    }

    [Fact]
    public void NextReset_LaterThisWeek()
    {
        var anchor = WeeklyAnchor.TryParse("Thu 03:00")!;
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);   // Tuesday
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero),
            WeeklyAnchor.NextReset(anchor, now, Utc));
    }

    [Fact]
    public void NextReset_UsesTheZonesOffsetNotUtc()
    {
        var anchor = WeeklyAnchor.TryParse("Thu 03:00")!;
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        // 2026-09-10 03:00 +02:00 (Berlin summer time) == 01:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero),
            WeeklyAnchor.NextReset(anchor, now, Berlin).ToUniversalTime());
    }

    [Fact]
    public void NextReset_SpringForwardGap_UsesTheFirstInstantAfterTheGap()
    {
        // 2027-03-28 is the last Sunday in March; 02:30 local does not exist. The first instant
        // after the gap is 03:00 +02:00 == 01:00 UTC.
        var anchor = WeeklyAnchor.TryParse("Sun 02:30")!;
        var now = new DateTimeOffset(2027, 3, 26, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero),
            WeeklyAnchor.NextReset(anchor, now, Berlin).ToUniversalTime());
    }

    [Fact]
    public void NextReset_FallBackAmbiguity_UsesTheEarlierOccurrence()
    {
        // 2026-10-25, 02:30 local happens twice. The earlier is +02:00 == 00:30 UTC; the later
        // would be +01:00 == 01:30 UTC.
        var anchor = WeeklyAnchor.TryParse("Sun 02:30")!;
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero),
            WeeklyAnchor.NextReset(anchor, now, Berlin).ToUniversalTime());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~WeeklyAnchorTests`
Expected: FAIL — compile error, `WeeklyAnchor` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/ClaudeUsageTray/Core/WeeklyAnchor.cs`:

```csharp
using System.Globalization;

namespace ClaudeUsageTray.Core;

/// <summary>
/// The user's own statement of when their weekly limit resets, e.g. "Thu 03:00". Stated rather than
/// inferred: the desktop history's sd series contains genuine off-lattice counter restarts that a
/// "last drop wins" rule would have misread on 2 of 6 detections, and the user can read the real
/// value off Claude's own UI. A fabricated weekly reset is worse than none.
///
/// The zone is always a parameter, never TimeZoneInfo.Local read in here: Core is deliberately
/// ambient-free, and the DST cases cannot be tested against a function with no zone to vary.
/// </summary>
public sealed record WeeklyAnchor(DayOfWeek Day, TimeOnly TimeOfDay)
{
    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>Weekday (three-letter abbreviation or full English name, any case) plus HH:mm.
    /// Null for anything else, including a valid-looking string with seconds or a 12-hour clock:
    /// the setting is round-tripped through Format, so accepting a shape we cannot re-emit would
    /// rewrite the user's file into something they never typed.</summary>
    public static WeeklyAnchor? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;

        // Enum.TryParse also accepts the numeric form, so "3 03:00" would parse and be rewritten to
        // "Wed 03:00" in the user's file — a shape they never typed.
        if (parts[0].Any(char.IsDigit)) return null;

        if (!Enum.TryParse<DayOfWeek>(parts[0], ignoreCase: true, out var day) || !Enum.IsDefined(day))
        {
            int index = Array.FindIndex(DayNames,
                n => string.Equals(n, parts[0], StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            day = (DayOfWeek)index;
        }

        // Exact formats only: "3:00" and "03:00" are the two the dialog and hand-editing produce.
        if (!TimeOnly.TryParseExact(parts[1], ["HH:mm", "H:mm"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time)) return null;

        return new WeeklyAnchor(day, time);
    }

    /// <summary>The canonical spelling written back to settings.json, so "thu 3:00" and "Thursday
    /// 03:00" do not persist as two different-looking values for one anchor.</summary>
    public string Format() => $"{DayNames[(int)Day]} {TimeOfDay:HH\\:mm}";

    /// <summary>The next occurrence strictly after now, in the given zone. Strictly after, because
    /// an anchor landing exactly on now describes the reset that just happened, not the next one.</summary>
    public static DateTimeOffset NextReset(WeeklyAnchor anchor, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        int ahead = ((int)anchor.Day - (int)local.DayOfWeek + 7) % 7;
        var day = local.Date.AddDays(ahead);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var candidate = Resolve(day.Add(anchor.TimeOfDay.ToTimeSpan()), zone);
            if (candidate > now) return candidate;
            day = day.AddDays(7);   // this week's occurrence has passed, or fell exactly on now
        }
        // Unreachable: one week on is always in the future. Kept total rather than throwing, because
        // nothing on this path may throw.
        return Resolve(day.Add(anchor.TimeOfDay.ToTimeSpan()), zone);
    }

    /// <summary>A wall-clock instant in a zone, made total across both DST discontinuities. Spring
    /// forward: the wall time does not exist, so use the first instant after the gap. Fall back: it
    /// occurs twice, so use the earlier of the two, which is the one carrying the larger (pre-
    /// transition) offset.
    ///
    /// The gap is located by walking back a minute at a time rather than by reading
    /// TimeZoneInfo.GetAdjustmentRules: the walk is bounded by the gap's own length (an hour in
    /// every real zone), and First() over the rules throws InvalidOperationException when none
    /// matches — an exception DesktopUsageReader's catch filter does not list, on a path whose
    /// contract is that it never throws.</summary>
    private static DateTimeOffset Resolve(DateTime wall, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(unspecified))
        {
            // The first wall minute inside the gap, read with the offset still in force just before
            // it, IS the transition instant — i.e. the first instant after the gap. Adding the
            // delta to the *requested* time instead would land that far past the transition.
            var gapStart = unspecified;
            while (zone.IsInvalidTime(gapStart.AddMinutes(-1))) gapStart = gapStart.AddMinutes(-1);
            return new DateTimeOffset(gapStart, zone.GetUtcOffset(gapStart.AddMinutes(-1)));
        }

        if (zone.IsAmbiguousTime(unspecified))
            return new DateTimeOffset(unspecified, zone.GetAmbiguousTimeOffsets(unspecified).Max());

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~WeeklyAnchorTests`
Expected: PASS.

If `TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")` throws on this Windows machine, switch the
test constant to `"W. Europe Standard Time"` — the same zone under its Windows id — and update the
comment. Do **not** weaken the two DST assertions to make them pass.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/WeeklyAnchor.cs tests/ClaudeUsageTray.Tests/WeeklyAnchorTests.cs
git commit -m "feat: add WeeklyAnchor parsing and next-occurrence projection

Refs #5"
```

---

### Task 4: The `weeklyResetAnchor` setting

**Files:**
- Modify: `src/ClaudeUsageTray/Core/Settings.cs` (the `Settings` class properties, and `NormalizeFields`)
- Test: `tests/ClaudeUsageTray.Tests/SettingsTests.cs`

**Interfaces:**
- Consumes: `WeeklyAnchor.TryParse`, `WeeklyAnchor.Format` (Task 3).
- Produces: `Settings.WeeklyResetAnchor` (`string?`, JSON `weeklyResetAnchor`, default null, canonicalised or nulled by `NormalizeFields`).

- [ ] **Step 1: Write the failing test**

Append to `tests/ClaudeUsageTray.Tests/SettingsTests.cs`. The file already has a temp-path/write
helper — use it rather than inventing one; the names below (`WriteSettings`, `SettingsPath`) stand in
for whatever it is actually called:

```csharp
    [Fact]
    public void WeeklyResetAnchor_DefaultsToNull()
    {
        Assert.Null(new Settings().WeeklyResetAnchor);
    }

    [Fact]
    public void WeeklyResetAnchor_IsCanonicalisedOnLoad()
    {
        var path = WriteSettings("""{"weeklyResetAnchor":"thu 3:00"}""");
        Assert.Equal("Thu 03:00", Settings.Load(path).WeeklyResetAnchor);
    }

    [Fact]
    public void WeeklyResetAnchor_IsCanonicalisedOnSave()
    {
        var path = SettingsPath();
        new Settings { WeeklyResetAnchor = "  wednesday 15:05 " }.Save(path);
        Assert.Equal("Wed 15:05", Settings.Load(path).WeeklyResetAnchor);
    }

    [Fact]
    public void WeeklyResetAnchor_Unparseable_BecomesNullAndLeavesOtherFieldsAlone()
    {
        var path = WriteSettings("""{"weeklyResetAnchor":"Donnerstag","thresholds":{"orange":40,"red":70}}""");
        var settings = Settings.Load(path);
        Assert.Null(settings.WeeklyResetAnchor);
        Assert.Equal(40, settings.Thresholds.Orange);
        Assert.Equal(70, settings.Thresholds.Red);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~SettingsTests`
Expected: FAIL — compile error, `Settings.WeeklyResetAnchor` does not exist.

- [ ] **Step 3: Write minimal implementation**

In `src/ClaudeUsageTray/Core/Settings.cs`, add next to `DesktopHistoryPathOverride`:

```csharp
    /// <summary>When the user's weekly limit resets, e.g. "Thu 03:00", as a weekday plus HH:mm in
    /// local time. Applies only to a Claude Desktop history snapshot, which carries no reset field;
    /// Claude Code reports its own and ignores this entirely. Stated rather than inferred — see
    /// <see cref="WeeklyAnchor"/>. Null when unset or unparseable, and then the 7-day row honestly
    /// says it has no reset time.</summary>
    public string? WeeklyResetAnchor { get; set; }
```

In `NormalizeFields`, immediately after the `DesktopStalenessHours` line:

```csharp
        // Parse-then-Format here, not in the dialog: NormalizeFields already runs on both Load and
        // Save, so this is the one place where the loader and the dialog cannot disagree about what
        // "thu 3:00" means.
        WeeklyResetAnchor = WeeklyAnchor.TryParse(WeeklyResetAnchor)?.Format();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SettingsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/Settings.cs tests/ClaudeUsageTray.Tests/SettingsTests.cs
git commit -m "feat: add the weeklyResetAnchor setting

Refs #5"
```

---

### Task 5: The reader assigns origins

**Files:**
- Modify: `src/ClaudeUsageTray/Core/DesktopUsageReader.cs`
- Test: `tests/ClaudeUsageTray.Tests/DesktopUsageReaderTests.cs`

**Interfaces:**
- Consumes: `DesktopSample`, `DesktopResetInference.FiveHourReset` (Task 2); `WeeklyAnchor`, `WeeklyAnchor.NextReset` (Task 3); `ResetOrigin` (Task 1).
- Produces:
  - `DesktopUsageReader.Read(string path, DateTimeOffset now, WeeklyAnchor? anchor = null, TimeZoneInfo? zone = null)`
  - `DesktopUsageReader.ReadFirst(IReadOnlyList<string> byFreshness, DateTimeOffset now, WeeklyAnchor? anchor = null, TimeZoneInfo? zone = null)`
  - `TryRead(string, DateTimeOffset)` keeps its two-argument signature — it is a test and convenience shim and never sees an anchor.

`zone` defaults to `TimeZoneInfo.Utc` when null — **not** `TimeZoneInfo.Local`: `Core` stays
ambient-free and every test is deterministic. `TrayApp` passes `TimeZoneInfo.Local` explicitly in
Task 9. Plumbing the anchor into `ReadFirst` as well as `Read` is load-bearing: `ReadFirst` is the
production entry point (`TrayApp.cs:142`), so an anchor wired only into `Read` would pass its unit
tests and be silently absent in the running tray.

- [ ] **Step 1: Write the failing test**

Add this helper beside the existing `Write` helper in `tests/ClaudeUsageTray.Tests/DesktopUsageReaderTests.cs`:

```csharp
    private static UsageSnapshot? ReadWith(string path, DateTimeOffset now, WeeklyAnchor anchor)
        => DesktopUsageReader.Read(path, now, anchor, TimeZoneInfo.Utc).Snapshot;
```

Then append:

```csharp
    /// <summary>The measured case from the issue-#5 comment: newest sample at 06:52 with fh = 55 and
    /// the window bracketed to 05:52–06:07 — midpoint 05:59:30, so 52.5 of 300 minutes elapsed
    /// (17.5 %) and a pace ratio of 3.14 → Red, where the absolute rule the tray applied said Orange.
    /// (The issue's own 17.7 % / 3.10 came from a 05:59:00 start; the midpoint rule gives 05:59:30.)
    ///
    /// This is a synthetic arithmetic regression, not empirical validation: it is built from the
    /// summary figures in that comment, since wus-it-1337's own file is not in this repo. It pins the
    /// formula, and would not catch the field semantics being wrong.</summary>
    private const string MeasuredCase = """
        {"version":2,"samples":[
          {"t":1789019520000,"org":"a","u":{"fh":91,"sd":40}},
          {"t":1789020420000,"org":"a","u":{"fh":4,"sd":40}},
          {"t":1789023120000,"org":"a","u":{"fh":55,"sd":41}}
        ]}
        """;

    // 1789019520000 = 2026-09-10 05:52Z, 1789020420000 = 06:07Z, 1789023120000 = 06:52Z.
    private static readonly DateTimeOffset MeasuredNow = new(2026, 9, 10, 6, 52, 0, TimeSpan.Zero);

    [Fact]
    public void MeasuredCase_InfersTheFiveHourResetAndPacesRed()
    {
        var s = DesktopUsageReader.TryRead(Write(MeasuredCase), MeasuredNow)!;

        // Bracket 05:52–06:07 → start 05:59:30, reset 10:59:30.
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 10, 59, 30, TimeSpan.Zero), s.FiveHour!.ResetsAt);
        Assert.Equal(ResetOrigin.Inferred, s.FiveHour.Origin);

        var severity = UsageValues.WindowSeverity(s.FiveHour, UsageValues.FiveHourPeriod,
            new Settings(), MeasuredNow);
        Assert.Equal(Severity.Red, severity);
        // The absolute rule, which is what the tray applied before this change, says Orange.
        Assert.Equal(Severity.Orange, SeverityRules.For(55));
    }

    [Fact]
    public void NoBracket_LeavesTheFiveHourResetNull()
    {
        var s = DesktopUsageReader.TryRead(Write("""
            {"version":2,"samples":[
              {"t":1789019520000,"org":"a","u":{"fh":30,"sd":40}},
              {"t":1789020420000,"org":"a","u":{"fh":55,"sd":41}}
            ]}
            """), MeasuredNow)!;
        Assert.Null(s.FiveHour!.ResetsAt);
        Assert.Equal(ResetOrigin.Reported, s.FiveHour.Origin);
    }

    [Fact]
    public void FutureSample_IsExcludedFromTheBracketAsWellAsFromThePercentages()
    {
        // A corrupt sample an hour ahead of now would otherwise be the newer half of the bracket.
        var future = MeasuredNow.AddHours(1).ToUnixTimeMilliseconds();
        var s = DesktopUsageReader.TryRead(Write($$"""
            {"version":2,"samples":[
              {"t":1789019520000,"org":"a","u":{"fh":91,"sd":40}},
              {"t":{{future}},"org":"a","u":{"fh":4,"sd":40}}
            ]}
            """), MeasuredNow)!;
        Assert.Equal(91, s.FiveHour!.Percent);   // the future sample did not win max-by-t
        Assert.Null(s.FiveHour.ResetsAt);        // and did not form a bracket either
    }

    [Fact]
    public void Anchor_SetsTheSevenDayResetAsStated()
    {
        var anchor = WeeklyAnchor.TryParse("Thu 12:00")!;
        var s = ReadWith(Write(MeasuredCase), MeasuredNow, anchor)!;
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero), s.SevenDay!.ResetsAt);
        Assert.Equal(ResetOrigin.Stated, s.SevenDay.Origin);
    }

    [Fact]
    public void NoAnchor_LeavesTheSevenDayResetNull()
    {
        var s = DesktopUsageReader.TryRead(Write(MeasuredCase), MeasuredNow)!;
        Assert.Null(s.SevenDay!.ResetsAt);
        Assert.Equal(ResetOrigin.Reported, s.SevenDay.Origin);
    }

    [Fact]
    public void ReadFirst_ForwardsTheAnchor()
    {
        // The production entry point. An anchor wired only into Read would pass every test above and
        // be silently absent in the running tray.
        var anchor = WeeklyAnchor.TryParse("Thu 12:00")!;
        var r = DesktopUsageReader.ReadFirst([Write(MeasuredCase)], MeasuredNow, anchor, TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero), r.Snapshot!.SevenDay!.ResetsAt);
        Assert.Equal(ResetOrigin.Stated, r.Snapshot.SevenDay.Origin);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~DesktopUsageReaderTests`
Expected: FAIL — compile error on the four-argument `Read` and on the `ResetOrigin` assertions.

- [ ] **Step 3: Write minimal implementation**

In `src/ClaudeUsageTray/Core/DesktopUsageReader.cs`:

Correct the class doc, which currently claims "There are no reset timestamps, so every window is
emitted with ResetsAt null" — replace that sentence with:

```
/// The file carries no reset timestamp of any kind: the five-hour boundary is reconstructed from the
/// series by <see cref="DesktopResetInference"/> and marked <see cref="ResetOrigin.Inferred"/>, and
/// the seven-day one comes from the user's own <see cref="WeeklyAnchor"/> when they set it. Both stay
/// null when unavailable, and pace colouring then falls back to the absolute thresholds as before.
```

Update the signatures:

```csharp
    public static UsageSnapshot? TryRead(string path, DateTimeOffset now) => Read(path, now).Snapshot;

    /// <param name="anchor">The user's stated weekly reset, or null. Applied to the seven-day window
    /// only, as <see cref="ResetOrigin.Stated"/>.</param>
    /// <param name="zone">The zone the anchor's wall time is read in. Defaults to UTC rather than
    /// Local so Core stays ambient-free and every test is deterministic; TrayApp passes
    /// TimeZoneInfo.Local.</param>
    public static DesktopHistoryResult Read(string path, DateTimeOffset now,
        WeeklyAnchor? anchor = null, TimeZoneInfo? zone = null)
```

Collect the eligible samples in the *same* loop that finds the newest, so the two views cannot
diverge — replace the existing loop and the three window constructions:

```csharp
            long futureCutoffMs = (now + SourceSelection.FutureTolerance).ToUnixTimeMilliseconds();
            JsonElement? newest = null;
            long newestT = long.MinValue;
            string? newestOrg = null;
            // Collected in the same pass, under the same eligibility rules, so the inference sees
            // exactly the series the displayed percentages came from.
            var series = new List<DesktopSample>();
            foreach (var sample in samples.EnumerateArray())
            {
                if (sample.ValueKind != JsonValueKind.Object) continue;
                if (!sample.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number
                    || !t.TryGetInt64(out var ms) || ms < MinUnixMs || ms > MaxUnixMs) continue;
                if (ms > futureCutoffMs) continue;
                if (!sample.TryGetProperty("u", out var u) || u.ValueKind != JsonValueKind.Object) continue;

                var org = UsageJson.NonEmptyString(sample, "org");
                series.Add(new DesktopSample(DateTimeOffset.FromUnixTimeMilliseconds(ms), org,
                    UsageJson.ReadRoundedPercent(u, "fh"), UsageJson.ReadRoundedPercent(u, "sd")));

                if (newest is null || ms > newestT) { newest = u; newestT = ms; newestOrg = org; }
            }
            if (newest is not { } usage) return new(null, DesktopHistoryStatus.NoSamples);

            var fiveReset = DesktopResetInference.FiveHourReset(series, newestOrg, now);
            var five = UsageJson.ReadRoundedPercent(usage, "fh") is { } fh
                ? new WindowUsage(fh, fiveReset)
                {
                    Origin = fiveReset is null ? ResetOrigin.Reported : ResetOrigin.Inferred,
                }
                : null;

            var sevenReset = anchor is null
                ? (DateTimeOffset?)null
                : WeeklyAnchor.NextReset(anchor, now, zone ?? TimeZoneInfo.Utc);
            var seven = UsageJson.ReadRoundedPercent(usage, "sd") is { } sd
                ? new WindowUsage(sd, sevenReset)
                {
                    Origin = sevenReset is null ? ResetOrigin.Reported : ResetOrigin.Stated,
                }
                : null;
```

`UsageJson.NonEmptyString` is `internal` and `Core` is the same assembly — no access change needed.

Finally `ReadFirst`:

```csharp
    public static DesktopHistoryResult ReadFirst(IReadOnlyList<string> byFreshness, DateTimeOffset now,
        WeeklyAnchor? anchor = null, TimeZoneInfo? zone = null)
    {
        DesktopHistoryResult? firstFailure = null;
        foreach (var path in byFreshness)
        {
            var result = Read(path, now, anchor, zone);
            if (result.Snapshot is not null) return result;
            if (result.Status != DesktopHistoryStatus.NotFound) firstFailure ??= result;
        }
        return firstFailure ?? new(null, DesktopHistoryStatus.NotFound);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: all pass. Every existing `DesktopUsageReaderTests` fixture keeps `ResetsAt == null` — the
two-sample fixture rises (63 → 64) and forms no bracket.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/DesktopUsageReader.cs tests/ClaudeUsageTray.Tests/DesktopUsageReaderTests.cs
git commit -m "feat: assign reset origins in the desktop history reader

Refs #5"
```

---

### Task 6: `UsageValue.NotifySeverity`

**Files:**
- Modify: `src/ClaudeUsageTray/Core/UsageValues.cs`
- Test: `tests/ClaudeUsageTray.Tests/UsageValuesTests.cs`

**Interfaces:**
- Produces: `UsageValue(string Key, string Label, int Percent, Severity Severity, DateTimeOffset? ResetsAt, Severity NotifySeverity)` — a **sixth positional member**, appended. `UsageValues.Enumerate` is the only producer in the tree, so no other call site changes.
- Produces: `UsageValues.WindowSeverities(WindowUsage usage, TimeSpan period, Settings settings, DateTimeOffset now)` returning `(Severity Draw, Severity Notify)`.

`WindowSeverity` keeps its existing signature and meaning (the drawing verdict), so `UsagePopup`,
`TrayApp` and `IconRenderer` are untouched.

- [ ] **Step 1: Write the failing test**

Append to `tests/ClaudeUsageTray.Tests/UsageValuesTests.cs`:

```csharp
    /// <summary>17.5 % of a 5-hour window elapsed at 55 % used: pace ratio 55/17.5 = 3.14 → Red,
    /// absolute (55, orange 50, red 85) → Orange. The two verdicts differ, which is what makes these
    /// cases meaningful. Expressed as time-until-reset so the elapsed fraction is legible.</summary>
    private static (WindowUsage Usage, DateTimeOffset Now) PacedRedAbsoluteOrange(ResetOrigin origin)
    {
        var now = new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero);
        var reset = now + TimeSpan.FromMinutes(247.5);   // 52.5 of 300 minutes gone
        return (new WindowUsage(55, reset) { Origin = origin }, now);
    }

    [Fact]
    public void Inferred_DrawsPaced_ButNotifiesAbsolute()
    {
        var (usage, now) = PacedRedAbsoluteOrange(ResetOrigin.Inferred);
        var value = UsageValues.Enumerate(
            new UsageSnapshot(now, usage, null) { Source = UsageSource.DesktopHistory },
            new Settings(), now)[0];

        Assert.Equal(Severity.Red, value.Severity);
        Assert.Equal(Severity.Orange, value.NotifySeverity);
    }

    [Theory]
    [InlineData(ResetOrigin.Reported)]
    [InlineData(ResetOrigin.Stated)]
    public void ReportedAndStated_AgreeOnBothSeverities(ResetOrigin origin)
    {
        var (usage, now) = PacedRedAbsoluteOrange(origin);
        var value = UsageValues.Enumerate(new UsageSnapshot(now, usage, null), new Settings(), now)[0];

        Assert.Equal(Severity.Red, value.Severity);
        Assert.Equal(value.Severity, value.NotifySeverity);
    }

    [Fact]
    public void Inferred_AboveRedAbove_StillNotifiesRed()
    {
        // A null elapsed fraction falls back to the absolute thresholds, and 90 % is past redAbove.
        // The inference is simply not what got it there.
        var now = new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero);
        var usage = new WindowUsage(90, now.AddHours(4)) { Origin = ResetOrigin.Inferred };
        var value = UsageValues.Enumerate(
            new UsageSnapshot(now, usage, null) { Source = UsageSource.DesktopHistory },
            new Settings(), now)[0];

        Assert.Equal(Severity.Red, value.NotifySeverity);
    }

    [Fact]
    public void ScopedLimitsAndCredits_HaveEqualSeverities()
    {
        // Neither can be Inferred: only the desktop reader assigns that, and it emits neither.
        var now = new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero);
        var snapshot = new UsageSnapshot(now, null, null,
            [new ScopedLimit("Fable", null, 92, now.AddDays(2), true)],
            new CreditUsage(null, null, 40, null, new CreditState(true, null, false)));

        foreach (var value in UsageValues.Enumerate(snapshot, new Settings(), now))
            Assert.Equal(value.Severity, value.NotifySeverity);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~UsageValuesTests`
Expected: FAIL — compile error, `UsageValue.NotifySeverity` does not exist.

- [ ] **Step 3: Write minimal implementation**

In `src/ClaudeUsageTray/Core/UsageValues.cs`, replace the record:

```csharp
/// <summary>One notifiable value of a snapshot. Key is stable across polls and is what the
/// notifier files its state under; Label is the popup's caption text, so a toast and the row it
/// refers to cannot be worded differently.
///
/// Two severities, computed in the same call from the same inputs — the anti-drift property that
/// matters, not the field count. Severity is what the badge and the bar draw. NotifySeverity is what
/// every notification decision reads, and it ignores an inferred reset: a wrong badge colour is a
/// glance you re-check, a wrong toast is an interruption you cannot undo. On Claude Code data, and
/// on any stated or reported reset, the two are equal by construction.</summary>
public sealed record UsageValue(string Key, string Label, int Percent, Severity Severity,
    DateTimeOffset? ResetsAt, Severity NotifySeverity);
```

Add beside `WindowSeverity`:

```csharp
    /// <summary>The drawing verdict and the notification verdict for one window, from one set of
    /// inputs. An inferred reset is withheld from the second — a null fraction still falls back to
    /// the absolute thresholds, so an inferred-reset value at 90 % toasts exactly as it does today.</summary>
    public static (Severity Draw, Severity Notify) WindowSeverities(WindowUsage usage, TimeSpan period,
        Settings settings, DateTimeOffset now)
    {
        var fraction = TimeMarker.ElapsedFraction(usage.ResetsAt, period, now);
        return (SeverityRules.ForSettings(settings, usage.Percent, fraction),
            SeverityRules.ForSettings(settings, usage.Percent,
                usage.Origin == ResetOrigin.Inferred ? null : fraction));
    }
```

In `Enumerate`, use it for the two windows and pass the single severity twice for the others:

```csharp
        if (snapshot.FiveHour is { } five)
        {
            var (draw, notify) = WindowSeverities(five, FiveHourPeriod, settings, now);
            values.Add(new("5h", "5-hour window", five.Percent, draw, five.ResetsAt, notify));
        }
        if (snapshot.SevenDay is { } seven)
        {
            var (draw, notify) = WindowSeverities(seven, SevenDayPeriod, settings, now);
            values.Add(new("7d", "7-day window", seven.Percent, draw, seven.ResetsAt, notify));
        }
        foreach (var limit in snapshot.ScopedLimits)
        {
            // Scoped limits and credits never carry an inferred reset: only the desktop reader
            // assigns Inferred, and it emits neither.
            var severity = ScopedSeverity(limit, settings, now);
            values.Add(new(limit.Label, $"{limit.Label} weekly", limit.Percent, severity, limit.ResetsAt, severity));
        }
        if (snapshot.Credits is { } credits)
        {
            var severity = CreditSeverity(credits, settings);
            values.Add(new("credits", "Credits", credits.Percent, severity, null, severity));
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/UsageValues.cs tests/ClaudeUsageTray.Tests/UsageValuesTests.cs
git commit -m "feat: give UsageValue a separate notification severity

Refs #5"
```

---

### Task 7: The notifier reads `NotifySeverity` everywhere

**Files:**
- Modify: `src/ClaudeUsageTray/Core/NotificationRules.cs:55-58` (the `Fingerprint` record) and the four decision sites at `:141`, `:156`, `:211`, `:219`
- Test: `tests/ClaudeUsageTray.Tests/NotificationRulesUsageTests.cs`

**Interfaces:**
- Consumes: `UsageValue.NotifySeverity` (Task 6), `Settings.WeeklyResetAnchor` (Task 4).
- Produces: no public surface change. `Fingerprint` gains a seventh member, `string? WeeklyResetAnchor`.

- [ ] **Step 1: Write the failing test**

Append to `tests/ClaudeUsageTray.Tests/NotificationRulesUsageTests.cs`. If the file has no armed-rules
helper, add this one first:

```csharp
    private static NotificationRules ArmedRules()
    {
        var rules = new NotificationRules();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        return rules;
    }
```

```csharp
    /// <summary>A desktop snapshot whose 5-hour window carries an inferred reset. untilReset fixes
    /// the elapsed fraction: 247.5 min left of 300 is 17.5 % gone, 30 min left is 90 % gone.</summary>
    private static UsageSnapshot Inferred(DateTimeOffset now, int percent, double untilResetMinutes)
        => new(now, new WindowUsage(percent, now + TimeSpan.FromMinutes(untilResetMinutes))
        {
            Origin = ResetOrigin.Inferred,
        }, null)
        { Source = UsageSource.DesktopHistory };

    /// <summary>55 % at 17.5 % elapsed: ratio 3.14 → paced Red, absolute Orange.</summary>
    private static UsageSnapshot InferredPacedRed(DateTimeOffset now, int percent)
        => Inferred(now, percent, 247.5);

    /// <summary>60 % at 90 % elapsed: ratio 0.67 → paced Green, absolute Orange. The mirror image of
    /// the case above, and the only shape that tells the two latch rules apart.</summary>
    private static UsageSnapshot InferredPacedGreen(DateTimeOffset now)
        => Inferred(now, 60, 30);

    [Fact]
    public void InferredPacedRed_RaisesNoRedToast()
    {
        var now = new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero);
        var rules = ArmedRules();
        var settings = new Settings();

        // Baseline at a green percentage, then the paced-red / absolute-orange reading.
        rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 10), false), settings, now);
        var outcome = rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 55), false), settings, now);

        Assert.Null(outcome.Notification);
    }

    [Fact]
    public void InferredValue_AtLevelOrange_IsWordedFromTheNotifyVerdict()
    {
        var now = new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero);
        var rules = ArmedRules();
        var settings = new Settings
        {
            UsageNotifications = new UsageNotificationSettings { Enabled = true, Level = NotifyLevel.Orange },
        };

        rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 10), false), settings, now);
        var outcome = rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 55), false), settings, now);

        // It crossed into Orange under the absolute rule, so the toast says orange — not red, which
        // is only what the badge draws.
        Assert.NotNull(outcome.Notification);
        Assert.Equal("Usage limit orange", outcome.Notification.Title);
        Assert.Contains("now orange", outcome.Notification.Body);
    }

    [Fact]
    public void HysteresisLatch_KeysOffTheNotifyVerdict_NotTheDrawnOne()
    {
        var now = new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero);
        var rules = ArmedRules();
        var settings = new Settings();   // level Red

        // Baseline green, then an absolute red that toasts and latches.
        rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 10), false), settings, now);
        Assert.NotNull(rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 90), false), settings, now).Notification);

        // Drawn severity Green, notify severity Orange. Reading the drawn one here would release the
        // latch on a green the notifier never acted on; reading NotifySeverity holds it.
        rules.OnUsage(new DisplayChoice(InferredPacedGreen(now), false), settings, now);

        // Back to red without ever having gone green by the notifier's reckoning: still latched.
        Assert.Null(rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 90), false), settings, now).Notification);
    }

    [Fact]
    public void HysteresisLatch_ATrueGreenStillReleasesIt()
    {
        var now = new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero);
        var rules = ArmedRules();
        var settings = new Settings();

        rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 10), false), settings, now);
        Assert.NotNull(rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 90), false), settings, now).Notification);
        rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 10), false), settings, now);
        Assert.NotNull(rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 90), false), settings, now).Notification);
    }

    [Fact]
    public void WeeklyResetAnchor_IsPartOfTheFingerprint()
    {
        // Editing the anchor shifts the 7-day elapsed fraction and can flip Green to Red; without
        // this entry the user's own edit fires the toast.
        var now = new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero);
        var rules = ArmedRules();
        var before = new Settings();
        var after = new Settings { WeeklyResetAnchor = "Wed 15:00" };

        rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 90), false), before, now);
        var outcome = rules.OnUsage(new DisplayChoice(InferredPacedRed(now, 90), false), after, now);

        Assert.Contains(outcome.Log, l => l.Contains("fingerprint change"));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~NotificationRulesUsageTests`
Expected: FAIL — `InferredPacedRed_RaisesNoRedToast` emits a notification, and
`WeeklyResetAnchor_IsPartOfTheFingerprint` finds no fingerprint line.

- [ ] **Step 3: Write minimal implementation**

In `src/ClaudeUsageTray/Core/NotificationRules.cs`, extend the fingerprint:

```csharp
    private sealed record Fingerprint(int Orange, int Red, bool PaceColors, int StalenessMinutes,
        int DesktopStalenessHours, NotifyLevel Level, string? WeeklyResetAnchor)
    {
        public static Fingerprint Of(Settings s) => new(s.Thresholds.Orange, s.Thresholds.Red, s.PaceColors,
            s.StalenessMinutes, s.DesktopStalenessHours, s.UsageNotifications.Level, s.WeeklyResetAnchor);
    }
```

and name it in the log line:

```csharp
            log.Add("notify[usage]: fingerprint change (thresholds/pace/staleness/level/anchor); rebaselining");
```

Add the single accessor, immediately above `AtOrAbove`:

```csharp
    /// <summary>The verdict every notification decision reads. One accessor rather than four reads
    /// of the field, so a fifth decision site cannot be added against the drawing severity: an
    /// inferred reset colours the badge but must never be the reason for an interruption.</summary>
    private static Severity Verdict(UsageValue value) => value.NotifySeverity;
```

Then switch exactly these four sites:

```csharp
            bool above = AtOrAbove(Verdict(value), level);                          // was value.Severity  (:141)
            ...
            if (Verdict(value) == Severity.Green) state.Notified = false;           // was value.Severity  (:156)
```

and inside `Compose`:

```csharp
            var group = crossed.Where(v => Verdict(v) == severity).ToList();        // was v.Severity      (:211)
            ...
        var worst = crossed.Any(v => Verdict(v) == Severity.Red) ? "red" : "orange";  //                   (:219)
```

`Compose` is `static`, so `Verdict` is `static` too.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: all pass. Then confirm nothing was missed:

```bash
grep -n "value\.Severity\|v\.Severity" src/ClaudeUsageTray/Core/NotificationRules.cs
```

Expected: no matches.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/NotificationRules.cs tests/ClaudeUsageTray.Tests/NotificationRulesUsageTests.cs
git commit -m "fix: base every notification decision on NotifySeverity

Refs #5"
```

---

### Task 8: The popup marks estimates and explains a missing reset

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/UsagePopup.cs:44-45` (the two call sites) and `:89-100` (`AddWindowRow`)
- Test: `tests/ClaudeUsageTray.Tests/UsagePopupSourceTests.cs`

**Interfaces:**
- Consumes: `WindowUsage.Origin` (Task 1).
- Produces: `AddWindowRow(TableLayoutPanel layout, string title, WindowUsage? usage, TimeSpan period, Settings settings, DateTimeOffset now, bool desktop)` — a seventh parameter, `desktop`, passed as `snapshot.Source == UsageSource.DesktopHistory` from both call sites.

Exact fragments, appended after the percentage, so the tests and the implementation cannot drift:

| State | Fragment |
|---|---|
| `Inferred` | ` · ~resets in 3h 05m` |
| `Stated` / `Reported` with a reset | ` · resets in 2d 4h` (unchanged) |
| `ResetsAt` null, desktop source | ` · no reset time` |
| `ResetsAt` null, Claude Code source | `""` (unchanged) |

- [ ] **Step 1: Write the failing test**

Append to `tests/ClaudeUsageTray.Tests/UsagePopupSourceTests.cs`:

```csharp
    [Fact]
    public void DesktopSource_InferredReset_IsMarkedWithATilde()
    {
        var five = new WindowUsage(55, Now.AddHours(4)) { Origin = ResetOrigin.Inferred };
        var popup = Popup(new DisplayChoice(Desktop(TimeSpan.FromMinutes(5), five, null), false));
        Assert.Contains(Texts(popup), t => t.Contains("~resets in "));
    }

    [Fact]
    public void DesktopSource_StatedReset_IsIndistinguishableFromReported()
    {
        // The user asserted it; marking their own answer as doubtful is noise.
        var seven = new WindowUsage(40, Now.AddDays(2)) { Origin = ResetOrigin.Stated };
        var popup = Popup(new DisplayChoice(Desktop(TimeSpan.FromMinutes(5), null, seven), false));
        Assert.Contains(Texts(popup), t => t.Contains("· resets in ") && !t.Contains("~"));
    }

    [Fact]
    public void DesktopSource_NoReset_SaysSoInTheRow()
    {
        // The row exists because the percentage exists; the fragment explains why the rest is missing.
        var popup = Popup(new DisplayChoice(Desktop(TimeSpan.FromMinutes(5), new(7, null), new(17, null)), false));
        Assert.Contains(Texts(popup), t => t.StartsWith("5-hour window — 7%") && t.Contains("· no reset time"));
    }

    [Fact]
    public void ClaudeCodeSource_NoReset_SaysNothingNew()
    {
        // Regression: a Claude Code user sees today's behaviour byte for byte.
        var cli = new UsageSnapshot(Now.AddMinutes(-2), new(7, null), new(17, null));
        var popup = Popup(new DisplayChoice(cli, false));
        Assert.DoesNotContain(Texts(popup), t => t.Contains("no reset time") || t.Contains("~resets"));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~UsagePopupSourceTests`
Expected: FAIL — the first three assertions find no such text.

- [ ] **Step 3: Write minimal implementation**

In `src/ClaudeUsageTray/Tray/UsagePopup.cs`, hoist the source test and pass it at both call sites:

```csharp
            bool desktop = snapshot.Source == UsageSource.DesktopHistory;
            AddWindowRow(layout, "5-hour window", snapshot.FiveHour, UsageValues.FiveHourPeriod, settings, now, desktop);
            AddWindowRow(layout, "7-day window", snapshot.SevenDay, UsageValues.SevenDayPeriod, settings, now, desktop);
```

and reuse that same local in the `updated` line below, replacing its inline comparison.

Then `AddWindowRow`:

```csharp
    /// <param name="desktop">Whether the snapshot came from the Claude Desktop history. Gates both
    /// new fragments: a Claude Code user must see today's row byte for byte, and Reported being the
    /// default origin is not on its own enough to guarantee that.</param>
    private static void AddWindowRow(TableLayoutPanel layout, string title, WindowUsage? usage,
        TimeSpan period, Settings settings, DateTimeOffset now, bool desktop)
    {
        if (usage is null)
        {
            layout.Controls.Add(new Label { Text = $"{title}: no data", AutoSize = true });
            return;
        }
        // The tilde carries the hedge inside the 240 px row; the sentence lives in the tooltip, where
        // there is room for it. Stated is deliberately unmarked — the user asserted it.
        var resets = usage.ResetsAt is { } r
            ? $" · {(desktop && usage.Origin == ResetOrigin.Inferred ? "~" : "")}resets in {RelativeTime.In(r, now)}"
            : desktop ? " · no reset time" : "";
        var elapsed = TimeMarker.ElapsedFraction(usage.ResetsAt, period, now);
        AddCaption(layout, $"{title} — {usage.Percent}%{resets}{PaceSuffix(usage.Percent, elapsed, settings)}");
        AddBar(layout, usage.Percent, UsageValues.WindowSeverity(usage, period, settings, now), elapsed);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: all pass, `UsagePopupSourceTests` included.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Tray/UsagePopup.cs tests/ClaudeUsageTray.Tests/UsagePopupSourceTests.cs
git commit -m "feat: mark inferred resets and explain a missing one in the popup

Refs #5"
```

---

### Task 9: `TrayApp` plumbs the anchor and glosses the tooltip

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs:142-144` (the `ReadFirst` call) and `:378-401` (`BuildTooltip`)
- Test: covered by Task 5's `ReadFirst_ForwardsTheAnchor`; no new test file

`TrayApp` is a WinForms `ApplicationContext` with no test harness of its own; both changes are
straight-line wiring over logic already tested in `Core`. Verify by running the app (Step 4).

**Interfaces:**
- Consumes: `DesktopUsageReader.ReadFirst(byFreshness, now, anchor, zone)` (Task 5), `WeeklyAnchor.TryParse` (Task 3), `WindowUsage.Origin` (Task 1).

- [ ] **Step 1: Plumb the anchor into the desktop read**

Replace the `ReadFirst` call at `TrayApp.cs:142`:

```csharp
        // TimeZoneInfo.Local belongs here, not in Core: the anchor is a wall-clock statement, and
        // Core stays ambient-free so its DST cases are testable against an explicit zone.
        var desktop = DesktopUsageReader.ReadFirst(
            DesktopHistoryPath.ByFreshness(
                DesktopHistoryPath.Candidates(_settings.DesktopHistoryPathOverride,
                    DesktopHistoryPath.DefaultAppData, DesktopHistoryPath.DefaultLocalAppData)),
            now,
            WeeklyAnchor.TryParse(_settings.WeeklyResetAnchor),
            TimeZoneInfo.Local);
```

- [ ] **Step 2: Add the two tooltip branches**

The spec's presentation table gives the tooltip a line for `Inferred` *and* one for a desktop
snapshot with no reset at all — the popup row's `no reset time` fragment has no room to say why.
In `BuildTooltip`, inside the existing `if (choice.Snapshot is { } snapshot)` block, after the two
desktop/stale lines:

```csharp
            // The tooltip is where a sentence fits; the popup row carries only the short fragment.
            if (desktop && usage.Origin == ResetOrigin.Inferred)
                parts.Add("estimated from Claude Desktop history");
            else if (desktop && usage.ResetsAt is null)
                parts.Add("Claude Desktop history carries no reset time");
```

`desktop` is the local already computed in that block and `usage` is the method's own parameter, so
both branches are gated on the source twice over — deliberately. The `else` matters: the two states
are mutually exclusive (an inferred origin always has a reset), and chaining them makes that
explicit rather than relying on it.

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet test`
Expected: all pass. No behavioural test changes here; this is the compile-and-regression gate.

- [ ] **Step 4: Verify in the running app**

Run: `dotnet run --project src/ClaudeUsageTray`

Point `desktopHistoryPathOverride` at a hand-written fixture that reproduces the measured case
(a decrease within 20 minutes, newest `fh > 0`, timestamps within the last few hours). Hover the tray
icon: the tooltip must carry `estimated from Claude Desktop history` and a `resets in …` part, and
the popup's 5-hour row must show `~resets in …` with an elapsed marker in the bar. On Claude Code
data the tooltip must be unchanged.

**Do not probe the live usage endpoint to force a state** — it is a shared per-token budget and even
~15 quick probes cause minutes of 429s for the user's real Claude Code sessions.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Tray/TrayApp.cs
git commit -m "feat: plumb the weekly anchor and gloss inferred resets in the tooltip

Refs #5"
```

---

### Task 10: The Claude Desktop settings group

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/SettingsDialog.cs` (fields, ctor, `BuildLayout`, `LoadFrom`, `WireLiveSync`, `Draft`, `Clone`)
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs:520-521`
- Test: `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`

**Interfaces:**
- Consumes: `Settings.WeeklyResetAnchor` (Task 4), `WeeklyAnchor.TryParse` / `Format` (Task 3).
- Produces: `SettingsDialog(Settings settings, bool canRunAtStartup, bool runAtStartup, Func<Settings, bool> save, UpdateOptions updates, bool desktopSource)` — a **sixth parameter, appended, with no default**, so no call site can forget it.
- Produces: a `TextBox` named `weeklyAnchor` and a `Label` named `weeklyAnchorError`, both present only when `desktopSource` is true.

The gate is **frozen at open time**: `SourceSelection.Choose` can flip source on any 30 s tick, and a
group that appears or vanishes under an open dialog — possibly discarding a half-typed anchor — is
worse than one that is briefly out of date. The cost is accepted deliberately: a user on Claude Code
today who will fall back to desktop data tomorrow cannot set the anchor in advance, and the field
reappears the next time the dialog is opened while desktop data is the active source.

- [ ] **Step 1: Write the failing test**

In `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`, give the file's existing `Dialog(...)` helper
a `bool desktopSource = false` parameter defaulting to today's behaviour and forward it to the
constructor, so every existing call is untouched. Then append:

```csharp
    private static T? Find<T>(Control root, string name) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T match && match.Name == name) return match;
            if (Find<T>(c, name) is { } nested) return nested;
        }
        return null;
    }

    [Fact]
    public void WithoutADesktopSource_TheClaudeDesktopGroupIsAbsent()
    {
        // The invariant: a Claude Code user is never shown a setting that does nothing for them.
        var dialog = Dialog(new Settings());
        Assert.Null(Find<TextBox>(dialog, "weeklyAnchor"));
    }

    [Fact]
    public void WithADesktopSource_TheAnchorFieldShowsTheCanonicalValue()
    {
        var dialog = Dialog(new Settings { WeeklyResetAnchor = "Thu 03:00" }, desktopSource: true);
        Assert.Equal("Thu 03:00", Find<TextBox>(dialog, "weeklyAnchor")!.Text);
    }

    [Fact]
    public void ValidAnchor_ReachesTheDraftCanonicalised()
    {
        var dialog = Dialog(new Settings(), desktopSource: true);
        Find<TextBox>(dialog, "weeklyAnchor")!.Text = "thu 3:00";
        Assert.Equal("Thu 03:00", dialog.Draft().WeeklyResetAnchor);
    }

    [Fact]
    public void BlankAnchor_ClearsTheSetting()
    {
        var dialog = Dialog(new Settings { WeeklyResetAnchor = "Thu 03:00" }, desktopSource: true);
        Find<TextBox>(dialog, "weeklyAnchor")!.Text = "  ";
        Assert.Null(dialog.Draft().WeeklyResetAnchor);
    }

    [Fact]
    public void UnparseableAnchor_ShowsAnErrorAndKeepsTheStoredValue()
    {
        // Silently nulling what they typed would look like the field simply does not work.
        var dialog = Dialog(new Settings { WeeklyResetAnchor = "Thu 03:00" }, desktopSource: true);
        Find<TextBox>(dialog, "weeklyAnchor")!.Text = "Donnerstag";
        Assert.True(Find<Label>(dialog, "weeklyAnchorError")!.Visible);
        Assert.Equal("Thu 03:00", dialog.Draft().WeeklyResetAnchor);
    }

    [Fact]
    public void AnchorSurvivesADialogOpenedWithoutADesktopSource()
    {
        // A CLI-source dialog must not wipe an anchor the user set while on desktop data.
        var dialog = Dialog(new Settings { WeeklyResetAnchor = "Thu 03:00" });
        Assert.Equal("Thu 03:00", dialog.Draft().WeeklyResetAnchor);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~SettingsDialogTests`
Expected: FAIL — compile error on the `desktopSource` argument.

- [ ] **Step 3: Write minimal implementation**

In `src/ClaudeUsageTray/Tray/SettingsDialog.cs`, add the fields beside `_desktopStaleness`:

```csharp
    private readonly TextBox _weeklyAnchor = new() { Name = "weeklyAnchor", Width = 120 };
    private readonly Label _weeklyAnchorError = new()
    {
        Name = "weeklyAnchorError",
        Text = "Use a weekday and a time, e.g. Thu 03:00.",
        AutoSize = true,
        ForeColor = Color.Firebrick,
        Visible = false,
    };
    private readonly bool _desktopSource;
```

Add the constructor parameter and assign it before `Controls.Add(BuildLayout())`:

```csharp
    /// <param name="desktopSource">Whether the Claude Desktop history is the active source right
    /// now. Frozen at open time on purpose: SourceSelection.Choose can flip on any 30 s tick, and a
    /// group that vanishes under an open dialog — discarding a half-typed anchor — is worse than one
    /// that is briefly out of date.</param>
    public SettingsDialog(Settings settings, bool canRunAtStartup, bool runAtStartup,
        Func<Settings, bool> save, UpdateOptions updates, bool desktopSource)
    {
        _draft = Clone(settings);
        _desktopSource = desktopSource;
```

In `BuildLayout`, add the note under the pace checkbox — gated, like every other new branch: it is
true on Claude Code data too, but the invariant is that a Claude Code user's dialog is unchanged:

```csharp
        layout.Controls.Add(Indent(_paceColors));
        if (_desktopSource)
        {
            layout.Controls.Add(Indent(new Label
            {
                Text = "Needs a reset time; without one the plain thresholds decide.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
            }));
        }
```

and the group after the Refresh section, before `Heading("About")`:

```csharp
        // Only while the desktop history is the live source: a Claude Code user puzzling over a
        // setting that does nothing for them is the far more common outcome than the reverse.
        if (_desktopSource)
        {
            layout.Controls.Add(Heading("Claude Desktop"));
            layout.Controls.Add(Indent(new Label
            {
                Text = "Weekly reset (read it off Claude's own UI), e.g. Thu 03:00",
                AutoSize = true,
            }));
            layout.Controls.Add(Indent(_weeklyAnchor));
            layout.Controls.Add(Indent(_weeklyAnchorError));
        }
```

In `LoadFrom`, inside the `_suspendSync` block:

```csharp
        _weeklyAnchor.Text = source.WeeklyResetAnchor ?? "";
```

In `WireLiveSync`, beside the other handlers:

```csharp
        _weeklyAnchor.TextChanged += (_, _) =>
        {
            if (_suspendSync) return;
            _weeklyAnchorError.Visible = !string.IsNullOrWhiteSpace(_weeklyAnchor.Text)
                && WeeklyAnchor.TryParse(_weeklyAnchor.Text) is null;
        };
```

In `Draft()`, after the `DesktopStalenessHours` line:

```csharp
        // Hidden group: keep whatever the clone carries, so opening the dialog on Claude Code data
        // cannot wipe an anchor set while the desktop history was the source. Blank clears it;
        // unparseable keeps the stored value, since silently nulling what they typed would look like
        // the field does not work.
        if (_desktopSource)
        {
            draft.WeeklyResetAnchor = string.IsNullOrWhiteSpace(_weeklyAnchor.Text)
                ? null
                : WeeklyAnchor.TryParse(_weeklyAnchor.Text)?.Format() ?? draft.WeeklyResetAnchor;
        }
```

In `Clone`, add:

```csharp
        WeeklyResetAnchor = source.WeeklyResetAnchor,
```

Add `_weeklyAnchor` to the control array at `SettingsDialog.cs:362` alongside `_desktopStaleness`,
matching whatever that array is used for.

Finally, in `src/ClaudeUsageTray/Tray/TrayApp.cs`, pass the gate:

```csharp
        _settingsDialog = new SettingsDialog(_settings, _isVelopackInstalled, TryIsStartupEnabled(),
            ApplySettings, BuildUpdateOptions(),
            SourceSelection.Choose(_cliSnapshot, _desktopSnapshot, DateTimeOffset.UtcNow, _settings)
                .Snapshot?.Source == UsageSource.DesktopHistory);
```

With no snapshot at all, `Snapshot` is null and the group is hidden — the specified behaviour.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

Then run the app and open Settings with a desktop-source fixture in place, and confirm the group
renders, the error label appears only for junk, and Save round-trips `Thu 03:00` into
`%APPDATA%\ClaudeUsageTray\settings.json`:

Run: `dotnet run --project src/ClaudeUsageTray`

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Tray/SettingsDialog.cs src/ClaudeUsageTray/Tray/TrayApp.cs tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs
git commit -m "feat: add the Claude Desktop weekly-anchor setting

Refs #5"
```

---

### Task 11: Staleness regression, README, changelog and the spec's implementation notes

**Files:**
- Test: `tests/ClaudeUsageTray.Tests/UsageValuesTests.cs`
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Modify: `docs/superpowers/specs/2026-09-10-desktop-reset-inference-design.md`

**Interfaces:**
- Consumes: everything above. Produces nothing new.

- [ ] **Step 1: Write the staleness-direction test**

The spec's "considered and left alone" section claims a stale desktop snapshot paced against a live
`now` *understates* the ratio. Pin the direction, since it is the argument for accepting the risk.
Append to `tests/ClaudeUsageTray.Tests/UsageValuesTests.cs`:

```csharp
    [Fact]
    public void StaleSnapshotPacedAgainstLiveNow_Understates()
    {
        // Same percentage, same inferred reset; the only difference is how much of the window has
        // elapsed by the time we look. A larger elapsed fraction yields a smaller ratio, so the
        // verdict can only soften — never escalate — as a snapshot ages.
        var reset = new DateTimeOffset(2026, 9, 10, 11, 0, 0, TimeSpan.Zero);
        var usage = new WindowUsage(55, reset) { Origin = ResetOrigin.Inferred };
        var settings = new Settings();

        var fresh = UsageValues.WindowSeverities(usage, UsageValues.FiveHourPeriod, settings,
            new DateTimeOffset(2026, 9, 10, 6, 52, 0, TimeSpan.Zero)).Draw;
        var stale = UsageValues.WindowSeverities(usage, UsageValues.FiveHourPeriod, settings,
            new DateTimeOffset(2026, 9, 10, 10, 30, 0, TimeSpan.Zero)).Draw;

        Assert.Equal(Severity.Red, fresh);
        Assert.True(stale < fresh, $"expected the aged reading to soften, got {stale}");
    }

    [Fact]
    public void AboveRedAbove_StaysRedHoweverStale()
    {
        // ForPace returns Red unconditionally above redAbove: running out is running out.
        var reset = new DateTimeOffset(2026, 9, 10, 11, 0, 0, TimeSpan.Zero);
        var usage = new WindowUsage(95, reset) { Origin = ResetOrigin.Inferred };
        Assert.Equal(Severity.Red, UsageValues.WindowSeverities(usage, UsageValues.FiveHourPeriod,
            new Settings(), new DateTimeOffset(2026, 9, 10, 10, 55, 0, TimeSpan.Zero)).Draw);
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~UsageValuesTests`
Expected: PASS. The first assertion pins the *direction*, not a particular colour, so it holds
whatever the exact ratios work out to.

- [ ] **Step 3: Update the README**

In the section that describes the Claude Desktop history source, add:

```markdown
On a machine where the Claude Desktop history is the active source, the file carries no reset
timestamps of its own. The tray reconstructs the 5-hour boundary from the sample series and marks it
with a tilde — `~resets in 3h 05m` — so pace colouring, the elapsed marker and the pace text all
work; the tooltip says `estimated from Claude Desktop history`. An estimate never raises a
notification: those still fire on the plain percentage thresholds. When the boundary cannot be
established the row says `no reset time` rather than guessing.

The weekly window has no reliable signature in the file, so it is not inferred. Read your weekly
reset off Claude's own UI and enter it under **Settings → Claude Desktop** as a weekday and time
(`weeklyResetAnchor`, e.g. `Thu 03:00`). A stated anchor is treated as fact: it renders unmarked and
does raise notifications. The group appears only while the desktop history is the active source.
```

Add `weeklyResetAnchor` to the README's settings-key table in the neighbouring keys' style: *"When
the weekly limit resets, as a weekday plus HH:mm in local time, e.g. `Thu 03:00`. Claude Desktop
history source only; unset or unparseable means the 7-day row shows no reset time."*

- [ ] **Step 4: Add the changelog entry**

Add to `CHANGELOG.md`, in the open unreleased section (create one following the file's own
convention if none is open):

```markdown
### Added
- Claude Desktop history: the 5-hour reset is now inferred from the sample series, so pace colours,
  the elapsed marker and `resets in …` work on a desktop-only machine. Estimates are marked with a
  tilde and never raise a notification.
- `weeklyResetAnchor` setting and a **Claude Desktop** settings group: state your weekly reset
  (e.g. `Thu 03:00`) and the 7-day window paces against it.

### Fixed
- A window with no known reset now says `no reset time` on a desktop snapshot, instead of leaving a
  silent blank where the reset would be.
```

- [ ] **Step 5: Record the implementation deviations in the spec**

Append to `docs/superpowers/specs/2026-09-10-desktop-reset-inference-design.md`, so the next reader
is not surprised by three additive choices the plan made:

```markdown
## Implementation notes

Four details were settled during implementation and are recorded here rather than left to be
rediscovered:

- `UsageValue`'s second severity is a **sixth positional member**, `NotifySeverity`, appended after
  `ResetsAt`. `UsageValues.Enumerate` is its only producer in the tree, so no call site outside that
  method changes. The pair is computed by `UsageValues.WindowSeverities`, which returns
  `(Severity Draw, Severity Notify)`; `WindowSeverity` keeps its existing signature and meaning, so
  the popup, the badge and the icon renderer are untouched.
- `DesktopUsageReader.Read` and `ReadFirst` take **two** optional arguments, `WeeklyAnchor? anchor`
  and `TimeZoneInfo? zone`, and `zone` defaults to `TimeZoneInfo.Utc` rather than `Local`: `Core`
  stays ambient-free and every reader test is deterministic. `TrayApp` passes `TimeZoneInfo.Local`.
- **Gate 1 judges the last candidate pair, and only that one.** The walk takes the last qualifying
  pair unconditionally and then applies the 20-minute rule to it; a wide last bracket rejects the
  whole inference rather than falling back to an earlier, narrower one. Falling back would emit a
  reset for a window a more recent — if poorly observed — boundary says has already turned over.
- **"An inferred reset never influences a toast" means never decides that a toast fires, or at what
  level.** `NotificationRules.Compose` still reads `UsageValue.ResetsAt` to set the Action Center
  slot's `ExpiresAt`, so an inferred reset can shorten or lengthen a notification's lifetime. That
  is accepted: the toast it affects was already correctly raised on the absolute thresholds, and the
  alternative — a seventh field carrying a notify-side reset — buys nothing a user would notice.

## DST resolution

`WeeklyAnchor.Resolve` locates a spring-forward gap by walking back one minute at a time from the
requested wall time, rather than reading `TimeZoneInfo.GetAdjustmentRules`. Two reasons, both
load-bearing: `Enumerable.First` over the rules throws `InvalidOperationException` when none matches,
and that exception is not in `DesktopUsageReader`'s catch filter — on a path whose contract is that
it never throws; and adding `DaylightDelta` to the *requested* time (rather than to the gap's start)
lands that far past the transition, which is not "the first instant after the gap".
```

- [ ] **Step 6: Full verification**

Run: `dotnet test --configuration Release`
Expected: all pass — this is what CI runs.

- [ ] **Step 7: Commit**

```bash
git add tests/ClaudeUsageTray.Tests/UsageValuesTests.cs README.md CHANGELOG.md docs/superpowers/specs/2026-09-10-desktop-reset-inference-design.md
git commit -m "docs: document desktop reset inference and the weekly anchor

Closes #5"
```

---

## Coverage against the spec

| Spec section | Task |
|---|---|
| Decision 1 — infer the 5-hour reset behind gates | 2, 5 |
| Decision 2 — do not infer the weekly reset | 3, 4 (no lattice fit anywhere) |
| Decision 3 — desktop-only; every new branch tests `Source` | 8, 9, 10 (regressions in 8 and 10) |
| Decision 4 — an inferred reset never influences a toast | 6, 7 |
| Decision 5 — a stated anchor is not an estimate | 1, 5, 8 |
| Architecture — origin as a field on `WindowUsage` | 1 |
| New files — `DesktopSample`, `DesktopResetInference`, `WeeklyAnchor` | 2, 3 |
| Changed types — `ResetOrigin`, `UsageValue`, `Settings`, reader signatures | 1, 4, 5, 6 |
| `TimeMarker` / `SeverityRules` / `SnapshotPrecedence` / `SourceSelection` untouched | — (no task modifies them) |
| Shared eligibility, defensive sort, 20 000 cap by `t` | 2, 5 |
| Org handling — contiguous segments, not a filter | 2 |
| Gates 1–4 | 2 |
| Weekly anchor — parse, format, `NextReset`, both DST rules | 3 |
| `NormalizeFields` canonicalisation on load and save | 4 |
| Where origins are assigned | 5 |
| Presentation table — tilde, unmarked, `no reset time` | 8 |
| Tooltip — the `Inferred` suffix **and** the null-reset explanation | 9 |
| Settings group, frozen `desktopSource` gate, pace-colours note | 10 |
| Notifications — two severities, four call sites, fingerprint | 6, 7 |
| Testing section — every listed case | 2, 3, 4, 5, 6, 7, 8, 10, 11 |
| Considered and left alone — staleness direction, no new logging | 11 (staleness); no task adds a log line |
