# Tabbed settings layout — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single vertical stack of sections in `SettingsDialog` with a four-page
`TabControl`, so the window is as tall as its tallest section group rather than the sum of all of
them.

**Architecture:** `BuildLayout()` keeps its outer one-column `TableLayoutPanel` but holds three
rows: a `TabControl`, the error label, and the button row. The existing section code moves verbatim
into four `BuildXxxPage()` methods, one per `TabPage` (`general`, `appearance`, `status`, `about`).
The `TabControl` does not auto-size, so the form measures all four page panels once the handle
exists and sizes the control to the largest — fixed, so switching tabs never resizes the window.

**Tech Stack:** .NET 10, WinForms, xUnit. Target framework `net10.0-windows10.0.19041.0`.

**Spec:** `docs/superpowers/specs/2026-09-11-settings-tabbed-layout-design.md`

## Global Constraints

- Only `src/ClaudeUsageTray/Tray/SettingsDialog.cs` and
  `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs` change. No `Core/` change, no new or renamed
  settings key, no change to what any control does.
- Every control keeps its existing `Name`. `Controls.Find(name, searchAllChildren: true)` descends
  into `TabPage`s, so `SettingsDialogUpdateTests` and the rest of `SettingsDialogTests` must pass
  **untouched**. If a change would require editing them, the change is wrong.
- Keep the bold `Heading()` labels and the 16 px `Indent()` on every page, so alignment is identical
  across tabs and the diff stays a move rather than a restyle.
- The dialog stays modeless; `LoadFrom`, `WireLiveSync`, `Commit`, `ResetThresholdsToDefaults` and
  `RefreshUpdateSection` are not touched. `AcceptButton`/`CancelButton` stay wired to Save/Cancel.
- Tab page order and names: `general` "General", `appearance` "Appearance", `status` "Status",
  `about` "About".
- `dotnet test` output on this machine is **German**: a run succeeded when it prints `Bestanden!`;
  failures print `Fehler:`. Do not grep for `Passed!`.
- Match surrounding style: there is no formatter. Comments explain *why*, in the voice of the
  existing file.

---

### Task 1: Four tab pages replacing the stack

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/SettingsDialog.cs` — `BuildLayout()` (currently lines 167–256)
- Test: `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: a field `private readonly TabControl _tabs = new() { Name = "tabs" };`; the private
  methods `Control BuildGeneralPage()`, `Control BuildAppearancePage()`, `Control BuildStatusPage()`,
  `Control BuildAboutPage()` (each returns the page's `TableLayoutPanel`), and
  `static TabPage Page(string name, string text, Control content)`. Task 2 reads `_tabs` and each
  page's `Controls[0]`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs` (inside the class, above the private
helpers at the bottom). Add `using System.Linq;` and `using System.Windows.Forms;` only if the file
does not already have them — it already has `System.Windows.Forms` and uses `.Single()`.

