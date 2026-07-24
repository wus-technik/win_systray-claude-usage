# v0.2 Icon Readability — Filled-Badge Tray Icons

**Fixes:** [#2](https://github.com/wus-technik/win_systray-claude-usage/issues/2) — v0.1 progress-ring icons are far too fine and nearly unreadable at real tray size.

**Decision basis:** five variants rendered with the production GDI+ code at 16/24 px on dark and light taskbar swatches. Chosen: **V3 filled pie**. Prototyping also exposed a second v0.1 defect this design fixes: the white center digit is nearly invisible on light taskbars because the ring center is transparent.

## Design

`IconRenderer` switches from a stroked progress ring to a **filled badge**. Draw order, all antialiased on a transparent bitmap:

1. **Base disc** — ellipse inset 0.5 px from the icon bounds, filled with the severity color at **alpha 90** (translucent tint marking the unused fraction).
2. **Usage wedge** — solid severity-colored pie slice (`FillPie`) from 12 o'clock (−90°), sweep = `360 × clamp(percent, 0, 100) / 100`; clockwise for the 5-hour icon, counter-clockwise (negative sweep) for the 7-day icon. Unchanged semantics from v0.1.
3. **Rim** — 1 px ellipse outline in the solid severity color.
4. **Digit** — the window digit (`5`/`7`), Segoe UI Bold at **0.60 × size** px, centered, white, over a **1 px dark halo** (the digit drawn at the 8 surrounding ±1 px offsets in black at alpha 140) so it reads on both the filled and unfilled halves and on light taskbars.

**Severity colors are unchanged:** Green (64, 184, 96), Orange (232, 150, 40), Red (224, 68, 68).

**Dimmed (stale) treatment keeps its current meaning,** applied to the new elements: severity color reduced to alpha 120 before drawing disc/wedge/rim, digit white at alpha 160, halo black at alpha 90.

**`RenderNeutral`** becomes the badge equivalent of the no-data state: grey (150, 150, 150) base disc + rim, no wedge (percent 0), centered em-dash with the same halo.

## Unchanged

- Public API: `Render(char digit, int percent, Severity severity, bool clockwise, bool dimmed, int size)`, `RenderNeutral(int size)`, `SystemTrayIconSize()` — signatures and semantics identical, so `TrayApp`, `UsagePopup`, and all callers need no changes.
- HICON lifetime pattern (GetHicon → clone → DestroyIcon).
- Percent clamping at the renderer; tooltip/popup behavior; severity thresholds.

## Ride-along changes

- `tests/ClaudeUsageTray.Tests/IconRendererTests.cs`: existing smoke tests must still pass unmodified (they assert size, non-blankness, clamp-no-throw — all geometry-agnostic).
- `README.md`: icon description updated — "ring fill = usage" becomes badge/fill wording.
- `docs/icon-preview.html`: preview geometry updated to the filled-badge design so the design reference matches the shipped renderer.
- `src/ClaudeUsageTray/ClaudeUsageTray.csproj`: `<Version>` bumped to `0.2.0` (next release tag: `v0.2.0`).

## Acceptance

- All existing tests pass; build warning-free.
- 16 px renders of `5`@42 green and `7`@88 red are legible on dark AND light swatches (human visual check against a regenerated comparison render).
- Stale/dimmed and neutral states remain visually distinct from live states.
