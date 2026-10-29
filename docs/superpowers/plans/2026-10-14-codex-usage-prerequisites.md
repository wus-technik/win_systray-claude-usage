# Codex Usage Prerequisites Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the shared code a second usage provider can plug into, without adding any Codex code.

**Architecture:** Six changes to code Claude's paths already use. Each one generalises an assumption
that was correct while there was exactly one provider: notification rules that reach for Claude's
settings, a toast tag that is a constant, a scoped limit that is weekly by construction, a badge flag
that is absolute, a settings default that cannot stay undecided, an icon renderer that only draws
discs, and a popup with no height bound. Every task is behaviour-preserving for a Claude-only user
except where a test says otherwise.

**Tech Stack:** .NET 10, C# (primary constructors, records), WinForms, xUnit.

**Spec:** [docs/superpowers/specs/2026-10-13-codex-usage-design.md](../specs/2026-10-13-codex-usage-design.md)

Note on numbering: the spec's Prerequisites list has seven items. Items 1 and 2 (splitting
`NotificationRules`, and per-provider toast tags) land as one task here — they share a constructor
and a test file, and a reviewer cannot sensibly accept one without the other.

## Global Constraints

- App and tests target `net10.0-windows10.0.19041.0`. Nothing else does.
- `Core/` is WinForms-free, clock-free and thread-free. Every time-dependent function takes a
  caller-supplied `DateTimeOffset now`. I/O readers are allowed there and already live there.
- Nothing in a read path throws. IO/JSON errors return null or defaults.
- The token is read-only: never written, never refreshed, never logged.
- `fetch.log` carries percentages and outcomes only — never money amounts, currency, account
  identifiers, or account-specific model names.
- Absent data means no row. Never render a placeholder `0 %` or `—` for a limit the account lacks.
- Labels come from the payload, not from a hardcoded model list.
- No linter or formatter step. Match surrounding style, including the explanatory doc comments —
  this codebase documents *why* a rule looks odd, and that is load-bearing.
- `dotnet test` output on this machine is German: grep `Bestanden!` / `Fehler:`, not `Passed!`.
- Commit messages in English. Never add a Claude/AI co-author trailer.

## Review Focus

Five conditions the spec implies that no task's happy path exercises. Each has its test added to the
owning task.

1. A scoped limit whose `ResetsAt` is further out than its own period (a 31-day calendar month
   against a 30-day period) — pace must degrade to absolute-only, never a negative fraction. *Task 2.*
2. A `statusSources` entry that is present but has no `enabled` key at all — must stay undecided
   through a `Save`/`Load` round trip, not harden into `false`. *Task 4.*
3. A tooltip where both status sources are degraded and the combined text exceeds 127 characters —
   the suffix belonging to the *target* provider survives, the other is dropped whole. *Task 3.*
4. A rounded-square badge at size 16 with percent 100 — the wedge must not paint into the corners
   the silhouette excludes. *Task 5.*
5. A popup with more rows than the screen is tall — height clamped to the working area and
   scrollable, never drawn off-screen. *Task 6.*

---

### Task 1: Per-provider usage notification rules

`NotificationRules` is one `sealed partial class` over two files, holding both the usage state machine
and the per-source status state machine. It also reads `settings.UsageNotifications` internally
(lines 59, 124, 182) and stamps a constant tag (`"usage"`) that `TrayApp` also hardcodes when
retracting. A second instance for a second provider would therefore run on Claude's level, duplicate
every status toast, and retract Claude's toast.

The provider's notification settings arrive as a **selector**, not a value: the rules object is
long-lived and `Settings` is replaced when the user saves, so holding a `UsageNotificationSettings`
reference would go stale.

**Files:**
- Create: `src/ClaudeUsageTray/Core/Notification.cs`
- Create: `src/ClaudeUsageTray/Core/UsageNotificationRules.cs`
- Create: `src/ClaudeUsageTray/Core/StatusNotificationRules.cs`
- Delete: `src/ClaudeUsageTray/Core/NotificationRules.cs`
- Delete: `src/ClaudeUsageTray/Core/NotificationRules.Status.cs`
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs:47, 202, 267, 357, 359, 362`
- Test: `tests/ClaudeUsageTray.Tests/NotificationRulesUsageTests.cs` (17 construction sites)
- Test: `tests/ClaudeUsageTray.Tests/NotificationRulesStatusTests.cs` (14 construction sites)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `UsageNotificationRules(string tag, Func<Settings, UsageNotificationSettings> notifications)`
  - `UsageNotificationRules.ForClaude()` → `UsageNotificationRules`
  - `UsageNotificationRules.Tag` → `string`
  - `UsageNotificationRules.NoteLiveOutcome(LiveOutcome)` → `string?`
  - `UsageNotificationRules.OnUsage(DisplayChoice, Settings, DateTimeOffset)` → `NotificationOutcome`
  - `UsageNotificationRules.IsArmed` → `bool`
  - `StatusNotificationRules()` and `.OnStatus(IReadOnlyList<SourceView>, Settings)` → `IReadOnlyList<NotificationOutcome>`
  - `NotificationTags.OpenPopupArgument`, `NotificationTags.Usage(string provider)`, `NotificationTags.Status(string sourceId)`

- [x] **Step 1: Write the failing test**

Add to `tests/ClaudeUsageTray.Tests/NotificationRulesUsageTests.cs`:

```csharp
    // ---- per-provider wiring ----

    /// <summary>A rules object reads the UsageNotificationSettings its selector names, not
    /// Settings.UsageNotifications. Without this, a second provider silently runs on Claude's level.
    /// Claude's block is Orange-and-on here and the second provider's is Red-and-on, so the 85 %
    /// reading crosses for Claude and must not for the other.</summary>
    [Fact]
    public void OnUsage_ReadsTheProvidersOwnNotificationSettings()
    {
        var settings = new Settings
        {
            PaceColors = false,
            UsageNotifications = new UsageNotificationSettings { Enabled = true, Level = NotifyLevel.Orange },
            SecondaryNotificationsForTest = new UsageNotificationSettings { Enabled = true, Level = NotifyLevel.Red },
        };

        var other = new UsageNotificationRules("usage:other", s => s.SecondaryNotificationsForTest);
        other.NoteLiveOutcome(LiveOutcome.Snapshot);
        other.OnUsage(Fresh(Snap(10, T0)), settings, T0);

        var claude = UsageNotificationRules.ForClaude();
        claude.NoteLiveOutcome(LiveOutcome.Snapshot);
        claude.OnUsage(Fresh(Snap(10, T0)), settings, T0);

        var at85 = Fresh(Snap(85, T0));
        Assert.Null(other.OnUsage(at85, settings, T0).Notification);      // 85 % is orange, level is red
        Assert.NotNull(claude.OnUsage(at85, settings, T0).Notification);  // level is orange
    }

    /// <summary>Each provider owns an Action Center slot. One shared tag means a Codex toast replaces
    /// a Claude toast instead of sitting beside it, and a Codex retraction pulls Claude's out.</summary>
    [Fact]
    public void Tag_IsPerProvider()
    {
        Assert.Equal("usage:claude", UsageNotificationRules.ForClaude().Tag);
        Assert.NotEqual(UsageNotificationRules.ForClaude().Tag,
            new UsageNotificationRules("usage:other", s => s.UsageNotifications).Tag);
    }

    /// <summary>The emitted notification carries that same slot, so the retraction site and the
    /// emission site cannot drift apart.</summary>
    [Fact]
    public void EmittedNotification_CarriesTheProvidersTag()
    {
        var settings = Absolute();
        var rules = ArmedAt(10, settings);
        var outcome = rules.OnUsage(Fresh(Snap(95, T0)), settings, T0);
        Assert.Equal(rules.Tag, outcome.Notification!.Tag);
    }
```

The test needs a second `UsageNotificationSettings` on `Settings` to select. Add it to
`src/ClaudeUsageTray/Core/Settings.cs`, directly below `UsageNotifications`:

```csharp
    /// <summary>A second usage-notification block with no consumer yet. It exists so the
    /// per-provider selector in UsageNotificationRules is exercised by a test rather than only by
    /// the provider that arrives in the next plan. Not serialized: no settings key, no dialog
    /// control, no migration.</summary>
    [JsonIgnore]
    public UsageNotificationSettings SecondaryNotificationsForTest { get; set; } = new();
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~NotificationRulesUsageTests`
Expected: FAIL — `UsageNotificationRules` does not exist (CS0246), `ForClaude` undefined.

- [x] **Step 3: Create the shared notification types**

Create `src/ClaudeUsageTray/Core/Notification.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>How one live usage fetch concluded, as far as arming cares. Relayed by TrayApp from the
/// outcomes it already distinguishes; it never decides anything about them.</summary>
public enum LiveOutcome { Snapshot, Unauthorized, RateLimited, Failed, NoToken }

/// <summary>What to show. Argument is the toast's activation payload; Tag is the Action Center slot
/// (a newer toast of a kind replaces its predecessor); ExpiresAt is when Action Center should stop
/// asserting it, null for the platform default.</summary>
public sealed record Notification(string Title, string Body, string Argument, string Tag, DateTimeOffset? ExpiresAt);