```csharp
    [Fact]
    public void TheSettingsAreSplitAcrossFourTabs()
    {
        var tabs = Tabs(Dialog(new Settings()));

        Assert.Equal(
            new[] { "general", "appearance", "status", "about" },
            tabs.TabPages.Cast<TabPage>().Select(page => page.Name));
        Assert.Equal(
            new[] { "General", "Appearance", "Status", "About" },
            tabs.TabPages.Cast<TabPage>().Select(page => page.Text));
    }

    /// <summary>One representative control per group, which is what catches a control landing on the
    /// wrong page while the sections are moved.</summary>
    [Theory]
    [InlineData("general", "modeFive")]
    [InlineData("general", "startup")]
    [InlineData("general", "staleness")]
    [InlineData("general", "desktopStaleness")]
    [InlineData("appearance", "orange")]
    [InlineData("appearance", "red")]
    [InlineData("appearance", "paceColors")]
    [InlineData("appearance", "preview")]
    [InlineData("status", "watchClaude")]
    [InlineData("status", "claudeComponents")]
    [InlineData("status", "watchOpenAi")]
    [InlineData("status", "openAiComponents")]
    [InlineData("status", "notifyUsage")]
    [InlineData("status", "notifyLevel")]
    [InlineData("status", "notifyClaude")]
    [InlineData("status", "notifyOpenAi")]
    [InlineData("about", "creator")]
    [InlineData("about", "installedVersion")]
    [InlineData("about", "updateStatus")]
    [InlineData("about", "checkUpdates")]
    [InlineData("about", "updateNow")]
    [InlineData("about", "betaReleases")]
    public void EverySettingSitsOnItsOwnTab(string page, string control)
    {
        Assert.Equal(page, PageOf(Dialog(new Settings()), control));
    }

    [Fact]
    public void TheErrorAndTheButtonsStayOutsideTheTabs()
    {
        // Save must be reachable from every page, and a failed save has to be readable from the page
        // the user was on when they pressed it.
        var dialog = Dialog(new Settings());

        Assert.Null(PageOf(dialog, "error"));
        Assert.Null(PageOf(dialog, "save"));
        Assert.Null(PageOf(dialog, "cancel"));
        Assert.Null(PageOf(dialog, "reset"));
    }

    [Fact]
    public void TheWeeklyAnchorAppearsOnGeneralOnlyForTheDesktopSource()
    {
        Assert.Empty(Dialog(new Settings()).Controls.Find("weeklyAnchor", searchAllChildren: true));
        Assert.Equal("general", PageOf(Dialog(new Settings(), desktopSource: true), "weeklyAnchor"));
    }
```

And these two private helpers next to the existing `Spinner` / `Button` helpers:

```csharp
    private static TabControl Tabs(SettingsDialog dialog)
        => (TabControl)dialog.Controls.Find("tabs", searchAllChildren: true).Single();

    /// <summary>The name of the tab page a control sits on, or null when it is outside the tabs.</summary>
    private static string? PageOf(SettingsDialog dialog, string control)
    {
        var found = dialog.Controls.Find(control, searchAllChildren: true).Single();
        for (var parent = found.Parent; parent is not null; parent = parent.Parent)
            if (parent is TabPage page) return page.Name;
        return null;
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SettingsDialogTests"`
Expected: `Fehler:` — `TheSettingsAreSplitAcrossFourTabs` and `EverySettingSitsOnItsOwnTab` fail
because no control is named `tabs` (`Single()` throws on the empty match) and `PageOf` returns null
for every control.

- [ ] **Step 3: Add the tab control field**

In `SettingsDialog.cs`, next to the other control fields (after `_notifyOpenAi`, before the
`LevelLabels` array):

```csharp
    private readonly TabControl _tabs = new() { Name = "tabs", Margin = new Padding(0, 0, 0, 4) };
```

- [ ] **Step 4: Replace BuildLayout with the tab shell**

Replace the whole existing `BuildLayout()` body with this, and add the `BuildTabs` / `Page` helpers
directly below it:

```csharp
    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };

        // The error label and the buttons stay outside the tabs: Save has to be reachable from every
        // page, and a failed save has to be readable from whichever page the user was on.
        layout.Controls.Add(BuildTabs());
        layout.Controls.Add(_error);
        layout.Controls.Add(BuildButtons());
        return layout;
    }

    /// <summary>Four pages grouped by what a user changes together, not by the order the sections
    /// were written in. Notifications sits with Platform status because two of its three controls are
    /// per-source status toggles; the beta ring sits with About because it steers the updater
    /// directly above it.</summary>
    private Control BuildTabs()
    {
        _tabs.TabPages.Add(Page("general", "General", BuildGeneralPage()));
        _tabs.TabPages.Add(Page("appearance", "Appearance", BuildAppearancePage()));
        _tabs.TabPages.Add(Page("status", "Status", BuildStatusPage()));
        _tabs.TabPages.Add(Page("about", "About", BuildAboutPage()));
        return _tabs;
    }

    private static TabPage Page(string name, string text, Control content)
    {
        content.Dock = DockStyle.Fill;
        var page = new TabPage(text)
        {
            Name = name,
            Padding = new Padding(8),
            UseVisualStyleBackColor = true,
        };
        page.Controls.Add(content);
        return page;
    }

    /// <summary>An empty page body, sized to its content. The pages differ only in what goes in.</summary>
    private static TableLayoutPanel PagePanel() => new()
    {
        ColumnCount = 1,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };
```

