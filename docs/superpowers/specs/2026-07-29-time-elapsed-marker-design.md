# Time-elapsed marker on usage progress bars

Date: 2026-07-29
Issue: [#6](https://github.com/wus-technik/win_systray-claude-usage/issues/6)

## Problem

A usage bar shows how much of a limit is spent but not whether that spend is fast or slow.
40% of the 5-hour window is comfortable one hour in and alarming four hours in, and the popup
gives no way to tell those apart without reading the countdown text and doing arithmetic.

## Solution

Draw a small vertical marker on each bar at the point the *clock* has reached within that
limit's period. The bar then reads on its own:

- fill ends left of the marker → burning slower than the clock
- fill ends right of the marker → on track to hit the cap before the period resets

## Marker position

The marker sits at the elapsed fraction of the period:

```
fraction = 1 - (resetsAt - now) / period
```

Examples from the issue:

| Row         | Period | Time remaining | Fraction  |
|-------------|--------|----------------|-----------|
| 5-hour      | 5h     | 3h             | 0.4 (2/5) |
| 7-day       | 7d     | 2d             | ~0.71 (5/7) |

## Where the period comes from

The payload carries `ResetsAt` but no period length, so the period is supplied per row by the
popup, which already knows statically which row it is drawing:

| Row                  | Period |
|----------------------|--------|
| 5-hour window        | 5h     |
| 7-day window         | 7d     |
| Scoped weekly limits | 7d     |
| Credits              | none — no marker |

Rejected alternatives:

- **Carry the period on `WindowUsage`/`ScopedLimit`.** Would mean changing `UsageJson` and its
  tests to move a constant the parser never receives from the payload. No current consumer
  benefits.
- **Infer the period from the magnitude of `ResetsAt`.** Guesswork; a weekly limit resetting in
  four hours is indistinguishable from the session window.

Credits have no reset time in `CreditUsage` at all, so they need no special case beyond not
opting in.

## Out-of-range handling

The marker is hidden — nothing drawn — whenever the fraction cannot be trusted:

- `ResetsAt` is null
- `ResetsAt` is in the past (fraction > 1), i.e. a stale snapshot after a period rollover
- `ResetsAt` is further out than the period length (fraction < 0), i.e. inconsistent data
- the period is zero or negative (defensive; no caller does this today)

A pinned marker at either edge would assert a position the data does not support, so
out-of-range is treated the same as unknown in both directions. Exact `0.0` and `1.0` are in
range and do draw.

## Components

### `TimeMarker` — new, `src/ClaudeUsageTray/Core/TimeMarker.cs`

Pure and unit-testable, following the existing `PopupRows` / `SeverityRules` pattern of keeping
decisions out of the drawing code.

```csharp
public static class TimeMarker
{
    /// <summary>Elapsed fraction (0..1) of a period ending at resetsAt, or null when it cannot
    /// be trusted: no reset time, non-positive period, or a fraction outside 0..1.</summary>
    public static double? ElapsedFraction(DateTimeOffset? resetsAt, TimeSpan period, DateTimeOffset now);
}
```

Depends on nothing but `DateTimeOffset` and `TimeSpan`. `now` is passed in, as everywhere else
in the codebase, so tests need no clock abstraction.

### `UsagePopup` — modified, `src/ClaudeUsageTray/Tray/UsagePopup.cs`

- `AddWindowRow` gains a `TimeSpan period` parameter. Call sites pass `TimeSpan.FromHours(5)`
  and `TimeSpan.FromDays(7)`.
- `AddScopedRow` uses `TimeSpan.FromDays(7)`; every scoped row is a weekly limit and its caption
  already says so.
- `AddBar(layout, percent, severity, double? elapsedFraction = null)`. The default keeps the
  credits call site unchanged.
- `AddCreditRow` passes no marker.

### Drawing

Inside the existing `Paint` handler, after the fill and before the border, when
`elapsedFraction` is non-null:

```csharp
var x = 1 + (int)Math.Round((bar.Width - 3) * fraction);
e.Graphics.DrawLine(SystemPens.ControlText, x, 0, x, 2);
e.Graphics.DrawLine(SystemPens.ControlText, x, bar.Height - 3, x, bar.Height - 1);
```

An inset notch — 2px ticks biting in from the top and bottom edges — rather than a full-height
line, so the fill stays visually continuous.

The x mapping targets the bar's *inner* region, x=1..238, not its full width. The border is
drawn last as `DrawRectangle(0, 0, Width - 1, Height - 1)`, so its verticals own x=0 and x=239;
a marker placed there would be computed correctly and then painted over. Since fractions of
exactly `0.0` and `1.0` are in range and must be visible, the offset is required, not cosmetic.
The ticks' outer endpoints at y=0 and y=Height-1 are likewise covered by the border, leaving 2
visible pixels of each 3-pixel tick — acceptable, and the reason the ticks are 3px long.

`SystemPens.ControlText` is a single fixed color for all cases. Black reaches roughly 5:1
contrast against the worst-case red fill (`224,68,68`) and 18:1 against the `ControlLight`
unfilled background, so no per-side inversion is needed and the draw stays at two calls.
`ControlDarkDark` (`#696969`) was rejected: against the red fill it measures about 1.3:1, which
would make the marker invisible on precisely the bars that matter most.

The bar stays 240×12, leaving 8px of unbroken fill between the ticks.

## Testing

`tests/ClaudeUsageTray.Tests/TimeMarkerTests.cs` covers `ElapsedFraction`:

- 3h remaining of a 5h period → 0.4
- 2d remaining of a 7d period → 5/7
- `resetsAt == now` → 1.0 (in range, draws at the right edge)
- `resetsAt == now + period` → 0.0 (in range, draws at the left edge)
- `resetsAt` in the past → null
- `resetsAt` beyond one period out → null
- `resetsAt` null → null
- `TimeSpan.Zero` period → null

Floating-point comparisons round to 6 decimal places. The drawing itself is not tested, consistent with
the rest of `UsagePopup`.

## Out of scope

- The tray icon. This is a popup-only change.
- Any change to caption text; the countdown already states the time remaining.
- User-configurable period lengths or marker visibility.