/// <summary>One evaluation's result. Log carries one line per decision event — never per tick — so
/// "I never got a toast" can be answered from fetch.log. RemoveUsageToast is set when a notified
/// usage value left the level, so the presenter can retract a claim that is no longer true.</summary>
public sealed record NotificationOutcome(Notification? Notification, IReadOnlyList<string> Log, bool RemoveUsageToast);

/// <summary>The Action Center slots and the activation payload, in one place because both rule
/// halves and the retraction site in TrayApp have to agree on them.</summary>
public static class NotificationTags
{
    public const string OpenPopupArgument = "open-popup";

    /// <summary>One usage slot per provider. A shared slot would make a second provider's toast
    /// replace the first's rather than sit beside it, and its retraction would pull the first's out
    /// of Action Center. Changing Claude's slot from the old bare "usage" orphans at most one toast
    /// that was on screen across the upgrade — it can no longer be retracted programmatically, and
    /// expires on its own.</summary>
    public static string Usage(string provider) => "usage:" + provider;

    public static string Status(string sourceId) => "status:" + sourceId;
}
```

- [x] **Step 4: Create the usage half as its own type**

Create `src/ClaudeUsageTray/Core/UsageNotificationRules.cs` with the entire contents of the current
`NotificationRules.cs`, minus the three types now in `Notification.cs`, and with these changes:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>
/// Remembers what it last saw and turns one provider's readings into transitions. Stateful but
/// clock-free: the caller supplies now, so every rule below is a unit test. One instance per
/// provider — the provider's notification settings arrive through <paramref name="notifications"/>
/// rather than being read off Settings, because an instance that reached for
/// Settings.UsageNotifications would run every provider on Claude's level. A selector rather than a
/// value: Settings is replaced wholesale on save, so a captured reference would go stale.
///
/// Usage rules, in order: stale → nothing recorded; fingerprint change → clear; source switch →
/// clear; unarmed → record baseline; per key, notify on crossing from below the level to at/above it,
/// re-armed only on return to Green; absent keys evicted; crossings coalesced into one toast; the
/// on/off switch suppresses only the output, never the recording. Retraction is driven by whether a
/// key's toast was actually shown (Announced), not by the hysteresis latch (Notified) alone — a key
/// already red at the baseline, or one that crossed while notifications were off, was never named in
/// any toast and so leaving red must not retract someone else's.
/// </summary>
public sealed class UsageNotificationRules(
    string tag, Func<Settings, UsageNotificationSettings> notifications)
{
    public static UsageNotificationRules ForClaude()
        => new(NotificationTags.Usage("claude"), s => s.UsageNotifications);

    /// <summary>This provider's Action Center slot. The retraction site reads it from here rather
    /// than from a constant, so emission and retraction cannot drift.</summary>
    public string Tag => tag;

    /// <summary>Toast lifetime for a value with no known reset (credits), so Action Center does not
    /// assert a red credit line for days.</summary>
    public static readonly TimeSpan DefaultUsageToastLifetime = TimeSpan.FromHours(6);
```

Keep `KeyState`, `_keys`, `_fingerprint`, `_source`, `_armed`, `_concludedAttempts`,
`_loggedUnarmedBaseline`, `_lastWasStale`, `IsArmed`, `NoteLiveOutcome`, `Verdict`, `AtOrAbove` and
`Describe` exactly as they are. Change these four places:

```csharp
    private sealed record Fingerprint(int Orange, int Red, bool PaceColors, int StalenessMinutes,
        int DesktopStalenessHours, NotifyLevel Level, string? WeeklyResetAnchor)
    {
        public static Fingerprint Of(Settings s, UsageNotificationSettings n) => new(s.Thresholds.Orange,
            s.Thresholds.Red, s.PaceColors, s.StalenessMinutes, s.DesktopStalenessHours, n.Level,
            s.WeeklyResetAnchor);
    }
```

In `OnUsage`, after the stale guard:

```csharp
        var mine = notifications(settings);

        // Rule 2 — the user's own edit is never news.
        var fingerprint = Fingerprint.Of(settings, mine);
```

Further down, replace the two remaining reads:

```csharp
        var level = mine.Level;
```

```csharp
        if (!mine.Enabled)
```

And make `Compose` an instance method so it can stamp this provider's slot:

```csharp
    private Notification Compose(IReadOnlyList<UsageValue> crossed, DateTimeOffset now)
    {
        // ... body unchanged ...
        return new Notification(title, string.Join(" · ", sentences), NotificationTags.OpenPopupArgument,
            tag, expires);
    }
```

- [x] **Step 5: Create the status half as its own type**

Create `src/ClaudeUsageTray/Core/StatusNotificationRules.cs` with the contents of
`NotificationRules.Status.cs`, changing only the declaration and the two shared references:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>The platform-status half of notifications, split out from the usage half so a
/// per-provider usage-rules instance cannot bring a second copy of this state with it: OnStatus
/// iterates every source, and two instances would announce every outage twice. There is exactly one
/// of these, whatever the provider count.</summary>
public sealed class StatusNotificationRules
{
    // _sources, OnStatus(views, settings), OnStatus(view, notify), Degraded, Recovered — unchanged,
    // except StatusTag(id) → NotificationTags.Status(id) and OpenPopupArgument →
    // NotificationTags.OpenPopupArgument.
}
```

Then delete `src/ClaudeUsageTray/Core/NotificationRules.cs` and
`src/ClaudeUsageTray/Core/NotificationRules.Status.cs`.

- [x] **Step 6: Update TrayApp**

In `src/ClaudeUsageTray/Tray/TrayApp.cs`, replace line 47:

```csharp
    private readonly UsageNotificationRules _usageNotifications = UsageNotificationRules.ForClaude();
    private readonly StatusNotificationRules _statusNotifications = new();
```

Lines 202 and 267 become `_usageNotifications.NoteLiveOutcome(...)`. In `Notify`:

```csharp
        var usage = _usageNotifications.OnUsage(choice, _settings, now);
        foreach (var line in usage.Log) _log.Write(now, line);
        if (usage.RemoveUsageToast) _toasts.Remove(_usageNotifications.Tag);
        if (usage.Notification is { } toast) _toasts.Show(toast);

        foreach (var outcome in _statusNotifications.OnStatus(_statusMonitor.Sources(), _settings))
```

- [x] **Step 7: Update the existing tests mechanically**

`NotificationRulesUsageTests.cs` references the deleted type in **five** distinct ways, not just the
constructor. Grep `NotificationRules` in that file and fix every hit:

| Line | Current | Becomes |
|---|---|---|
| 29 | `private static NotificationRules ArmedAt(...)` | `UsageNotificationRules` |
| 37 | `private static NotificationRules ArmedRules()` | `UsageNotificationRules` |
| 132 | `Assert.Equal(NotificationRules.UsageTag, first.Notification!.Tag)` | **rewrite** — see below |
| 133 | `Assert.Equal(NotificationRules.OpenPopupArgument, ...)` | `NotificationTags.OpenPopupArgument` |
| 283 | `Assert.Equal(at + NotificationRules.DefaultUsageToastLifetime, ...)` | `UsageNotificationRules.DefaultUsageToastLifetime` |
| 17 sites | `new NotificationRules()` | `UsageNotificationRules.ForClaude()` |

Line 132 is not a rename. `UsageTag` has no successor constant, and the *value* it asserted changes
from `"usage"` to `"usage:claude"`. Rewrite it against the instance:

```csharp
        Assert.Equal(NotificationTags.Usage("claude"), first.Notification!.Tag);