- [ ] **Step 5: Move the sections into the four page builders**

Add these below `PagePanel()`. Every line of section code is the old `BuildLayout()` body, moved —
do not reword a caption or drop a heading.

```csharp
    private Control BuildGeneralPage()
    {
        var page = PagePanel();

        page.Controls.Add(Heading("Tray icons"));
        page.Controls.Add(Indent(_modeFive));
        page.Controls.Add(Indent(_modeSeven));
        page.Controls.Add(Indent(_modeBoth));

        _startup.Enabled = _canRunAtStartup;
        page.Controls.Add(Indent(_startup));
        if (!_canRunAtStartup)
        {
            page.Controls.Add(Indent(new Label
            {
                Text = "Available only in the installed app.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
            }));
        }

        page.Controls.Add(Heading("Refresh"));
        page.Controls.Add(Spinners(
            ("Treat data as stale after", _staleness, "minutes"),
            ("Claude Desktop history stale after", _desktopStaleness, "hours")));

        // Only while the desktop history is the live source: a Claude Code user puzzling over a
        // setting that does nothing for them is the far more common outcome than the reverse.
        if (_desktopSource)
        {
            page.Controls.Add(Heading("Claude Desktop"));
            page.Controls.Add(Indent(new Label
            {
                Text = "Weekly reset (read it off Claude's own UI), e.g. Thu 03:00",
                AutoSize = true,
            }));
            page.Controls.Add(Indent(_weeklyAnchor));
            page.Controls.Add(Indent(_weeklyAnchorError));
        }

        return page;
    }

    private Control BuildAppearancePage()
    {
        var page = PagePanel();

        page.Controls.Add(Heading("Colour thresholds"));
        page.Controls.Add(Spinners(("Orange at", _orange, "%"), ("Red above", _red, "%")));
        page.Controls.Add(Indent(_paceColors));
        if (_desktopSource)
        {
            page.Controls.Add(Indent(new Label
            {
                Text = "Needs a reset time; without one the plain thresholds decide.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
            }));
        }
        page.Controls.Add(Indent(_preview));
        page.Controls.Add(Indent(_previewCaption));

        return page;
    }

    private Control BuildStatusPage()
    {
        var page = PagePanel();

        // Both pages, each with its own watch filter. The notify checkboxes stay under
        // Notifications, where the two of them already sit together.
        page.Controls.Add(Heading("Platform status"));
        page.Controls.Add(Indent(_watchClaude));
        page.Controls.Add(Indent(_claudeComponentsCaption));
        page.Controls.Add(Indent(_claudeComponents));
        page.Controls.Add(Indent(_claudeComponentsHint));
        page.Controls.Add(Indent(_watchOpenAi));
        page.Controls.Add(Indent(_openAiComponentsCaption));
        page.Controls.Add(Indent(_openAiComponents));
        page.Controls.Add(Indent(_openAiComponentsHint));

        page.Controls.Add(Heading("Notifications"));
        _notifyLevel.Items.AddRange(LevelLabels);
        var usageRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(16, 0, 0, 2) };
        _notifyUsage.Margin = new Padding(0, 4, 4, 0);
        _notifyLevel.Margin = new Padding(0);
        usageRow.Controls.Add(_notifyUsage);
        usageRow.Controls.Add(_notifyLevel);
        page.Controls.Add(usageRow);
        page.Controls.Add(Indent(_notifyClaude));
        page.Controls.Add(Indent(_notifyOpenAi));

        return page;
    }

    private Control BuildAboutPage()
    {
        var page = PagePanel();

        page.Controls.Add(Heading("About"));
        page.Controls.Add(BuildAbout());

        // Which ring the updater follows belongs next to the update controls it changes. Disabled
        // outside the installed app for the same reason those are: there is nothing to update.
        _betaReleases.Enabled = _updates.IsInstalled;
        page.Controls.Add(Indent(_betaReleases));

        return page;
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SettingsDialog"`
Expected: `Bestanden!` — both `SettingsDialogTests` and `SettingsDialogUpdateTests`, the latter with
no edits. If an old test fails, a control lost its `Name` or was dropped in the move; fix the move,
not the test.

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Tray/SettingsDialog.cs tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs
git commit -m "$(cat <<'EOF'
feat(settings): group the sections into four tabs

