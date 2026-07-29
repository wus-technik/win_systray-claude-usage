# Time-Elapsed Marker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Draw a small vertical notch on each usage progress bar in the tray popup marking how far the clock has advanced through that limit's reset period, so a bar shows at a glance whether spend is ahead of or behind the clock.

**Architecture:** A new pure static helper `TimeMarker` in `Core` converts a `ResetsAt` timestamp plus a period length into an elapsed fraction, or `null` when the data can't be trusted. `UsagePopup` supplies the period per row (5h for the session window, 7d for weekly and scoped-weekly rows, none for credits) and passes the resulting fraction into the already custom-drawn `AddBar`, which paints two 2px ticks at that x position.

**Tech Stack:** C# on .NET 10 (`net10.0-windows`), WinForms with `System.Drawing` GDI+ painting, xUnit for tests.

**Spec:** `docs/superpowers/specs/2026-07-29-time-elapsed-marker-design.md` — read it before starting.
**Issue:** [#6](https://github.com/wus-technik/win_systray-claude-usage/issues/6)

## Global Constraints

- Build: `dotnet build ClaudeUsageTray.sln`. Test: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`.
- Target framework `net10.0-windows`, `Nullable` enabled, `ImplicitUsings` enabled, `LangVersion` latest — all set in `Directory.Build.props`; do not add per-project overrides.
- Decision logic lives in `src/ClaudeUsageTray/Core/` as pure static helpers and is unit-tested. Drawing code in `src/ClaudeUsageTray/Tray/` is not unit-tested. Follow this split.
- `now` is always passed in as a `DateTimeOffset` parameter. Never call `DateTimeOffset.Now`/`UtcNow` inside `Core` helpers.
- Period lengths are 5 hours (5-hour window) and 7 days (7-day window, all scoped weekly limits). Credits get no marker.
- Out-of-range fractions are hidden, not clamped: `null` reset time, fraction `> 1`, fraction `< 0`, and non-positive period all yield no marker. Exactly `0.0` and `1.0` are in range and do draw.
- The bar stays 240×12. Marker ticks are drawn at the top and bottom edges with `SystemPens.ControlText`, positioned within the bar's inner region x=1..238 so the border cannot occlude them.
- Comments explain *why*, not *what*, matching the existing density in `UsagePopup.cs` and `PopupRows.cs`. Do not add narration comments.
- All produced text (code comments, commit messages) in English.
- Do not add a `Co-Authored-By` trailer to commits.

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `src/ClaudeUsageTray/Core/TimeMarker.cs` | Create | Convert `(ResetsAt, period, now)` into an elapsed fraction or `null`. Pure, no drawing, no clock. |
| `tests/ClaudeUsageTray.Tests/TimeMarkerTests.cs` | Create | Unit tests for `TimeMarker.ElapsedFraction`, including every hide case. |
| `src/ClaudeUsageTray/Tray/UsagePopup.cs` | Modify | Supply the per-row period, thread the fraction through to `AddBar`, paint the notch. |

Two tasks. Task 1 delivers the tested decision logic; Task 2 delivers the visible feature. A reviewer could reject the drawing while accepting the helper, so they are split there and nowhere else.

---

### Task 1: `TimeMarker.ElapsedFraction`

**Files:**
- Create: `src/ClaudeUsageTray/Core/TimeMarker.cs`
- Test: `tests/ClaudeUsageTray.Tests/TimeMarkerTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `public static double? ClaudeUsageTray.Core.TimeMarker.ElapsedFraction(DateTimeOffset? resetsAt, TimeSpan period, DateTimeOffset now)`. Returns a value in `[0.0, 1.0]` inclusive, or `null` when no marker should be drawn. Task 2 calls this.

- [ ] **Step 1: Write the failing test**

Create `tests/ClaudeUsageTray.Tests/TimeMarkerTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class TimeMarkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThreeHoursLeftOfFiveHourWindowIsFortyPercentElapsed()
        => AssertFraction(0.4, TimeMarker.ElapsedFraction(Now.AddHours(3), TimeSpan.FromHours(5), Now));

    [Fact]
    public void TwoDaysLeftOfSevenDayWindowIsFiveSeventhsElapsed()
        => AssertFraction(5d / 7d, TimeMarker.ElapsedFraction(Now.AddDays(2), TimeSpan.FromDays(7), Now));

    [Fact]
    public void ResetDueNowIsFullyElapsed()
        => AssertFraction(1.0, TimeMarker.ElapsedFraction(Now, TimeSpan.FromHours(5), Now));

    [Fact]
    public void ResetOnePeriodOutIsNotElapsedAtAll()
        => AssertFraction(0.0, TimeMarker.ElapsedFraction(Now.AddHours(5), TimeSpan.FromHours(5), Now));

    [Fact]
    public void StaleResetInThePastIsHidden()
        => Assert.Null(TimeMarker.ElapsedFraction(Now.AddMinutes(-1), TimeSpan.FromHours(5), Now));

    [Fact]
    public void ResetBeyondOnePeriodIsHidden()
        => Assert.Null(TimeMarker.ElapsedFraction(Now.AddHours(6), TimeSpan.FromHours(5), Now));

    [Fact]
    public void MissingResetTimeIsHidden()
        => Assert.Null(TimeMarker.ElapsedFraction(null, TimeSpan.FromHours(5), Now));

    [Fact]
    public void ZeroPeriodIsHidden()
        => Assert.Null(TimeMarker.ElapsedFraction(Now.AddHours(3), TimeSpan.Zero, Now));

    [Fact]
    public void NegativePeriodIsHidden()
        => Assert.Null(TimeMarker.ElapsedFraction(Now.AddHours(3), TimeSpan.FromHours(-5), Now));

    private static void AssertFraction(double expected, double? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value, precision: 6);
    }
}
```

`AssertFraction` exists because every assertion has to unwrap the `double?` and compare the result inexactly — `Assert.Equal(expected, actual, precision: 6)` compares both values rounded to 6 decimal places (it is decimal-place rounding, not an absolute tolerance), which is what makes the `5d / 7d` case pass. Doing both in one helper keeps the nine test bodies to one line each.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj --filter TimeMarker`
Expected: FAIL at compile time — `error CS0103: The name 'TimeMarker' does not exist in the current context` (or `CS0246`). A compile failure is the correct red state here.

- [ ] **Step 3: Write the minimal implementation**

Create `src/ClaudeUsageTray/Core/TimeMarker.cs`:

```csharp
namespace ClaudeUsageTray.Core;

public static class TimeMarker
{
    /// <summary>Elapsed fraction (0..1) of a period ending at resetsAt, or null when it cannot be
    /// trusted: no reset time, a non-positive period, or a fraction outside 0..1. Out-of-range is
    /// hidden rather than clamped in both directions — a marker pinned to an edge would assert a
    /// position the data does not support. A fraction above 1 means resetsAt is already past (a
    /// stale snapshot); below 0 means it is further out than one period (inconsistent data).</summary>
    public static double? ElapsedFraction(DateTimeOffset? resetsAt, TimeSpan period, DateTimeOffset now)
    {
        if (resetsAt is not { } reset || period <= TimeSpan.Zero) return null;

        var fraction = 1 - (reset - now) / period;
        return fraction is >= 0 and <= 1 ? fraction : null;
    }
}
```

`(reset - now) / period` is `TimeSpan`-over-`TimeSpan` division, which yields a `double` — no tick arithmetic needed. The `is >= 0 and <= 1` pattern also rejects `NaN`, so no separate guard is required.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj --filter TimeMarker`
Expected: PASS, 9 tests.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/ClaudeUsageTray/Core/TimeMarker.cs tests/ClaudeUsageTray.Tests/TimeMarkerTests.cs
git commit -m "feat: derive elapsed-period fraction for usage bar markers"
```

---

### Task 2: Draw the notch on the popup bars

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/UsagePopup.cs` — `AddWindowRow` (lines 82–93), `AddScopedRow` (98–104), `AddBar` (141–160), and the two `AddWindowRow` call sites (39–40)
- Do **not** modify: `AddCreditRow` (106–125). It is listed here only to be explicit that it stays untouched — credits opt out of the marker via `AddBar`'s parameter default.

**Interfaces:**
- Consumes: `TimeMarker.ElapsedFraction(DateTimeOffset? resetsAt, TimeSpan period, DateTimeOffset now)` from Task 1, returning `double?`.
- Produces: nothing consumed by later tasks. `AddBar`'s new parameter is private to `UsagePopup`.

There is no unit test for this task. Drawing in `Tray/` is deliberately untested across this codebase (only `IconRenderer`, which returns a bitmap, has tests); the decision logic that *could* be wrong was tested in Task 1. Verification here is a build plus a visual check of the running app.

- [ ] **Step 1: Add the period parameter to `AddWindowRow` and pass the fraction to `AddBar`**

Replace the `AddWindowRow` method (currently lines 82–93) with:

```csharp
    private static void AddWindowRow(TableLayoutPanel layout, string title, WindowUsage? usage,
        TimeSpan period, Settings settings, DateTimeOffset now)
    {
        if (usage is null)
        {
            layout.Controls.Add(new Label { Text = $"{title}: no data", AutoSize = true });
            return;
        }
        var resets = usage.ResetsAt is { } r ? $" · resets in {RelativeTime.In(r, now)}" : "";
        AddCaption(layout, $"{title} — {usage.Percent}%{resets}");
        AddBar(layout, usage.Percent, SeverityFor(usage.Percent, settings),
            TimeMarker.ElapsedFraction(usage.ResetsAt, period, now));
    }
```

- [ ] **Step 2: Update the two `AddWindowRow` call sites**

The period is supplied here rather than carried on `WindowUsage` because the payload has no period field — the popup is what knows which window it is drawing. Replace lines 39–40:

```csharp
            AddWindowRow(layout, "5-hour window", snapshot.FiveHour, TimeSpan.FromHours(5), settings, now);
            AddWindowRow(layout, "7-day window", snapshot.SevenDay, TimeSpan.FromDays(7), settings, now);
```

- [ ] **Step 3: Pass the fraction from `AddScopedRow`**

Every scoped limit is weekly — the caption already says "weekly" — so the period is fixed at 7 days. Replace the body of `AddScopedRow` (currently lines 100–104) with:

```csharp
    {
        var resets = limit.ResetsAt is { } r ? $" · resets in {RelativeTime.In(r, now)}" : "";
        AddCaption(layout, $"{limit.Label} weekly — {limit.Percent}%{resets}");
        AddBar(layout, limit.Percent, SeverityFor(limit.Percent, settings),
            TimeMarker.ElapsedFraction(limit.ResetsAt, TimeSpan.FromDays(7), now));
    }
```

Leave the existing `<summary>` doc comment above the method untouched.

- [ ] **Step 4: Add the marker parameter and drawing to `AddBar`**

`AddCreditRow` is deliberately not modified: `CreditUsage` has no reset time, so credits opt out simply by not passing a fraction, which the parameter default handles.

Replace the `AddBar` method (currently lines 141–160) with:

```csharp
    /// <summary>Custom-drawn bar (ProgressBar can't be recolored per-severity). elapsedFraction, when
    /// non-null, marks how far the clock has moved through the limit's reset period: fill short of the
    /// marker is burning slower than the clock, fill past it is on track to hit the cap early.</summary>
    private static void AddBar(TableLayoutPanel layout, int percent, Severity severity,
        double? elapsedFraction = null)
    {
        var barColor = severity switch
        {
            Severity.Red => Color.FromArgb(224, 68, 68),
            Severity.Orange => Color.FromArgb(232, 150, 40),
            _ => Color.FromArgb(64, 184, 96),
        };
        var bar = new Panel { Width = 240, Height = 12, Margin = new Padding(0, 0, 0, 4) };
        var filled = Math.Clamp(percent, 0, 100);
        bar.Paint += (_, e) =>
        {
            e.Graphics.FillRectangle(SystemBrushes.ControlLight, 0, 0, bar.Width, bar.Height);
            using var brush = new SolidBrush(barColor);
            e.Graphics.FillRectangle(brush, 0, 0, bar.Width * filled / 100, bar.Height);
            if (elapsedFraction is { } fraction)
            {
                // Ticks inset from the edges rather than a full-height line: the fill stays visually
                // continuous. x is mapped into the inner region (1..Width-2) because the border drawn
                // below owns x=0 and x=Width-1 — a marker at fraction 0.0 or 1.0 would be painted over.
                var x = 1 + (int)Math.Round((bar.Width - 3) * fraction);
                e.Graphics.DrawLine(SystemPens.ControlText, x, 0, x, 2);
                e.Graphics.DrawLine(SystemPens.ControlText, x, bar.Height - 3, x, bar.Height - 1);
            }
            e.Graphics.DrawRectangle(SystemPens.ControlDark, 0, 0, bar.Width - 1, bar.Height - 1);
        };
        layout.Controls.Add(bar);
    }
```

The border is drawn last so it stays on top of both the fill and the ticks, exactly as before.
That is why the x mapping is inset: with a 240px bar the border verticals sit at x=0 and x=239, so
`1 + Math.Round(237 * fraction)` puts fraction `0.0` at x=1 and `1.0` at x=238 — both visible.
Each tick's outer pixel (y=0, y=Height-1) is covered by the border's horizontal lines, leaving 2
visible pixels per tick; that is intended, and why the lines span 3 pixels.

`SystemPens.ControlText` rather than a mid-grey: black holds roughly 5:1 contrast against the red
fill `(224,68,68)`, where `ControlDarkDark` (`#696969`) manages only about 1.3:1 and would vanish
on exactly the bars the user most needs to read.

- [ ] **Step 5: Build and run the full suite**

Run: `dotnet build ClaudeUsageTray.sln` then `dotnet test tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
Expected: build succeeds with no warnings introduced; all tests pass, including `SmokeTests`.

- [ ] **Step 6: Visual check**

Run: `dotnet run --project src/ClaudeUsageTray/ClaudeUsageTray.csproj`
Then click the tray icon to open the popup and confirm:
- The 5-hour and 7-day bars each show a notch at top and bottom, at a position consistent with their "resets in …" countdown (e.g. "resets in 1h" on the 5-hour bar puts the notch near 80%).
- Scoped weekly rows show a notch; the Credits row shows none.
- The notch is visible both where it falls over the colored fill and where it falls over the grey background.
- The notch is legible over a **red** bar specifically (a limit above the red threshold). This is the worst contrast case and the reason the pen is `ControlText`; if no bar is currently red, temporarily lower `Thresholds.Red` in the settings file to force one rather than skipping this check.
- Nothing is drawn outside the bar's border.

If the tray already has a running instance, `SingleInstance` will refuse to start a second one — close the existing instance first.

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Tray/UsagePopup.cs
git commit -m "feat: mark elapsed period on usage bars (#6)"
```

---

## Out of Scope

Do not implement these; they were explicitly excluded by the spec:

- Any change to the tray icon.
- Any change to caption or countdown text.
- User-configurable period lengths or a setting to hide the marker.
- Moving the period onto `WindowUsage` / `ScopedLimit` or into `UsageJson` parsing.