```

In `NotificationRulesStatusTests.cs`, replace all 14 `new NotificationRules()` with
`new StatusNotificationRules()`, and any `NotificationRules.StatusTag(` with
`NotificationTags.Status(`. Note that file also calls `monitor.BadgeDegraded()` at line 218 — leave
it alone here; it is Task 3's problem.

The 92 `OnUsage(` call sites need no change — that is why the selector is a constructor parameter
and not a method parameter.

- [x] **Step 8: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~NotificationRules`
Expected: PASS — all usage and status tests, plus the three new ones. German output: `Bestanden!`

- [x] **Step 9: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS. `SmokeTests` constructs real UI and is the one most likely to catch a missed call site.

- [x] **Step 10: Commit**

```bash
git add src/ClaudeUsageTray/Core/Notification.cs src/ClaudeUsageTray/Core/UsageNotificationRules.cs src/ClaudeUsageTray/Core/StatusNotificationRules.cs src/ClaudeUsageTray/Core/Settings.cs src/ClaudeUsageTray/Tray/TrayApp.cs tests/ClaudeUsageTray.Tests/NotificationRulesUsageTests.cs tests/ClaudeUsageTray.Tests/NotificationRulesStatusTests.cs
git rm src/ClaudeUsageTray/Core/NotificationRules.cs src/ClaudeUsageTray/Core/NotificationRules.Status.cs
git commit -m "refactor: make usage notification rules per-provider

Splits NotificationRules into UsageNotificationRules (one per provider)
and StatusNotificationRules (exactly one). The provider's notification
settings arrive through a selector instead of being read off
Settings.UsageNotifications, and the Action Center slot is per-provider
at both the emission and the retraction site."
```

---

### Task 2: A period on `ScopedLimit`

Every consumer of `ScopedLimit` assumes weekly: `UsageValues.ScopedSeverity` paces over
`SevenDayPeriod`, and both `UsageValues.cs:82` and `UsagePopup.cs:204` caption it `"{label} weekly"`.
A limit that resets on a calendar-month boundary would render as "Spend control weekly — 111 %".

The period is an enum, not a `TimeSpan`, because a calendar month has no fixed length: the enum says
what to call it and supplies an approximate length for pacing only. `TimeMarker.ElapsedFraction`
already returns null when the reset is further out than one period, so a 31-day month paces as
absolute-only for its first day rather than going wrong — that is the Review Focus case.

**Files:**
- Modify: `src/ClaudeUsageTray/Core/UsageSnapshot.cs:28-30` (the `ScopedLimit` record)
- Create: `src/ClaudeUsageTray/Core/ScopedPeriod.cs`
- Modify: `src/ClaudeUsageTray/Core/UsageValues.cs:49-52, 77-83`
- Modify: `src/ClaudeUsageTray/Tray/UsagePopup.cs:196-206`
- Test: `tests/ClaudeUsageTray.Tests/UsageValuesTests.cs`
- Test: `tests/ClaudeUsageTray.Tests/ScopedPeriodTests.cs` (create)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces:
  - `enum ScopedPeriod { Weekly, Monthly }`
  - `ScopedPeriods.Length(ScopedPeriod)` → `TimeSpan`
  - `ScopedPeriods.Word(ScopedPeriod)` → `string`
  - `ScopedLimit.Period` → `ScopedPeriod` (init-only, default `Weekly`)

- [x] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/ScopedPeriodTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class ScopedPeriodTests
{
    [Fact]
    public void Weekly_IsSevenDaysAndSaysWeekly()
    {
        Assert.Equal(TimeSpan.FromDays(7), ScopedPeriods.Length(ScopedPeriod.Weekly));
        Assert.Equal("weekly", ScopedPeriods.Word(ScopedPeriod.Weekly));
    }

    [Fact]
    public void Monthly_IsThirtyDaysAndSaysMonthly()
    {
        Assert.Equal(TimeSpan.FromDays(30), ScopedPeriods.Length(ScopedPeriod.Monthly));
        Assert.Equal("monthly", ScopedPeriods.Word(ScopedPeriod.Monthly));
    }

    /// <summary>Default stays weekly, so every existing construction site keeps its meaning without
    /// being edited.</summary>
    [Fact]
    public void ScopedLimit_DefaultsToWeekly()
    {
        var limit = new ScopedLimit("Fable", null, 50, null, true);
        Assert.Equal(ScopedPeriod.Weekly, limit.Period);
    }
}
```

Add to `tests/ClaudeUsageTray.Tests/UsageValuesTests.cs`:

```csharp
    private static readonly DateTimeOffset TScoped = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The caption takes its word from the limit's own period. A monthly cap captioned
    /// "weekly" is a plain falsehood, and the same string goes into the toast.</summary>
    [Fact]
    public void Enumerate_LabelsAMonthlyScopedLimitMonthly()
    {
        var limit = new ScopedLimit("Spend control", null, 111, TScoped.AddDays(19), true)
            { Period = ScopedPeriod.Monthly };
        var snapshot = new UsageSnapshot(TScoped, null, null, [limit]);

        var value = Assert.Single(UsageValues.Enumerate(snapshot, new Settings { PaceColors = false }, TScoped));
        Assert.Equal("Spend control monthly", value.Label);
    }

    /// <summary>Review Focus 1: a reset further out than the period (a 31-day month against a 30-day
    /// length) must pace as absolute-only. ElapsedFraction returns null there, and the absolute
    /// thresholds decide.
    ///
    /// The percentage has to sit *inside* the pace band for this to prove anything.
    /// SeverityRules.ForPace opens with `if (percent > redAbove) return Severity.Red;`, so anything
    /// above 85 is Red whatever the fraction is — a 95 % fixture would pass with the pace path
    /// completely broken. 60 % is Orange absolutely (orangeAt 50) and would be Green if a bogus
    /// fraction were applied, so the assertion actually discriminates.</summary>
    [Fact]
    public void Enumerate_MonthlyLimitResettingBeyondItsPeriod_PacesAbsolute()
    {
        var limit = new ScopedLimit("Spend control", null, 60, TScoped.AddDays(31), true)
            { Period = ScopedPeriod.Monthly };
        var snapshot = new UsageSnapshot(TScoped, null, null, [limit]);
        var settings = new Settings { PaceColors = true };

        var value = Assert.Single(UsageValues.Enumerate(snapshot, settings, TScoped));
        Assert.Equal(Severity.Orange, value.Severity);
    }

    /// <summary>The companion case: a reset inside the period does pace, so the test above is
    /// measuring the fallback and not merely the absence of pacing everywhere.</summary>
    [Fact]
    public void Enumerate_MonthlyLimitResettingInsideItsPeriod_Paces()
    {
        // 60 % used with 29 of 30 days elapsed is ahead of pace, not behind it.
        var limit = new ScopedLimit("Spend control", null, 60, TScoped.AddDays(1), true)
            { Period = ScopedPeriod.Monthly };
        var snapshot = new UsageSnapshot(TScoped, null, null, [limit]);

        var value = Assert.Single(
            UsageValues.Enumerate(snapshot, new Settings { PaceColors = true }, TScoped));
        Assert.Equal(Severity.Green, value.Severity);
    }
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ScopedPeriodTests`
Expected: FAIL — `ScopedPeriod` does not exist (CS0246).

- [x] **Step 3: Create the period type**

Create `src/ClaudeUsageTray/Core/ScopedPeriod.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>How long a scoped limit's window is. An enum rather than a TimeSpan because a calendar
/// month has no fixed length: the payload gives a reset instant, not a window, and the only honest
/// things to say about the window are its name and an approximate length for pacing.</summary>
public enum ScopedPeriod { Weekly, Monthly }

public static class ScopedPeriods
{
    /// <summary>Pacing length only. Monthly is 30 days, so a 31-day month's first day has a reset
    /// further out than one period — TimeMarker.ElapsedFraction answers null there and the value
    /// paces absolutely, which is the fail-safe direction. The countdown shown to the user comes
    /// from the payload's own ResetsAt and is exact regardless.</summary>
    public static TimeSpan Length(ScopedPeriod period) => period switch
    {
        ScopedPeriod.Monthly => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(7),
    };

    /// <summary>The caption word. Used by both the popup row and the notification label, so a toast
    /// and the row it refers to cannot word the same limit differently.</summary>
    public static string Word(ScopedPeriod period) => period switch
    {
        ScopedPeriod.Monthly => "monthly",
        _ => "weekly",
    };
}
```

- [x] **Step 4: Add the period to `ScopedLimit`**

In `src/ClaudeUsageTray/Core/UsageSnapshot.cs`, extend the record. An init property rather than a
positional member, for the reason `WindowUsage.Origin` already records: a positional one would change
`Deconstruct` and force every construction site to pass a value.

```csharp
public sealed record ScopedLimit(
    string Label, string? ModelId, int Percent, DateTimeOffset? ResetsAt, bool IsActive)
{
    /// <summary>The limit's window. Weekly by default so every Claude construction site keeps its
    /// meaning untouched; a provider whose limit resets monthly sets it explicitly.</summary>
    public ScopedPeriod Period { get; init; } = ScopedPeriod.Weekly;
}
```

- [x] **Step 5: Derive severity and caption from the period**

In `src/ClaudeUsageTray/Core/UsageValues.cs`:

```csharp
    /// <summary>A scoped limit paces over its own window, not over a fixed week: a monthly cap
    /// compared against seven days would read as wildly ahead of pace all month.</summary>
    public static Severity ScopedSeverity(ScopedLimit limit, Settings settings, DateTimeOffset now)
        => SeverityRules.ForSettings(settings, limit.Percent,
            TimeMarker.ElapsedFraction(limit.ResetsAt, ScopedPeriods.Length(limit.Period), now));
```

and in `Enumerate`:

```csharp
            var severity = ScopedSeverity(limit, settings, now);
            values.Add(new(limit.Label, $"{limit.Label} {ScopedPeriods.Word(limit.Period)}",
                limit.Percent, severity, limit.ResetsAt, severity));
```

- [x] **Step 6: Derive the popup row from the period**

In `src/ClaudeUsageTray/Tray/UsagePopup.cs`, in `AddScopedRow`:

```csharp
        var resets = limit.ResetsAt is { } r ? $" · resets in {RelativeTime.In(r, now)}" : "";
        var elapsed = TimeMarker.ElapsedFraction(limit.ResetsAt, ScopedPeriods.Length(limit.Period), now);
        AddCaption(layout, $"{limit.Label} {ScopedPeriods.Word(limit.Period)} — {limit.Percent}%{resets}" +
            PaceSuffix(limit.Percent, elapsed, settings));
```

Also update the method's doc comment: it currently begins "A model- or surface-scoped weekly limit."
Drop the word "weekly" — the period is now the limit's own.

- [x] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ScopedPeriodTests|FullyQualifiedName~UsageValuesTests"`
Expected: PASS

- [x] **Step 8: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS. Existing scoped-limit tests assert the word "weekly" and must still pass unchanged —
that is the point of the default.

- [x] **Step 9: Commit**

```bash
git add src/ClaudeUsageTray/Core/ScopedPeriod.cs src/ClaudeUsageTray/Core/UsageSnapshot.cs src/ClaudeUsageTray/Core/UsageValues.cs src/ClaudeUsageTray/Tray/UsagePopup.cs tests/ClaudeUsageTray.Tests/ScopedPeriodTests.cs tests/ClaudeUsageTray.Tests/UsageValuesTests.cs
git commit -m "feat: give a scoped limit its own period

Scoped limits were weekly by construction, in the pace period and in the
caption word. A limit resetting on a calendar-month boundary rendered as
'weekly'. Period is an init property defaulting to Weekly, so every
existing construction site keeps its meaning."
```

---

### Task 3: `RaisesBadge` becomes a provider affinity

`RaisesBadge` is an absolute bool: true marks the tray, false marks nothing. With a second provider's
icons in the tray, the question is no longer *whether* a source marks an icon but *which* icon. The
flag has four consumers — `StatusMonitor.BadgeDegraded` and three in `StatusDetailRows` — and all four
must resolve against the same target.

Behaviour for a Claude-only user is unchanged: Claude's source targets Claude, so
`BadgeDegraded(Claude)` and a Claude-targeted tooltip behave exactly as the old absolute flag did.
OpenAI moves from "marks nothing" to "marks Codex", which is invisible until Codex icons exist.

**Files:**
- Create: `src/ClaudeUsageTray/Core/UsageProvider.cs`
- Modify: `src/ClaudeUsageTray/Core/StatusSource.cs:9-18, 29-50`
- Modify: `src/ClaudeUsageTray/Core/StatusMonitor.cs:89-91`
- Modify: `src/ClaudeUsageTray/Core/StatusDetailRows.cs:57-99`
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs:431-432`
- Test: `tests/ClaudeUsageTray.Tests/StatusSourceRegistryTests.cs:17-18` (assert the deleted
  `RaisesBadge` — must be rewritten, not just extended)
- Test: `tests/ClaudeUsageTray.Tests/StatusMonitorTests.cs:129, 133, 150, 188, 201, 205, 236`
- Test: `tests/ClaudeUsageTray.Tests/StatusDetailTooltipTests.cs:16, 70`
- Test: `tests/ClaudeUsageTray.Tests/UsageTooltipTests.cs:52` — calls `ComposeTooltip`
- Test: `tests/ClaudeUsageTray.Tests/NotificationRulesStatusTests.cs:218` — calls `BadgeDegraded()`

The last two are easy to miss: neither file is about status sources, and both break only because the
new parameter has no default. **Before writing any code, grep for every consumer** —
`BadgeDegraded`, `ComposeTooltip`, `TooltipSuffix`, `RaisesBadge` — across `src` and `tests`, and
work from that list rather than from this one.

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `enum UsageProvider { Claude, Codex }`
  - `StatusSource.BadgeFor` → `UsageProvider?` (replaces `bool RaisesBadge`)
  - `StatusSource.RaisesBadgeFor(UsageProvider)` → `bool`
  - `StatusMonitor.BadgeDegraded(UsageProvider)` → `bool`
  - `StatusDetail.TooltipSuffix(IReadOnlyList<SourceView>, DateTimeOffset, int, int, UsageProvider)` → `string`
  - `StatusDetail.ComposeTooltip(string, IReadOnlyList<SourceView>, DateTimeOffset, int, UsageProvider, int)` → `string`

- [x] **Step 1: Write the failing tests**

Add to `tests/ClaudeUsageTray.Tests/StatusSourceRegistryTests.cs`:

```csharp
    /// <summary>Each curated source marks exactly one provider's icons. Before Codex icons existed
    /// OpenAI marked nothing, which is why RaisesBadge was false; the flag is now which provider,
    /// and OpenAI's answer is Codex.</summary>
    [Fact]
    public void EachSource_MarksExactlyOneProvider()
    {
        Assert.Equal(UsageProvider.Claude, StatusSourceRegistry.Claude.BadgeFor);
        Assert.Equal(UsageProvider.Codex, StatusSourceRegistry.OpenAi.BadgeFor);

        Assert.True(StatusSourceRegistry.Claude.RaisesBadgeFor(UsageProvider.Claude));
        Assert.False(StatusSourceRegistry.Claude.RaisesBadgeFor(UsageProvider.Codex));
        Assert.True(StatusSourceRegistry.OpenAi.RaisesBadgeFor(UsageProvider.Codex));
        Assert.False(StatusSourceRegistry.OpenAi.RaisesBadgeFor(UsageProvider.Claude));
    }
```

Add to `tests/ClaudeUsageTray.Tests/StatusDetailTooltipTests.cs`:

```csharp
    /// <summary>Review Focus 3: both sources degraded, combined text over the 127-character limit.
    /// The suffix belonging to the icon's own provider is the one that explains the marker on that
    /// icon, so it survives; the other is dropped whole rather than cut mid-word.</summary>
    [Fact]
    public void ComposeTooltip_KeepsTheTargetProvidersSuffixAndDropsTheOtherWhole()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var sources = new[]
        {
            View(StatusSourceRegistry.Claude, "Claude API degraded performance across all endpoints", now),
            View(StatusSourceRegistry.OpenAi, "Codex elevated error rates on all responses", now),
        };
        var usage = new string('x', 60);

        var claudeIcon = StatusDetail.ComposeTooltip(usage, sources, now, 30, UsageProvider.Claude);
        Assert.Contains("Claude:", claudeIcon);
        Assert.DoesNotContain("OpenAI:", claudeIcon);
        Assert.True(claudeIcon.Length <= StatusDetail.TooltipLimit);

        var codexIcon = StatusDetail.ComposeTooltip(usage, sources, now, 30, UsageProvider.Codex);
        Assert.Contains("OpenAI:", codexIcon);
        Assert.DoesNotContain("Claude:", codexIcon);
        Assert.True(codexIcon.Length <= StatusDetail.TooltipLimit);
    }
```

If `StatusDetailTooltipTests` has no `View` helper, add one. `PlatformStatus` is
`(SourceId, FetchedAt, Indicator, Description, Incidents, Components)` — note that `Degraded` is a
computed property (`Indicator != "none"`), not a constructor argument:

```csharp
    private static SourceView View(StatusSource source, string description, DateTimeOffset now)
        => new(source, new PlatformStatus(source.Id, now, "major", description, [], []), []);
```

Add to `tests/ClaudeUsageTray.Tests/StatusMonitorTests.cs`:

```csharp
    /// <summary>A source marks its own provider's icons and no one else's. An OpenAI outage must
    /// never put the warning marker on a Claude badge, and vice versa.</summary>
    [Fact]
    public void BadgeDegraded_IsScopedToTheProviderAsked()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var monitor = new StatusMonitor([(StatusSourceRegistry.OpenAi, [])]);
        monitor.TakeDue(now);   // Accept is only honoured for a source that is in flight
        monitor.Accept(StatusSourceRegistry.OpenAi.Id,
            new PlatformStatus(StatusSourceRegistry.OpenAi.Id, now, "major", "Elevated errors", [], []),
            now);

        Assert.True(monitor.BadgeDegraded(UsageProvider.Codex));
        Assert.False(monitor.BadgeDegraded(UsageProvider.Claude));
    }
```

The injection method is `Accept(string sourceId, PlatformStatus? result, DateTimeOffset now)`, not
`Apply` — and `StatusMonitorTests` already has helpers for the `TakeDue`-then-`Accept` dance. Reuse
them if they fit rather than repeating the two calls.

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~StatusSourceRegistryTests|FullyQualifiedName~StatusDetailTooltipTests|FullyQualifiedName~StatusMonitorTests"`
Expected: FAIL — `UsageProvider` does not exist; `BadgeFor` undefined.

- [x] **Step 3: Create the provider enum**

Create `src/ClaudeUsageTray/Core/UsageProvider.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>Which usage account a surface belongs to. Needed before any provider-specific code
/// exists, because "which icons does this status page mark" has no answer without it.</summary>
public enum UsageProvider { Claude, Codex }
```

- [x] **Step 4: Replace the flag on `StatusSource`**

In `src/ClaudeUsageTray/Core/StatusSource.cs`, replace `bool RaisesBadge` with:

```csharp
public sealed record StatusSource(
    string Id,
    string DisplayName,
    string SummaryUrl,
    string PageUrl,
    string PageLabel,
    UsageProvider? BadgeFor,
    bool EnabledByDefault,
    IReadOnlyList<string> DefaultComponents)
{
    /// <summary>Whether this page's disruptions mark the given provider's icons. Was an absolute
    /// bool while one provider had icons; with two, "does this mark the tray" has no single answer
    /// and the question is always asked about a target.</summary>
    public bool RaisesBadgeFor(UsageProvider provider) => BadgeFor == provider;
}
```

Update the record's doc comment: the sentence *"`RaisesBadge` in particular, so 'an OpenAI outage
never marks the tray icon' is a value a test can assert"* now reads as which provider's icons a page
marks.

In the registry, `Claude` takes `BadgeFor: UsageProvider.Claude`, and `OpenAi` takes
`BadgeFor: UsageProvider.Codex` — replacing the comment on the old `RaisesBadge: false` with the
reason it changed:

```csharp
        // Marks the Codex icons, not the tray at large. This was RaisesBadge: false ("an OpenAI
        // outage never marks the tray icon") only because no Codex icon existed to mark.
        BadgeFor: UsageProvider.Codex,
```

- [x] **Step 5: Scope the four consumers to a target**

`src/ClaudeUsageTray/Core/StatusMonitor.cs`:

```csharp
    public bool BadgeDegraded(UsageProvider provider)
        => _entries.Any(e => e.Source.RaisesBadgeFor(provider)
                             && e.Status is { } s && StatusDetail.IsRelevant(s, e.Filter));
```

`src/ClaudeUsageTray/Core/StatusDetailRows.cs` — both methods take the target and pass it down:

```csharp
    public static string TooltipSuffix(IReadOnlyList<SourceView> sources, DateTimeOffset now,
        int stalenessMinutes, int available, UsageProvider target)
    {
        var ordered = sources
            .Where(v => v.Status is { Degraded: true } s && IsRelevant(s, v.Filter))
            .OrderByDescending(v => v.Source.RaisesBadgeFor(target))
            .ToList();

        var text = "";
        foreach (var view in ordered)
        {
            var status = view.Status!;
            var words = string.IsNullOrWhiteSpace(status.Description) ? status.Indicator : status.Description;
            var stale = now - status.FetchedAt > TimeSpan.FromMinutes(stalenessMinutes);
            var piece = $" · {view.Source.DisplayName}: {words}{(stale ? " (stale)" : "")}";
            if (!view.Source.RaisesBadgeFor(target) && text.Length + piece.Length > available) continue;
            text += piece;
        }
        return text;
    }

    public static string ComposeTooltip(string usageText, IReadOnlyList<SourceView> sources,
        DateTimeOffset now, int stalenessMinutes, UsageProvider target, int limit = TooltipLimit)
    {
        var badge = TooltipSuffix(sources.Where(v => v.Source.RaisesBadgeFor(target)).ToList(), now,
            stalenessMinutes, limit, target);
        var room = limit - badge.Length;
        var head = usageText.Length <= room ? usageText
            : room <= 1 ? ""
            : usageText[..(room - 1)] + "…";
        var text = head + badge;
        return text + TooltipSuffix(sources.Where(v => !v.Source.RaisesBadgeFor(target)).ToList(), now,
            stalenessMinutes, available: limit - text.Length, target);
    }
```

Update both doc comments: "badge-raising source first" becomes "the target provider's source first",
and the reserved-budget sentence names the target.

`src/ClaudeUsageTray/Tray/TrayApp.cs` — `WithStatus` takes the provider the icon belongs to:

```csharp
    private string WithStatus(string text, DateTimeOffset now, UsageProvider provider)
        => TrimTooltip(StatusDetail.ComposeTooltip(text, _statusMonitor.Sources(), now,
            _settings.StalenessMinutes, provider));
```

Both `WithStatus` call sites inside `Apply` pass `UsageProvider.Claude`, and `Render`'s
`_statusMonitor.BadgeDegraded()` becomes `BadgeDegraded(UsageProvider.Claude)`.

- [x] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~StatusSource|FullyQualifiedName~StatusDetail|FullyQualifiedName~StatusMonitor"`
Expected: PASS

- [x] **Step 7: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS — but **not unchanged**. Every existing caller of the four scoped methods needs
`UsageProvider.Claude` threaded through, because the new parameter has no default; and
`StatusSourceRegistryTests.cs:17-18` has to be rewritten, because `RaisesBadge` no longer exists.
The behavioural assertions stay as they are: a Claude-targeted tooltip must still reserve Claude's
suffix and drop OpenAI's, exactly as before.

A default value on `target` would have avoided the churn and is deliberately not used: it would let
a future call site silently ask the Claude question about a Codex icon.

- [x] **Step 8: Commit**

```bash
git add src/ClaudeUsageTray/Core/UsageProvider.cs src/ClaudeUsageTray/Core/StatusSource.cs src/ClaudeUsageTray/Core/StatusMonitor.cs src/ClaudeUsageTray/Core/StatusDetailRows.cs src/ClaudeUsageTray/Tray/TrayApp.cs tests/ClaudeUsageTray.Tests/StatusSourceRegistryTests.cs tests/ClaudeUsageTray.Tests/StatusDetailTooltipTests.cs tests/ClaudeUsageTray.Tests/StatusMonitorTests.cs
git commit -m "refactor: RaisesBadge becomes a provider affinity

A status page no longer answers 'does this mark the tray' but 'which
provider's icons does this mark'. Scopes BadgeDegraded and all three
StatusDetailRows consumers — ordering, whole-suffix dropping and the
reserved character budget — to a target provider."
```

---

### Task 4: `StatusSourceSettings.Enabled` becomes undecidable-by-default

`NormalizeFields` materialises an entry for every registry source, defaulting `Enabled` to
`source.EnabledByDefault`, and it runs on `Save` as well as `Load` (`Settings.cs:204, 210`). So the
first settings save of any kind writes an explicit `openai: { enabled: false }`, after which an
inherited default and a deliberate opt-out are indistinguishable. Anything that wants to change a
default later — including the next plan — cannot.

**Files:**
- Modify: `src/ClaudeUsageTray/Core/Settings.cs:88, 219-263, 268-276`
- Modify: `src/ClaudeUsageTray/Tray/SettingsDialog.cs:617, 625`
- Test: `tests/ClaudeUsageTray.Tests/SettingsTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `StatusSourceSettings.Enabled` → `bool?` (null = never decided).
  `Settings.EnabledSources()` keeps its signature and resolves null against `EnabledByDefault`.

- [x] **Step 1: Write the failing tests**

Add to `tests/ClaudeUsageTray.Tests/SettingsTests.cs`:

```csharp
    /// <summary>Review Focus 2: an entry present but with no "enabled" key must stay undecided
    /// through a Save/Load round trip. NormalizeFields runs on Save as well as Load, so an entry it
    /// invents must not harden a default into an explicit value — the difference between "never
    /// decided" and "deliberately off" is what lets a default change later.</summary>
    [Fact]
    public void Save_KeepsAnUndecidedSourceUndecided()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                { "statusSources": { "openai": { "components": ["codex"] } } }
                """);

            var loaded = Settings.Load(path);
            Assert.Null(loaded.StatusSources["openai"]!.Enabled);

            loaded.Save(path);
            var again = Settings.Load(path);
            Assert.Null(again.StatusSources["openai"]!.Enabled);
        }
        finally { File.Delete(path); }
    }

    /// <summary>An explicit false survives just as faithfully — the point is the distinction, not
    /// that null wins.</summary>
    [Fact]
    public void Save_KeepsAnExplicitOptOut()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                { "statusSources": { "openai": { "enabled": false } } }
                """);

            var loaded = Settings.Load(path);
            Assert.False(loaded.StatusSources["openai"]!.Enabled);

            loaded.Save(path);
            Assert.False(Settings.Load(path).StatusSources["openai"]!.Enabled);
        }
        finally { File.Delete(path); }
    }

    /// <summary>An undecided source still resolves to its registry default when the monitor asks.</summary>
    [Fact]
    public void EnabledSources_ResolvesUndecidedAgainstTheRegistryDefault()
    {
        var settings = new Settings
        {
            StatusSources = new(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = new StatusSourceSettings { Enabled = null },
                ["openai"] = new StatusSourceSettings { Enabled = null },
            },
        };

        var ids = settings.EnabledSources().Select(e => e.Source.Id).ToList();
        Assert.Contains("claude", ids);      // EnabledByDefault: true
        Assert.DoesNotContain("openai", ids); // EnabledByDefault: false
    }