General, Appearance, Status and About, each a TabPage over the section
code moved verbatim. The error label and the button row stay outside the
tabs so Save is reachable from every page.

Claude-Session: https://claude.ai/code/session_01DxfvXPU5nsa33PcSFcKBN9
EOF
)"
```

---

### Task 2: Size the tab control to the largest page

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/SettingsDialog.cs`
- Test: `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`

**Interfaces:**
- Consumes: `_tabs` and the four pages from Task 1; each page's body panel is `page.Controls[0]`.
- Produces: `protected override void OnHandleCreated(EventArgs e)` and
  `private void FitTabsToLargestPage()`. Nothing later depends on them.

Why `OnHandleCreated` and not the constructor: `TabControl.DisplayRectangle` — the inset the tab
strip and borders cost — is only meaningful once the handle exists. Both test helpers call `Show()`,
and `TrayApp` shows the dialog, so the hook always runs.

- [ ] **Step 1: Write the failing tests**

Append to `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`:

```csharp
    [Fact]
    public void TheWindowIsShorterThanTheSectionsStacked()
    {
        // The whole point of the tabs: the height comes from the tallest page, not from the sum.
        var dialog = Dialog(new Settings());
        var stacked = Tabs(dialog).TabPages.Cast<TabPage>()
            .Sum(page => page.Controls[0].PreferredSize.Height);

        Assert.True(dialog.PreferredSize.Height < stacked,
            $"form {dialog.PreferredSize.Height} px, sections stacked {stacked} px");
    }

    [Fact]
    public void EveryPageFitsWithoutScrolling()
    {
        var dialog = Dialog(new Settings());
        var tabs = Tabs(dialog);

        foreach (TabPage page in tabs.TabPages)
        {
            var needed = page.Controls[0].PreferredSize;
            Assert.True(needed.Height + page.Padding.Vertical <= tabs.DisplayRectangle.Height,
                $"{page.Name} needs {needed.Height} px, has {tabs.DisplayRectangle.Height}");
            Assert.True(needed.Width + page.Padding.Horizontal <= tabs.DisplayRectangle.Width,
                $"{page.Name} needs {needed.Width} px, has {tabs.DisplayRectangle.Width}");
        }
    }

    [Fact]
    public void SwitchingTabsDoesNotResizeTheWindow()
    {
        // A dialog that jumps under the pointer is worse than a page with slack at the bottom.
        var dialog = Dialog(new Settings());
        var tabs = Tabs(dialog);
        var size = dialog.Size;

        foreach (TabPage page in tabs.TabPages)
        {
            tabs.SelectedTab = page;
            Assert.Equal(size, dialog.Size);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SettingsDialogTests.EveryPageFitsWithoutScrolling"`
Expected: `Fehler:` — the `TabControl` is still its default 200×100, so every page overflows it.

- [ ] **Step 3: Implement the sizing**

Add to `SettingsDialog.cs`, below `BuildTabs()`:

