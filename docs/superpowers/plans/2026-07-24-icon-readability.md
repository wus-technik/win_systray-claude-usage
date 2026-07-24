# v0.2 Filled-Badge Icons Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the too-fine progress-ring tray icons with filled-badge icons that are legible at 16 px on dark and light taskbars (spec: `docs/superpowers/specs/2026-07-24-icon-readability-design.md`, fixes issue #2).

**Architecture:** Only the drawing internals of `IconRenderer.Draw` change — public API, callers, HICON lifetime, and clamping stay identical. Ride-along updates keep README and the HTML design preview truthful, and bump the version for the `v0.2.0` release.

**Tech Stack:** C# / .NET 10 WinForms, GDI+ (`System.Drawing`), xUnit; plain canvas JS in the HTML preview.

## Global Constraints

- Severity colors unchanged: Green (64, 184, 96), Orange (232, 150, 40), Red (224, 68, 68); neutral grey (150, 150, 150).
- Badge geometry (from the spec, exact): base disc = ellipse inset 0.5 px, severity color at alpha 90 (alpha 50 when dimmed); usage wedge = solid `FillPie` from −90°, sweep `360 × clamp(percent, 0, 100) / 100`, negative sweep when counter-clockwise; rim = 1 px ellipse in the solid color; digit = Segoe UI Bold `0.60 × size` px, white (alpha 160 when dimmed), over a black halo drawn at the 8 ±1 px offsets (alpha 140, or 90 when dimmed).
- Dimmed severity color: alpha 120 applied before drawing (existing pattern).
- Public API signatures unchanged: `Render(char digit, int percent, Severity severity, bool clockwise, bool dimmed, int size)`, `RenderNeutral(int size)`, `SystemTrayIconSize()`.
- `tests/ClaudeUsageTray.Tests/IconRendererTests.cs` must pass **unmodified** — do not edit the test file.
- Before every task, inspect `git status --short`. Commit only task-owned paths (never `git add -A`) and never add AI co-author trailers.

## File Structure

```
src/ClaudeUsageTray/Tray/IconRenderer.cs      (Task 1: badge drawing internals)
src/ClaudeUsageTray/ClaudeUsageTray.csproj    (Task 2: version 0.2.0)
README.md                                     (Task 2: icon wording)
docs/icon-preview.html                        (Task 2: badge preview geometry)
```

---

### Task 1: IconRenderer filled-badge rewrite

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/IconRenderer.cs` (full replacement below)
- Test: `tests/ClaudeUsageTray.Tests/IconRendererTests.cs` (existing file — run only, do NOT edit)

**Interfaces:**
- Consumes: `Severity` from `ClaudeUsageTray.Core` (unchanged).
- Produces: same three public members as before with identical signatures; only rendering output differs. `TrayApp` needs no changes.

- [ ] **Step 1: Run the existing icon tests to confirm the green baseline**

Run: `dotnet test --filter IconRendererTests`
Expected: PASS (10 tests). These same tests are the regression net for the rewrite; they are geometry-agnostic (size, non-blankness, clamp-no-throw, min-16 metric).

- [ ] **Step 2: Replace `src/ClaudeUsageTray/Tray/IconRenderer.cs` entirely**

```csharp
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Tray;

public static class IconRenderer
{
    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>System small-icon size (per-monitor DPI aware via PerMonitorV2), floor 16 px.</summary>
    public static int SystemTrayIconSize() => Math.Max(16, GetSystemMetrics(SM_CXSMICON));

    /// <summary>
    /// Filled-badge icon: translucent severity-tinted disc, solid pie wedge from 12 o'clock
    /// for usage (clamped 0–100), 1 px rim, centered digit with a dark halo so it reads on
    /// dark AND light taskbars. clockwise=true for the 5h window, false (counter-clockwise)
    /// for 7d. dimmed = stale data.
    /// </summary>
    public static Icon Render(char digit, int percent, Severity severity, bool clockwise, bool dimmed, int size)
    {
        var color = severity switch
        {
            Severity.Red => Color.FromArgb(224, 68, 68),
            Severity.Orange => Color.FromArgb(232, 150, 40),
            _ => Color.FromArgb(64, 184, 96),
        };
        return Draw(digit.ToString(), Math.Clamp(percent, 0, 100), color, clockwise, dimmed, size);
    }