```

Match the existing temp-file idiom in `SettingsTests.cs` if it already has one, rather than
introducing a second style.

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SettingsTests`
Expected: FAIL at runtime, not at compile time — `Assert.Null` takes `object?`, so a non-nullable
`bool` boxes happily and the assertion simply fails on the boxed `false` that normalization wrote.

- [x] **Step 3: Make `Enabled` nullable**

In `src/ClaudeUsageTray/Core/Settings.cs`:

```csharp
    /// <summary>Null means never decided: the registry default applies and may still change. An
    /// explicit true or false is the user's own answer and is never overridden. The distinction has
    /// to survive NormalizeFields, which runs on Save as well as Load.</summary>
    public bool? Enabled { get; set; }
```

- [x] **Step 4: Stop normalization from inventing a decision**

In `NormalizeFields`, the invented entry leaves `Enabled` null, and a surviving entry keeps whatever
it had:

```csharp
            StatusSources.TryGetValue(source.Id, out var entry);
            sources[source.Id] = entry is null
                ? new StatusSourceSettings
                {
                    // Enabled stays null: an entry this method invented records no decision, and
                    // writing EnabledByDefault here would make the registry default permanent the
                    // first time anything calls Save.
                    Components = [.. source.DefaultComponents],
                }
                : new StatusSourceSettings
                {
                    Enabled = entry.Enabled,
                    Notify = entry.Notify,
                    Components = entry.Components is null
                        ? [.. source.DefaultComponents]
                        : [.. ComponentFilter.Normalize(entry.Components)],
                };
```