```csharp
    /// <summary>A TabControl never sizes itself to its pages, so the form would otherwise inherit the
    /// designer default. Measured once the handle exists, because DisplayRectangle — the inset the tab
    /// strip and borders cost — is only meaningful then.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FitTabsToLargestPage();
    }

    /// <summary>Fixed at the largest page, not re-measured per tab: the height then comes from the
    /// tallest group rather than the sum of all of them, and switching tabs never resizes the window.</summary>
    private void FitTabsToLargestPage()
    {
        var content = Size.Empty;
        foreach (TabPage page in _tabs.TabPages)
        {
            var needed = page.Controls[0].PreferredSize;
            content = new Size(
                Math.Max(content.Width, needed.Width + page.Padding.Horizontal),
                Math.Max(content.Height, needed.Height + page.Padding.Vertical));
        }

        _tabs.Size = content + (_tabs.Size - _tabs.DisplayRectangle.Size);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SettingsDialogTests"`
Expected: `Bestanden!`

If `EveryPageFitsWithoutScrolling` is short by a few pixels, the frame is being under-measured —
widen it in `FitTabsToLargestPage` by reading `_tabs.DisplayRectangle` *after* the assignment and
adding the shortfall once; do not paper over it by relaxing the assertion.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Tray/SettingsDialog.cs tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs
git commit -m "$(cat <<'EOF'
feat(settings): size the tab control to the largest page

Measured on handle creation, when DisplayRectangle is meaningful. Fixed
rather than per-tab, so the window height follows the tallest group and
switching tabs never resizes it.

Claude-Session: https://claude.ai/code/session_01DxfvXPU5nsa33PcSFcKBN9
EOF
)"
```

---

### Task 3: Per-page focus order

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/SettingsDialog.cs` — the `TabIndex` loop at the end of
  `BuildButtons()` (currently lines 441–448)
- Test: `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`

**Interfaces:**
- Consumes: the page builders from Task 1.
- Produces: `private static void SetOrder(params Control[] controls)`.

Why: `TabIndex` is compared only among siblings in the same container, so the current single flat
run across the whole form no longer describes the tree. Each page gets its own ascending run in
reading order; controls nested in a `Spinners` grid or the notify `FlowLayoutPanel` still ascend
relative to each other, which is all the traversal needs.

- [ ] **Step 1: Write the failing test**

Append to `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`:

```csharp
    /// <summary>Tab has to walk each page in reading order, so the dialog never needs the mouse.</summary>
    [Theory]
    [InlineData(new[] { "modeFive", "modeSeven", "modeBoth", "startup" })]
    [InlineData(new[] { "staleness", "desktopStaleness" })]
    [InlineData(new[] { "orange", "red" })]
    [InlineData(new[] { "watchClaude", "claudeComponents", "watchOpenAi", "openAiComponents" })]
    [InlineData(new[] { "notifyUsage", "notifyLevel" })]
    [InlineData(new[] { "reset", "cancel", "save" })]
    public void FocusRunsInReadingOrder(string[] names)
    {
        var dialog = Dialog(new Settings());
        var indices = names
            .Select(name => dialog.Controls.Find(name, searchAllChildren: true).Single().TabIndex)
            .ToArray();

        Assert.Equal(indices.OrderBy(index => index), indices);
        Assert.Equal(indices.Distinct().Count(), indices.Length);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~SettingsDialogTests.FocusRunsInReadingOrder"`
Expected: `Fehler:` on the `reset/cancel/save` case — the existing flat run gives them 19, 20, 21
while `orange`/`red` keep 4, 5 inside their own grid, so the runs collide across containers once
they no longer share a parent. (Some cases may pass already; the task is done when all six do and
the run is per page.)

- [ ] **Step 3: Replace the flat run with per-page runs**

In `BuildButtons()`, delete the whole trailing block:

```csharp
        // Tab reaches the controls in reading order, then the buttons.
        int order = 0;
        foreach (var control in new Control[]
                 { _modeFive, _modeSeven, _modeBoth, _startup, _orange, _red, _paceColors, _staleness,
                   _desktopStaleness, _weeklyAnchor, _betaReleases, _watchClaude, _claudeComponents,
                   _watchOpenAi, _openAiComponents, _notifyUsage, _notifyLevel, _notifyClaude,
                   _notifyOpenAi, reset, cancel, save })
            control.TabIndex = order++;
        return row;
```