    /// <summary>Grey badge with a centered em-dash: the "no usage data yet" state.</summary>
    public static Icon RenderNeutral(int size)
        => Draw("—", percent: 0, Color.FromArgb(150, 150, 150), clockwise: true, dimmed: false, size);

    private static Icon Draw(string glyph, int percent, Color color, bool clockwise, bool dimmed, int size)
    {
        if (dimmed) color = Color.FromArgb(120, color);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var rect = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);

            // Translucent tinted disc marks the unused fraction.
            using (var back = new SolidBrush(Color.FromArgb(dimmed ? 50 : 90, color)))
                g.FillEllipse(back, rect);

            // Solid usage wedge from 12 o'clock (-90°); counter-clockwise = negative sweep (7d).
            if (percent > 0)
            {
                float sweep = 360f * percent / 100f;
                if (!clockwise) sweep = -sweep;
                using var fill = new SolidBrush(color);
                g.FillPie(fill, rect.X, rect.Y, rect.Width, rect.Height, -90f, sweep);
            }

            using (var rim = new Pen(color, 1f))
                g.DrawEllipse(rim, rect);

            // Digit over a 1 px dark halo: readable on the filled and unfilled halves
            // and on light taskbars (the v0.1 ring left the center transparent).
            var textColor = dimmed ? Color.FromArgb(160, Color.White) : Color.White;
            using var font = new Font("Segoe UI", size * 0.60f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
                { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var halo = new SolidBrush(Color.FromArgb(dimmed ? 90 : 140, 0, 0, 0)))
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0)
                            g.DrawString(glyph, font, halo, new RectangleF(dx, dy, size, size), format);
            }
            using (var brush = new SolidBrush(textColor))
                g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var native = Icon.FromHandle(hIcon);
            return (Icon)native.Clone(); // clone so the icon outlives the HICON we destroy
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
```

- [ ] **Step 3: Run the icon tests again — they must pass unmodified**

Run: `dotnet test --filter IconRendererTests`
Expected: PASS (10 tests), zero edits to the test file.

- [ ] **Step 4: Run the full suite (regression)**

Run: `dotnet test`
Expected: PASS (73 tests), build warning-free.

- [ ] **Step 5: Commit**

```powershell
git add src/ClaudeUsageTray/Tray/IconRenderer.cs
git commit -m "feat: filled-badge tray icons readable at 16px on dark and light taskbars"
```

---

### Task 2: Docs, preview, and version bump

**Files:**
- Modify: `README.md` (icon description)
- Modify: `docs/icon-preview.html` (badge geometry in the canvas renderer + wording)
- Modify: `src/ClaudeUsageTray/ClaudeUsageTray.csproj` (`<Version>` → `0.2.0`)

**Interfaces:**
- Consumes: the badge design from Task 1 (visual parity only — no code dependency).
- Produces: docs consistent with the shipped renderer; version ready for the `v0.2.0` tag.

- [ ] **Step 1: Update the README icon wording (both occurrences)**

In `README.md`:

a) In the opening paragraph, replace

```markdown
as progress-ring icons next to the clock. Passive reader of the cache Claude Code
```

with

```markdown
as filled-badge icons next to the clock. Passive reader of the cache Claude Code
```

b) In the Usage section, replace the line

```markdown
- **Icons:** ring fill = usage, color = severity (green < 50 %, orange 50–85 %,
```

with

```markdown
- **Icons:** badge fill = usage, color = severity (green < 50 %, orange 50–85 %,
```

c) Verify no ring wording remains: `grep -in "ring" README.md` must return no icon-design matches (the grey `—` no-data line is already correct — the em-dash glyph is unchanged from v0.1).

- [ ] **Step 2: Update `docs/icon-preview.html` to the badge design**

a) Replace the `arcIcon` function (the whole `function arcIcon(letter, pct, dir, scale, opts) { ... }` block) with:

```javascript
  // Filled-badge icon, single-digit center. dir 'cw' for 5, 'ccw' for 7.
  function badgeIcon(letter, pct, dir, scale, opts) {
    opts = opts || {};
    const size = 16, S = scale || 1, px = size * S;
    const c = document.createElement('canvas');
    c.width = px*dpr; c.height = px*dpr; c.style.width = px+'px'; c.style.height = px+'px';
    const ctx = c.getContext('2d'); ctx.scale(dpr, dpr);
    const C = themeColors();
    const cx = px/2, cy = px/2;
    const color = opts.color || severity(pct, C);
    // Explicit per-element alphas — MUST mirror IconRenderer.cs exactly:
    // disc 90 (dim 50), wedge/rim 255 (dim 120), digit 255 (dim 160), halo 140 (dim 90).
    const A = opts.dim
      ? { disc: 50/255, solid: 120/255, digit: 160/255, halo: 90/255 }
      : { disc: 90/255, solid: 1,       digit: 1,       halo: 140/255 };
    const rad = (px/2) - 0.5*S;

    // Translucent base disc (unused fraction).
    ctx.globalAlpha = A.disc; ctx.fillStyle = color;
    ctx.beginPath(); ctx.arc(cx, cy, rad, 0, Math.PI*2); ctx.fill();

    // Solid usage wedge from 12 o'clock.
    if (pct > 0) {
      ctx.globalAlpha = A.solid;
      const start = -Math.PI/2;
      const sweep = Math.min(pct,100)/100 * Math.PI*2;
      ctx.beginPath(); ctx.moveTo(cx, cy);
      if (dir === 'ccw') ctx.arc(cx, cy, rad, start, start - sweep, true);
      else ctx.arc(cx, cy, rad, start, start + sweep);
      ctx.closePath(); ctx.fill();
    }

    // 1 px rim.
    ctx.globalAlpha = A.solid; ctx.lineWidth = 1*S; ctx.strokeStyle = color;
    ctx.beginPath(); ctx.arc(cx, cy, rad, 0, Math.PI*2); ctx.stroke();

    // White digit over a dark halo — reads on both halves and on light taskbars.
    ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.font = '700 ' + (9.6*S) + 'px "Segoe UI", system-ui, sans-serif';
    ctx.globalAlpha = A.halo; ctx.fillStyle = '#000';
    for (let dx = -1; dx <= 1; dx++)
      for (let dy = -1; dy <= 1; dy++)
        if (dx || dy) ctx.fillText(letter, cx + dx*S, cy + 0.5*S + dy*S);
    ctx.globalAlpha = A.digit; ctx.fillStyle = opts.labelColor || '#fff';
    ctx.fillText(letter, cx, cy + 0.5*S);
    return c;
  }
```

b) Replace every remaining call `arcIcon(` with `badgeIcon(` (six call sites: tray group ×2, ramp, pair, states `mk`).

c) Wording updates:
- `<title>`: `Claude Usage Tray — Badge Icons (5 / 7)` (keep the title concept stable otherwise).
- `.eyebrow`: `Claude Usage Tray · filled-badge concept, single-digit`
- `<h1>`: `Badge icons with <code>5</code> / <code>7</code> inside`
- In the `.lede` paragraph, replace `The ring still <strong>fills with usage</strong>` with `The badge still <strong>fills with usage</strong>`.
- In the "Fresh window" state (`mk('7',0,'ccw','Fresh window','0% · empty ring');`), change the sub-label text `'0% · empty ring'` to `'0% · empty badge'`.

d) Open the file in a browser (or re-render mentally) — badges must show: translucent disc + solid wedge + white halo'd digit. This is a sanity look, not a gate; the binding visual check happens against the production renderer.

- [ ] **Step 3: Bump the version**

In `src/ClaudeUsageTray/ClaudeUsageTray.csproj`, change:

```xml
    <Version>0.1.0</Version>
```

to

```xml
    <Version>0.2.0</Version>
```

- [ ] **Step 4: Build and full test suite (regression)**

Run: `dotnet test`
Expected: PASS (73 tests), build warning-free.

- [ ] **Step 5: Commit**

```powershell
git add README.md docs/icon-preview.html src/ClaudeUsageTray/ClaudeUsageTray.csproj
git commit -m "docs: badge icon preview and wording; bump version to 0.2.0"
```

---

## Acceptance (from the spec)

| Requirement | Task |
|---|---|
| Badge geometry (disc alpha 90/50, wedge, 1 px rim, digit 0.60 + halo 140/90, dim 120/160) | 1 |
| API and callers unchanged; HICON pattern unchanged | 1 |
| Existing IconRendererTests pass unmodified | 1 |
| README + icon-preview.html match shipped renderer | 2 |
| `<Version>` 0.2.0 | 2 |
| 16 px legibility on dark AND light swatches (human visual check on a regenerated comparison render) | post-plan verification by controller/human |