Expect a visible file-format change: `TolerantStatusSourcesConverter.Write` serializes the entry with
options that set no `DefaultIgnoreCondition`, so an undecided source is written as an explicit
`"enabled": null` rather than omitted. It round-trips correctly, and leaving it visible is arguably
better than hiding it — but it will appear in `settings.json` and should not be a surprise. If you
prefer the key omitted, add `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` to the
property and add a test for the omission.

- [x] **Step 5: Resolve null where the answer is needed**

```csharp
    public IReadOnlyList<(StatusSource Source, IReadOnlyList<string> Filter)> EnabledSources()
    {
        var result = new List<(StatusSource, IReadOnlyList<string>)>();
        foreach (var source in StatusSourceRegistry.All)
        {
            if (!StatusSources.TryGetValue(source.Id, out var entry) || entry is null) continue;
            if (entry.Enabled ?? source.EnabledByDefault)
                result.Add((source, entry.Components ?? []));
        }
        return result;
    }
```

- [x] **Step 6: Update the dialog**

`src/ClaudeUsageTray/Tray/SettingsDialog.cs` reads these at lines 617 and 625:

```csharp
        _watchClaude.Checked = claude?.Enabled ?? StatusSourceRegistry.Claude.EnabledByDefault;
```
```csharp
        _watchOpenAi.Checked = openAi?.Enabled ?? StatusSourceRegistry.OpenAi.EnabledByDefault;
```

