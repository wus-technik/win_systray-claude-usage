# Tabbed settings layout — design

## Problem

`SettingsDialog` stacks every section in one vertical `TableLayoutPanel`: Tray icons, Colour
thresholds, Platform status, Notifications, Refresh, Claude Desktop (conditionally) and About, plus
the beta-releases checkbox. Each feature has added a section, and the form is `AutoSize`d to their
sum — it is now a tall column that needs scanning from top to bottom to find one setting, and on a
laptop screen it approaches the working height.

Nothing about the *content* is wrong. What is wrong is that seven unrelated groups share one page.

## Decision

Replace the single stack with a `TabControl` of four pages. The error label and the
Reset/Cancel/Save row stay **outside** the tabs, at the bottom of the form.

| Tab | Sections |
|---|---|
| `general` — "General" | Tray icons (mode radios, Run at startup + its greyed caption); Refresh (both spinners); Claude Desktop (weekly anchor), only when `_desktopSource` |
| `appearance` — "Appearance" | Colour thresholds (Orange/Red spinners), pace colouring + its conditional caption, the preview bar and its caption |
| `status` — "Status" | Platform status (both watch checkboxes, component filter boxes, captions, hints); Notifications (usage row with its level combo, both status checkboxes) |
| `about` — "About" | The About grid (creator, installed version, update status, check and Update now) and the beta-releases checkbox |

The grouping follows what a user changes together, not the order the sections were written in.
Notifications sits with Platform status because two of its three controls are per-source status
toggles, and the beta checkbox sits with About because it steers the updater directly above it.

Rejected: one tab per existing heading (five tabs, two of them nearly empty, and a crowded strip),
and three tabs (the General page stays tall enough that the change buys little).

## Sizing

`TabControl` does not auto-size to its pages. Each page hosts its own `AutoSize` /
`GrowAndShrink` `TableLayoutPanel`; after the four are built, the `TabControl` is given a fixed
`Size` equal to the largest `PreferredSize` among them, plus the tab frame's own padding
(`DisplayRectangle` inset measured off the built control, not a hardcoded constant).

`Multiline` stays off, so a control narrower than its own four headers would grow scroll arrows
rather than wrap; the width is therefore the larger of the widest page and the measured tab strip.
And because the app is `PerMonitorV2`, the measurement is repeated on `DpiChangedAfterParent`:
WinForms rescales the fixed size it was given, but not by exactly the factor the pages' preferred
sizes move by — font rounding and the hint labels' fixed 320 px wrap width both drift.

Two consequences, both wanted:

- The window does not resize when the user switches tabs. A dialog that jumps under the pointer is
  worse than a page with some slack at the bottom.
- The form's height is set by the *tallest* page rather than the sum of all sections, which is the
  point of the change. The form keeps `AutoSize` + `AutoSizeMode.GrowAndShrink`, so it still sizes
  itself to the tab control, the error label and the button row.

The conditional sections (`_desktopSource` gates the Claude Desktop group and the pace caption) are
built before measuring, so a dialog opened against the desktop source sizes to its own content.

## Focus order

`TabIndex` is compared only among siblings, so the current flat run across the whole form becomes one
ascending run **per container** in reading order — not merely per page. The distinction matters on
the Status page, where the usage checkbox and its level combo sit in a nested `FlowLayoutPanel`: a
single page-wide run would number those two below the status checkboxes drawn beneath them and reach
them last. The run covers the panel, and the panel's own children get a run of their own.

The outer layout panel needs no run: `_tabs`, the error label and the button row take ascending
indices from the order they are added, which is the order they must be reached. Reset/Cancel/Save
keep their order inside the button row, and `AcceptButton`/`CancelButton` are unchanged, so Enter and
Esc still work from any page.

Because indices alone cannot express this, the test walks the real traversal with
`Control.GetNextControl` rather than comparing numbers.

## What does not change

- No `Core/` change, no new or renamed settings key, no change to what any control does. This is a
  layout move: the section-building code goes into four `BuildXxxPage()` methods essentially
  verbatim, keeping the bold `Heading()` labels and the 16 px `Indent()` so alignment is identical
  across pages.
- Every control keeps its `Name`. The existing tests reach controls with
  `Controls.Find(name, searchAllChildren: true)`, which descends into `TabPage`s, so every assertion
  still holds.

  One exception, found by review and confirmed in running code: `Button.PerformClick()` is a silent
  no-op on a control that cannot take focus, which a control on a hidden page cannot. Property reads
  and writes are unaffected, so this touches exactly one file — `SettingsDialogUpdateTests` clicks
  `checkUpdates` and `updateNow`, which move to the About page. Its `Dialog()` helper selects that
  page after `Show()`. Nothing else in the existing tests changes.
- The dialog stays modeless, still edits a clone, and `LoadFrom` / `WireLiveSync` / `Commit` are
  untouched.

## Testing

Extend `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`:

- The four pages exist, found by name on the constructed dialog.
- A representative control of each page sits on that page (`modeFive` → general, `orange` →
  appearance, `watchClaude` and `notifyUsage` → status, `betaReleases` → about) — this is what
  would catch a control silently landing on the wrong page during the move.
- The Claude Desktop group appears on the general page only when the desktop source is active.
- The tab control's height is smaller than the sum of the four pages' preferred heights — a coarse
  but stable assertion that the sections are no longer stacked, with no pixel constants in the test.
  Measured on the tab control rather than the form, whose chrome, padding and button row are constant
  overhead unrelated to the stacking.
- Every page fits its tab control without clipping, and the tab strip fits without scroll arrows.
- Focus reaches each page's controls in reading order, walked with `GetNextControl`.

Plus a throwaway `DrawToBitmap` probe per `CLAUDE.md` ("Verifying UI drawing without a human") to
look at each page once, deleted afterwards.