and replace it with:

```csharp
        SetOrder(reset, cancel, save);
        return row;
```

Add the helper next to `Indent()`:

```csharp
    /// <summary>An ascending run in reading order. TabIndex is only ever compared among siblings, so
    /// every page — and every nested row within one — gets its own run rather than one run across the
    /// whole form.</summary>
    private static void SetOrder(params Control[] controls)
    {
        for (int index = 0; index < controls.Length; index++) controls[index].TabIndex = index;
    }
```

Then add one call at the end of each page builder, before its `return page;`:

```csharp
        // BuildGeneralPage
        SetOrder(_modeFive, _modeSeven, _modeBoth, _startup, _staleness, _desktopStaleness, _weeklyAnchor);

        // BuildAppearancePage
        SetOrder(_orange, _red, _paceColors);

        // BuildStatusPage
        SetOrder(_watchClaude, _claudeComponents, _watchOpenAi, _openAiComponents,
            _notifyUsage, _notifyLevel, _notifyClaude, _notifyOpenAi);

        // BuildAboutPage
        SetOrder(_creator, _checkUpdates, _updateNow, _betaReleases);
```

`_weeklyAnchor` is passed on the general page whether or not it was added — setting `TabIndex` on a
control with no parent is harmless, and it keeps the call free of a conditional.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SettingsDialog"`
Expected: `Bestanden!`

- [ ] **Step 5: Walk the dialog with the keyboard, offscreen**

Confirm the pages look right, per `CLAUDE.md` → "Verifying UI drawing without a human". Create
`tests/ClaudeUsageTray.Tests/PageProbe.cs` as a **throwaway**:

```csharp
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class PageProbe
{
    [Fact]
    public void Capture()
    {
        using var dialog = new SettingsDialog(new Settings(), canRunAtStartup: true, runAtStartup: false,
            _ => true, TestUpdateOptions.Inert(), desktopSource: true,
            new Dictionary<string, IReadOnlyList<string>>());
        dialog.CreateControl(); // never Show(): OnDeactivate closes the form with no message loop
        var tabs = (TabControl)dialog.Controls.Find("tabs", searchAllChildren: true).Single();

        foreach (TabPage page in tabs.TabPages)
        {
            tabs.SelectedTab = page;
            using var shot = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(shot, new Rectangle(0, 0, dialog.Width, dialog.Height));
            using var big = new Bitmap(shot.Width * 2, shot.Height * 2);
            using (var g = Graphics.FromImage(big))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(shot, 0, 0, big.Width, big.Height);
            }
            big.Save(Path.Combine(Path.GetTempPath(), $"settings-{page.Name}.png"));
        }
    }
}
```

Run: `dotnet test --filter "FullyQualifiedName~PageProbe"`, then look at the four PNGs in `%TEMP%`.
Check: no clipped captions, the preview bar is fully visible, the component hint labels wrap inside
the page, and no page is wildly emptier than the tab control it sits in.

- [ ] **Step 6: Delete the probe**

```bash
rm tests/ClaudeUsageTray.Tests/PageProbe.cs
```

It is a verification artifact, not a test. It must not be committed.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test --configuration Release`
Expected: `Bestanden!` — this is what CI runs.

- [ ] **Step 8: Commit**

```bash
git add src/ClaudeUsageTray/Tray/SettingsDialog.cs tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs
git status --short   # must show no PageProbe.cs
git commit -m "$(cat <<'EOF'
feat(settings): give each tab its own focus order

TabIndex only orders siblings, so the single flat run across the form is
replaced by one ascending run per page and one for the button row.

Claude-Session: https://claude.ai/code/session_01DxfvXPU5nsa33PcSFcKBN9
EOF
)"
```