**The dialog's save path needs a change, not just a read fix.** `SettingsDialog.cs:727-732` writes
`Enabled = _watchOpenAi.Checked` on *every* OK press, whether or not the user touched that checkbox.
Left alone, the first OK on any settings page hardens the default into an explicit `false` —
reintroducing exactly the bug this task removes, just at a different moment.

Remember the loaded value and write it back unchanged when the checkbox still agrees with it:

```csharp
    // Null means "never decided", and pressing OK on an unrelated page must not decide it. Only a
    // checkbox the user actually moved is an answer.
    private bool? _loadedOpenAiEnabled;
    private bool? _loadedClaudeEnabled;
```

```csharp
        // in the load path, beside lines 617 and 625
        _loadedClaudeEnabled = claude?.Enabled;
        _loadedOpenAiEnabled = openAi?.Enabled;
```

```csharp
        // in the save path, replacing Enabled = _watchOpenAi.Checked
        Enabled = Decided(_loadedOpenAiEnabled, _watchOpenAi.Checked, StatusSourceRegistry.OpenAi),
```

```csharp
    /// <summary>The value to persist for a watch checkbox. An undecided source stays undecided while
    /// the box still shows what the registry default resolved to; moving the box is a decision and is
    /// written explicitly.</summary>
    private static bool? Decided(bool? loaded, bool shown, StatusSource source)
        => loaded is not null ? shown
           : shown == source.EnabledByDefault ? null
           : shown;
```

Apply the same to the Claude source at lines 735-740. Add a test to `SettingsDialogTests` that opens
the dialog, presses OK without touching anything, and asserts the OpenAI entry's `Enabled` is still
null.

- [x] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SettingsTests|FullyQualifiedName~SettingsDialog"`
Expected: PASS

- [x] **Step 8: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS

- [x] **Step 9: Commit**

```bash
git add src/ClaudeUsageTray/Core/Settings.cs src/ClaudeUsageTray/Tray/SettingsDialog.cs tests/ClaudeUsageTray.Tests/SettingsTests.cs
git commit -m "fix: let a status source stay undecided

NormalizeFields runs on Save as well as Load and materialised every
registry source with an explicit Enabled, so the first save of any kind
froze the registry default and made 'never decided' indistinguishable
from 'deliberately off'. Enabled is now nullable and resolved at the
point the answer is needed."
```

---

### Task 5: `BadgeShape` through both render paths

Execution note: the implementation landed before the coordinated red compile, so the isolated
Task 5 failing-test run was missed. Its red-run checkbox remains unchecked; the new tests passed
after implementation, and both 16 px silhouettes were visually inspected.

Silhouette is the only channel left to identify a provider: colour is severity, the digit is the
window, arc direction is 5h vs 7d, dimming is staleness, and the bottom-right corner is the
platform-degraded marker.

`RenderNeutral` matters as much as `Render`. `TrayApp.Apply` calls it whenever `usage is null`
(`TrayApp.cs:374-384`) — exactly the startup and partial-payload state a newly detected provider sits
in. A disc-shaped neutral badge would drop the provider encoding precisely when the user first looks
for it.

**Files:**
- Create: `src/ClaudeUsageTray/Tray/BadgeShape.cs`
- Modify: `src/ClaudeUsageTray/Tray/IconRenderer.cs:18-44, 46-60, 71`
- Test: `tests/ClaudeUsageTray.Tests/IconRendererTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `enum BadgeShape { Disc, RoundedSquare }` in namespace `ClaudeUsageTray.Tray`
  - `IconRenderer.Render(char, int, Severity, bool, bool, int, bool = false, BadgeShape = BadgeShape.Disc)` → `Icon`
  - `IconRenderer.RenderNeutral(int, bool = false, BadgeShape = BadgeShape.Disc)` → `Icon`

- [x] **Step 1: Write the failing tests**

Add to `tests/ClaudeUsageTray.Tests/IconRendererTests.cs`:

```csharp
    /// <summary>Review Focus 4: the wedge is a pie, and a pie inscribed in the badge's bounding box
    /// paints into corners a rounded square excludes. At 100 % the whole disc is filled, so if the
    /// clip is missing the corners are opaque. Sampling the extreme corner pixel is the cheapest
    /// statement of "the wedge stayed inside the silhouette".</summary>
    [Fact]
    public void Render_RoundedSquare_LeavesItsCornersEmpty()
    {
        using var icon = IconRenderer.Render('5', 100, Severity.Red, clockwise: true, dimmed: false,
            size: 32, warning: false, shape: BadgeShape.RoundedSquare);
        using var bmp = icon.ToBitmap();

        Assert.Equal(0, bmp.GetPixel(0, 0).A);
        Assert.Equal(0, bmp.GetPixel(bmp.Width - 1, 0).A);
        Assert.Equal(0, bmp.GetPixel(0, bmp.Height - 1).A);
    }

    /// <summary>The two shapes must actually differ, or the provider encoding is decorative. A disc
    /// at 100 % does not fill its corners either, so compare the shapes at a point a rounded square
    /// covers and a disc does not: just inside the top-left corner.</summary>
    [Fact]
    public void Render_ShapesDifferNearTheCorner()
    {
        using var disc = IconRenderer.Render('5', 100, Severity.Red, true, false, 32).ToBitmap();
        using var square = IconRenderer.Render('5', 100, Severity.Red, true, false, 32, false,
            BadgeShape.RoundedSquare).ToBitmap();

        int probe = 4;
        Assert.True(square.GetPixel(probe, probe).A > disc.GetPixel(probe, probe).A,
            "a rounded square must cover more near the corner than a disc");
    }

    /// <summary>The no-data badge carries the shape too: it is the state a newly detected provider
    /// spends its first poll in.</summary>
    [Theory]
    [InlineData(BadgeShape.Disc)]
    [InlineData(BadgeShape.RoundedSquare)]
    public void RenderNeutral_HonoursTheShape(BadgeShape shape)
    {
        using var icon = IconRenderer.RenderNeutral(32, warning: false, shape: shape);
        using var bmp = icon.ToBitmap();

        // Same probe as the shape-comparison test, and for the same reason: see the note below.
        bool covered = bmp.GetPixel(4, 4).A > 0;
        Assert.Equal(shape == BadgeShape.RoundedSquare, covered);
    }

    /// <summary>The warning badge rides over a rounded square as it does over a disc, and is never
    /// dimmed. Its corner is the bottom-right, which the silhouette change must not disturb.</summary>
    [Fact]
    public void RenderNeutral_RoundedSquareWithWarning_StillDrawsTheMarker()
    {
        using var plain = IconRenderer.RenderNeutral(32, false, BadgeShape.RoundedSquare).ToBitmap();
        using var warned = IconRenderer.RenderNeutral(32, true, BadgeShape.RoundedSquare).ToBitmap();

        var p = new Point(warned.Width - 6, warned.Height - 6);
        Assert.NotEqual(plain.GetPixel(p.X, p.Y), warned.GetPixel(p.X, p.Y));
    }
```

**The probe at `(4, 4)` is load-bearing, and the valid band is narrow.** With `size = 32`,
`rect = (0.5, 0.5, 31, 31)` and the corner radius from Step 3 (`31 × 0.22 ≈ 6.82`), the top-left
arc's centre is `(7.32, 7.32)`, so along the diagonal the rounded square's edge sits at about
`(2.5, 2.5)` and the disc's at about `(5.04, 5.04)`. Only pixels **3 and 4** are inside one shape and
outside the other:

- `(1, 1)` is outside *both* — an earlier draft of this plan probed there, and the
  rounded-square assertion would have failed.
- `(6, 6)` and beyond are inside *both*, so the comparison says nothing.

If a probe lands on antialiasing, move it within 3–4 or compare alpha with a tolerance. Do not widen
the assertion to `>= 0`, and do not change the corner radius to make a probe work — if you change
the radius for the reason in Step 6, recompute this band first.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~IconRendererTests`
Expected: FAIL — `BadgeShape` does not exist (CS0246).

- [x] **Step 3: Create the shape type**

Create `src/ClaudeUsageTray/Tray/BadgeShape.cs`:

```csharp
namespace ClaudeUsageTray.Tray;

/// <summary>The badge's silhouette, which is what identifies the provider. Lives in Tray/ beside the
/// renderer rather than in Core/: no pure decision depends on it, and Core stays provider-agnostic
/// about drawing. Every other channel is already spoken for — colour is severity, the digit is the
/// window, arc direction is 5h vs 7d, dimming is staleness, the bottom-right corner is the outage
/// marker — and silhouette is also the only one that survives Windows reordering the tray.</summary>
public enum BadgeShape { Disc, RoundedSquare }
```

- [x] **Step 4: Thread the shape through the renderer**

In `src/ClaudeUsageTray/Tray/IconRenderer.cs`, extend both public entry points and `Draw`, keeping
`shape` last and optional so every existing call site is untouched:

```csharp
    public static Icon Render(char digit, int percent, Severity severity, bool clockwise,
        bool dimmed, int size, bool warning = false, BadgeShape shape = BadgeShape.Disc)
    {
        var color = severity switch
        {
            Severity.Red => Color.FromArgb(224, 68, 68),
            Severity.Orange => Color.FromArgb(232, 150, 40),
            _ => Color.FromArgb(64, 184, 96),
        };
        return Draw(digit.ToString(), Math.Clamp(percent, 0, 100), color, clockwise, dimmed, size,
            warning, shape);
    }

    public static Icon RenderNeutral(int size, bool warning = false, BadgeShape shape = BadgeShape.Disc)
        => Draw("—", percent: 0, Color.FromArgb(150, 150, 150), clockwise: true, dimmed: false, size,
            warning, shape);
```

In `Draw`, replace the three ellipse calls with a path, and clip the wedge to it:

```csharp
    private static Icon Draw(string glyph, int percent, Color color, bool clockwise, bool dimmed,
        int size, bool warning, BadgeShape shape)
    {
        if (dimmed) color = Color.FromArgb(120, color);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var rect = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
            using var path = ShapePath(rect, shape);

            // Translucent tinted disc marks the unused fraction.
            using (var back = new SolidBrush(Color.FromArgb(dimmed ? 50 : 90, color)))
                g.FillPath(back, path);

            // Solid usage wedge from 12 o'clock (-90°); counter-clockwise = negative sweep (7d).
            // Clipped to the silhouette: FillPie inscribes its pie in the bounding box, so without
            // the clip a rounded square's corners would take paint the shape excludes.
            if (percent > 0)
            {
                float sweep = 360f * percent / 100f;
                if (!clockwise) sweep = -sweep;
                // Save/Restore rather than stashing g.Clip: the Clip property returns a *new* Region
                // on every read, and this runs for up to four icons on every Render — the handles
                // would accumulate for as long as the tray is open.
                var state = g.Save();
                g.SetClip(path);
                using (var fill = new SolidBrush(color))
                    g.FillPie(fill, rect.X, rect.Y, rect.Width, rect.Height, -90f, sweep);
                g.Restore(state);
            }

            using (var rim = new Pen(color, 1f))
                g.DrawPath(rim, path);

            // ... digit, halo and warning badge unchanged ...
        }
        // ... HICON handling unchanged ...
    }

    /// <summary>The badge outline. The corner radius is a fraction of the size rather than a fixed
    /// pixel count, so a 16 px badge and a 32 px one read as the same shape; it is floored at 2 px
    /// because below that a rounded square is indistinguishable from a square.</summary>
    private static GraphicsPath ShapePath(RectangleF rect, BadgeShape shape)
    {
        var path = new GraphicsPath();
        if (shape == BadgeShape.Disc)
        {
            path.AddEllipse(rect);
            return path;
        }

        float r = Math.Max(2f, rect.Width * 0.22f);
        float d = r * 2f;
        path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }
```

- [x] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~IconRendererTests`
Expected: PASS. If a corner probe fails, print the actual alpha at that pixel and move the probe
inward — do not relax the assertion to `>= 0`.

- [x] **Step 6: Eyeball the real thing once**

Per CLAUDE.md, drawing is verified by sampling, but the corner radius is an aesthetic judgement a
sample cannot make. Write a throwaway file under `tests/ClaudeUsageTray.Tests/` that renders both
shapes at 16 px and scales them 6x with `InterpolationMode.NearestNeighbor` into a PNG, look at it,
then delete the file. It is a verification artifact, not a test.

- [x] **Step 7: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS. Every existing `IconRenderer` call omits `shape` and must behave identically.

- [x] **Step 8: Commit**

```bash
git add src/ClaudeUsageTray/Tray/BadgeShape.cs src/ClaudeUsageTray/Tray/IconRenderer.cs tests/ClaudeUsageTray.Tests/IconRendererTests.cs
git commit -m "feat: let a badge be drawn as a rounded square

Silhouette is the only channel left to identify a provider. Both render
paths take the shape, RenderNeutral included — that is the state a newly
detected provider spends its first poll in. The usage wedge is clipped
to the outline, because FillPie inscribes its pie in the bounding box."
```

---

### Task 6: Bound the popup's height

`PopupRows.Cap` does not bound the popup. `ForScopedLimits` returns `[.. active, .. inactive.Take(slots)]`
— every active limit renders past the cap by design, because "the cap exists to bound a list of
background limits, and must never be the reason the limit actually throttling the user is invisible".
The form is `AutoSize`, and `PositionNearCursor` clamps position but not size. A second provider's
rows make an unbounded height a real case rather than a theoretical one.

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/UsagePopup.cs:12-28, 86-88, 252-264`
- Test: `tests/ClaudeUsageTray.Tests/UsagePopupHeightTests.cs` (create)

**Interfaces:**
- Consumes: `ScopedLimit.Period` from Task 2 (only to build test fixtures; any limit works).
- Produces: `UsagePopup(..., int? maxHeight = null)` — the cap as an injectable seam, defaulting to
  90 % of the working area. The scroll host is named `"scrollHost"` so a test can find it.

The cap **must** be injectable. The obvious test — "build a popup with many rows and assert it fits
the screen" — is not a test: a dozen scoped rows come to roughly 650 px (a caption at ~19 px plus a
12 px bar plus margins, ~43 px per row), so on any ordinary display the clamp never engages, the
overflow assertion passes before the change and proves nothing, and the scrolling assertion fails
after it. Passing the cap in makes both deterministic and independent of the machine's resolution.

- [x] **Step 1: Write the failing test**

Create `tests/ClaudeUsageTray.Tests/UsagePopupHeightTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Review Focus 5: more rows than there is room for. CreateControl, never Show —
/// UsagePopup.OnDeactivate calls Close, and with no message loop Show disposes the form mid-test.
/// The cap is injected rather than taken from the screen, so these assertions mean the same thing on
/// a 1080p laptop and a 4K monitor.</summary>
public class UsagePopupHeightTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static DisplayChoice Crowded()
    {
        var scoped = Enumerable.Range(0, 12)
            .Select(i => new ScopedLimit($"Limit {i}", null, 50 + i, Now.AddDays(2), IsActive: true))
            .ToList();
        return new(new UsageSnapshot(Now, new WindowUsage(42, Now.AddHours(3)),
            new WindowUsage(21, Now.AddDays(4)), scoped), Stale: false);
    }

    private static DisplayChoice Small()
        => new(new UsageSnapshot(Now, new WindowUsage(42, Now.AddHours(3)),
            new WindowUsage(21, Now.AddDays(4))), Stale: false);

    private static Panel Host(UsagePopup popup)
        => popup.Controls.Find("scrollHost", searchAllChildren: true).OfType<Panel>().Single();

    /// <summary>The actual size, not PreferredSize: Form.PreferredSize is GetPreferredSize(Empty)
    /// and ignores the clamp entirely, so asserting on it would pass whatever the clamp did.</summary>
    [Fact]
    public void Popup_OverTheCap_ClampsItsHeight()
    {
        using var popup = new UsagePopup(Crowded(), new Settings(), Now, maxHeight: 200);
        popup.CreateControl();

        Assert.True(popup.Height <= 200, $"popup is {popup.Height} px, cap was 200 px");
    }

    [Fact]
    public void Popup_OverTheCap_Scrolls()
    {
        using var popup = new UsagePopup(Crowded(), new Settings(), Now, maxHeight: 200);
        popup.CreateControl();

        var host = Host(popup);
        Assert.True(host.AutoScroll);
        Assert.True(host.AutoScrollMinSize.Height > host.ClientSize.Height
                    || host.VerticalScroll.Visible,
            $"content {host.AutoScrollMinSize.Height} px must exceed viewport {host.ClientSize.Height} px");
    }

    /// <summary>Under the cap nothing changes: no scrollbar, no reserved gutter, and the form still
    /// sizes itself to its content. The common case is two windows and it looked right before.</summary>
    [Fact]
    public void Popup_UnderTheCap_DoesNotScroll()
    {
        using var popup = new UsagePopup(Small(), new Settings(), Now, maxHeight: 2000);
        popup.CreateControl();

        Assert.False(Host(popup).AutoScroll);
        Assert.True(popup.Height < 2000);
    }

    /// <summary>And the clamp engages off the real screen when no cap is passed, which is how the app
    /// calls it.</summary>
    [Fact]
    public void Popup_WithNoCap_StaysWithinTheWorkingArea()
    {
        using var popup = new UsagePopup(Crowded(), new Settings(), Now);
        popup.CreateControl();

        Assert.True(popup.Height <= Screen.PrimaryScreen!.WorkingArea.Height);
    }
}
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~UsagePopupHeightTests`
Expected: FAIL — the constructor has no `maxHeight` parameter (CS1739), and there is no control named
`scrollHost`.

- [x] **Step 3: Host the layout in a scrollable panel**

Two WinForms traps to design around, both of which an earlier draft of this plan fell into:

1. **`AutoScroll` and `AutoSize` on the same panel defeat each other.** A panel that grows to fit its
   content never has content to scroll. So the host starts with `AutoScroll = false` and only turns it
   on when the clamp engages.
2. **A `Dock = Fill` child is sized *to* the viewport,** so `AutoScrollMinSize` stays at the client
   size and no scrollbar ever appears. `layout` is created with `Dock = DockStyle.Fill`
   (`UsagePopup.cs:27`); when the clamp engages, that has to be undone so the layout reports its own
   preferred height.

In `src/ClaudeUsageTray/Tray/UsagePopup.cs`, take the cap in the constructor and insert a host
between the form and the layout, replacing `Controls.Add(layout); PositionNearCursor();`:

```csharp
    public UsagePopup(DisplayChoice choice, Settings settings, DateTimeOffset now,
        IReadOnlyList<SourceView>? statusSources = null, string? lastFetchStatus = null,
        string? noDataText = null, int? maxHeight = null)
```

```csharp
        // The row count is not bounded: PopupRows.Cap bounds only inactive scoped limits, and every
        // active one renders past it on purpose ("the cap must never be the reason the limit actually
        // throttling the user is invisible"). With a second provider's rows in the same popup an
        // unbounded AutoSize form can exceed the screen, and PositionNearCursor clamps position but
        // not size. AutoScroll stays off until the clamp engages: a panel that grows to fit its
        // content has nothing to scroll.
        var host = new Panel
        {
            Name = "scrollHost",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AutoScroll = false,
            Dock = DockStyle.Fill,
        };
        host.Controls.Add(layout);
        Controls.Add(host);

        ClampHeight(host, layout, maxHeight);
        PositionNearCursor();
```

and add:

```csharp
    /// <summary>Caps the form's height, letting the host scroll past the cap. Default is 90 % of the
    /// working area of the screen the cursor is on — measured rather than guessed, for the same
    /// reason PositionNearCursor measures: large system fonts and high DPI defeat any hardcoded
    /// estimate. The cap is injectable so a test can exercise the overflow path without depending on
    /// the machine's resolution.</summary>
    private void ClampHeight(Panel host, TableLayoutPanel layout, int? maxHeight)
    {
        PerformLayout();
        int max = maxHeight ?? (int)(Screen.FromPoint(Cursor.Position).WorkingArea.Height * 0.9);
        if (PreferredSize.Height <= max) return;

        // Undock the layout before enabling AutoScroll: a Fill-docked child is sized to the viewport,
        // so the host would clip the overflow instead of scrolling it.
        layout.Dock = DockStyle.None;
        layout.Location = Point.Empty;

        host.AutoSize = false;
        host.AutoScroll = true;

        AutoSize = false;
        Height = max;
    }
```

`PositionNearCursor` reads `PreferredSize`, which is `GetPreferredSize(Size.Empty)` and is **not**
affected by `AutoSize = false` — so once the clamp engages it would reposition against the unclamped
height and push the popup off-screen. Change it to measure the real size:

```csharp
        PerformLayout();
        var size = AutoSize ? PreferredSize : Size;
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~UsagePopupHeightTests`
Expected: PASS

- [x] **Step 5: Look at both cases once**

Render a crowded popup and a two-window popup to PNG with `Control.DrawToBitmap`, per CLAUDE.md, and
confirm the short one is unchanged and the tall one scrolls without a clipped last row. Delete the
probe file afterwards.

- [x] **Step 6: Run the whole suite for regressions**

Run: `dotnet test`
Expected: PASS. `UsagePopupWidthTests`, `UsagePopupSourceTests` and `UsagePopupStatusTests` all
construct real popups and will catch a layout regression; note that
`SettingsDialogTests.EveryPageFitsWithoutScrolling` is known to fail only under parallel
solution-level runs — rerun the app project alone before treating it as a real failure.

- [x] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Tray/UsagePopup.cs tests/ClaudeUsageTray.Tests/UsagePopupHeightTests.cs
git commit -m "fix: bound the popup's height

PopupRows.Cap bounds only inactive scoped limits; every active one
renders past it by design, so an AutoSize popup had no height bound. The
layout now sits in a scrollable host capped at 90% of the working area
of the screen the cursor is on."
```

---

## Execution record — 2026-10-28

Implemented with three subagents, followed by cross-review and centralized integration checks.
All six tasks are included in one consolidated implementation commit because they share settings,
tray wiring and popup files. The isolated Task 5 red run was missed, as recorded in that section.

- Validation: 848 app tests passed with test collections serialized, plus all 153 setup-stub tests.
  The first parallel app run was stopped after it appeared stalled; the sequential retry completed
  in about 4.4 minutes. Existing UI tests take about two seconds each on this machine.
- The sequential retry used `dotnet test tests/ClaudeUsageTray.Tests --no-restore -m:1
  --disable-build-servers --settings artifacts/prerequisites.runsettings`, with the following
  temporary runsettings: `<RunSettings><xUnit><MaxParallelThreads>1</MaxParallelThreads>
  <ParallelizeTestCollections>false</ParallelizeTestCollections></xUnit></RunSettings>`.
  `DOTNET_CLI_HOME` pointed to `artifacts/dotnet-home` to keep SDK initialization inside the workspace.
- Dialog watch checkboxes track edits, including toggling back to the initial value, so the existing
  explicit-opt-out test remains unchanged while untouched saves keep null.
- Visual checks covered both badge shapes at 16 px, the short popup, and the tall popup at both ends
  of its scroll range. They caught width clipping when AutoSize was disabled: the clamp now preserves
  the measured width and adds the vertical scrollbar gutter. A regression test pins that behavior.
- Existing popup status tests now traverse the new host recursively; their assertions are unchanged.
  Temporary drawing probes were removed. `git diff --check` passed.
## Done when

- `dotnet test` passes. Two existing assertions are *expected* to change, and only these two:
  `NotificationRulesUsageTests.cs:132` (the toast tag's value becomes `usage:claude`) and
  `StatusSourceRegistryTests.cs:17-18` (`RaisesBadge` is replaced by `RaisesBadgeFor`). Every other
  existing assertion must still hold verbatim; call sites may gain a `UsageProvider.Claude` argument,
  but no behavioural expectation changes.
- A Claude-only user sees the same icons, the same popup, the same toasts and the same settings.
- `UsageNotificationRules` can be instantiated a second time with a different settings selector and a
  different Action Center slot, and nothing it touches is Claude-specific.
- `StatusSource.BadgeFor`, `ScopedLimit.Period`, `StatusSourceSettings.Enabled` and `BadgeShape` all
  exist, each with a test pinning the generalised behaviour.
