# Claude Desktop Usage History Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show usage percentages to users whose only usable source is the Claude Desktop app's local `plan-usage-history.json`, without regressing users whose Claude Code cache or live fetch is current, and replace the unconditional "run Claude Code" hint with messages that name what is missing.

**Architecture:** Two new pure readers in `Core/` (`DesktopHistoryPath` finds the file, `DesktopUsageReader` parses it into the existing `UsageSnapshot`), a pure `SourceSelection` that picks between the Claude Code snapshot and the desktop snapshot with per-source staleness, and a pure `NoDataReason` that turns file-status facts into one sentence. `TrayApp` holds two snapshot slots instead of one and hands the selection result to the icons and the popup. Everything time-dependent takes a caller-supplied `now`.

**Tech Stack:** .NET 10, C# 13, WinForms (Tray only), System.Text.Json, xUnit 2.9.

**Spec:** `docs/superpowers/specs/2026-09-05-desktop-usage-history-design.md`

## Global Constraints

- Nothing in a read path throws: every new reader swallows `IOException`, `JsonException`, `UnauthorizedAccessException`, `ArgumentException`, `NotSupportedException` and returns null / a status.
- Absent data means no row; never synthesise a `0 %`.
- `fetch.log` carries percentages and ages only. Never an org uuid, money, or model names.
- The token stays read-only; this work never touches `.credentials.json` beyond existence and the existing validation.
- Field semantics of `plan-usage-history.json` are inferred (`fh` = five-hour, `sd` = seven-day, `xu` = credits utilization); XML doc comments say so.
- User-facing strings use "Claude Desktop" and "Claude Code" (capitalised, no "app").
- Commit messages end with `Refs #5` and the trailer `Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL`. No Co-Authored-By trailers.
- Run from the worktree `C:\Users\ChristianFoellmann\projects\wus-technik\win_systray-claude-usage\.claude\worktrees\issue-5-desktop-only`. Test with `dotnet test --filter FullyQualifiedName~<Class>`; full `dotnet test` before the final commit.

---

## File structure

| File | Responsibility |
|---|---|
| `src/ClaudeUsageTray/Core/UsageSnapshot.cs` (modify) | `UsageSource` enum and `Source` property on the snapshot |
| `src/ClaudeUsageTray/Core/Settings.cs` (modify) | `DesktopStalenessHours`, `DesktopHistoryPathOverride`, default constant, normalisation |
| `src/ClaudeUsageTray/Core/DesktopHistoryPath.cs` (create) | Candidate paths for the desktop file, ordered by file write time |
| `src/ClaudeUsageTray/Core/DesktopUsageReader.cs` (create) | Parse the file into a `UsageSnapshot`, report why not; walk candidates |
| `src/ClaudeUsageTray/Core/SourceSelection.cs` (create) | Fallback-only precedence and per-source staleness |
| `src/ClaudeUsageTray/Core/NoDataReason.cs` (create) | Status enums and the empty-state sentence |
| `src/ClaudeUsageTray/Core/UsageCacheReader.cs` (modify) | `Status(path)` |
| `src/ClaudeUsageTray/Core/CredentialsReader.cs` (modify) | `Status(path, now)` |
| `src/ClaudeUsageTray/Tray/SettingsDialog.cs` (modify) | Second staleness spinner |
| `src/ClaudeUsageTray/Tray/UsagePopup.cs` (modify) | Takes a `DisplayChoice`; source-aware last-updated line; no-data text |
| `src/ClaudeUsageTray/Tray/TrayApp.cs` (modify) | Two slots, desktop read in `Refresh`, selection in `Render`, log lines, fetch status |
| `README.md`, `CHANGELOG.md` (modify) | Sources, icon table, settings table, Unreleased entry |
| `tests/ClaudeUsageTray.Tests/*` | One test class per new Core unit plus additions to existing ones |

---

### Task 1: `UsageSource` on the snapshot

**Files:**
- Modify: `src/ClaudeUsageTray/Core/UsageSnapshot.cs`
- Test: `tests/ClaudeUsageTray.Tests/UsageSnapshotTests.cs` (create)

**Interfaces:**
- Produces: `public enum UsageSource { ClaudeCode, DesktopHistory }` and `UsageSnapshot.Source` (init-only, default `ClaudeCode`). Tasks 4, 9 and 10 rely on both names.

- [ ] **Step 1: Write the failing test**

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class UsageSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Source_DefaultsToClaudeCode()
        => Assert.Equal(UsageSource.ClaudeCode, new UsageSnapshot(Now, null, null).Source);

    [Fact]
    public void Source_CanBeSetToDesktopHistory()
    {
        var s = new UsageSnapshot(Now, null, null) { Source = UsageSource.DesktopHistory };
        Assert.Equal(UsageSource.DesktopHistory, s.Source);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~UsageSnapshotTests`
Expected: build error, `UsageSource` does not exist.

- [ ] **Step 3: Add the enum and property**

In `src/ClaudeUsageTray/Core/UsageSnapshot.cs`, before the `UsageSnapshot` record:

```csharp
/// <summary>Which program produced the data. The cache file and the live API are both Claude
/// Code's; the distinction the user sees is Claude Code versus the Claude Desktop history.</summary>
public enum UsageSource { ClaudeCode, DesktopHistory }
```

Inside the record body, after the `ScopedLimits` property:

```csharp
    public UsageSource Source { get; init; } = UsageSource.ClaudeCode;
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~UsageSnapshotTests`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/UsageSnapshot.cs tests/ClaudeUsageTray.Tests/UsageSnapshotTests.cs
git commit -m "feat(desktop-history): tag a usage snapshot with its source

Refs #5

Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 2: Settings keys

**Files:**
- Modify: `src/ClaudeUsageTray/Core/Settings.cs`
- Test: `tests/ClaudeUsageTray.Tests/SettingsTests.cs`

**Interfaces:**
- Produces: `ThresholdRules.DefaultDesktopStalenessHours` (`3`), `Settings.DesktopStalenessHours` (int), `Settings.DesktopHistoryPathOverride` (string?). Tasks 5, 8 and 10 use them.

- [ ] **Step 1: Write the failing tests**

Append to `tests/ClaudeUsageTray.Tests/SettingsTests.cs` inside the class:

```csharp
    [Fact]
    public void RoundTrip_PreservesDesktopKeys()
    {
        var path = PathFor("settings.json");
        new Settings
        {
            DesktopStalenessHours = 6,
            DesktopHistoryPathOverride = @"C:\alt\plan-usage-history.json",
        }.Save(path);
        var loaded = Settings.Load(path);

        Assert.Equal(6, loaded.DesktopStalenessHours);
        Assert.Equal(@"C:\alt\plan-usage-history.json", loaded.DesktopHistoryPathOverride);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-4")]
    public void Load_NonPositiveDesktopStaleness_ResetsToDefault(string value)
    {
        var path = PathFor("settings.json");
        File.WriteAllText(path, $$$"""{ "desktopStalenessHours": {{{value}}}, "stalenessMinutes": 7 }""");
        var loaded = Settings.Load(path);

        Assert.Equal(ThresholdRules.DefaultDesktopStalenessHours, loaded.DesktopStalenessHours);
        Assert.Equal(7, loaded.StalenessMinutes); // only the invalid field resets
    }

    [Fact]
    public void Load_FileWithoutDesktopKeys_UsesDefaults()
    {
        var path = PathFor("settings.json");
        File.WriteAllText(path, """{ "stalenessMinutes": 15 }""");
        var loaded = Settings.Load(path);

        Assert.Equal(3, loaded.DesktopStalenessHours);
        Assert.Null(loaded.DesktopHistoryPathOverride);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SettingsTests`
Expected: build error, `DesktopStalenessHours` does not exist.

- [ ] **Step 3: Add the constant, properties and normalisation**

In `ThresholdRules`, after `DefaultStalenessMinutes`:

```csharp
    /// <summary>The desktop app samples usage only while someone works in it, so gaps of an hour are
    /// normal; a minutes-scale cutoff would flag a desktop-only user as stale most of the time.</summary>
    public const int DefaultDesktopStalenessHours = 3;
```

In `Settings`, after `StalenessMinutes`:

```csharp
    /// <summary>Staleness allowance for the Claude Desktop history source, in hours. Separate from
    /// <see cref="StalenessMinutes"/> because the two sources have cadences an order of magnitude apart.</summary>
    public int DesktopStalenessHours { get; set; } = ThresholdRules.DefaultDesktopStalenessHours;
```

After `ConfigPathOverride`:

```csharp
    /// <summary>Explicit path to the desktop app's plan-usage-history.json. File-only, like
    /// <see cref="ConfigPathOverride"/>; two real locations already exist in the wild and a third
    /// should not need a release.</summary>
    public string? DesktopHistoryPathOverride { get; set; }
```

In `NormalizeFields()`, after the `StalenessMinutes` line:

```csharp
        if (DesktopStalenessHours <= 0) DesktopStalenessHours = ThresholdRules.DefaultDesktopStalenessHours;
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~SettingsTests`
Expected: all passed, including the three new ones.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/Settings.cs tests/ClaudeUsageTray.Tests/SettingsTests.cs
git commit -m "feat(desktop-history): desktopStalenessHours and desktopHistoryPathOverride settings

Refs #5

Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 3: `DesktopHistoryPath`

**Files:**
- Create: `src/ClaudeUsageTray/Core/DesktopHistoryPath.cs`
- Test: `tests/ClaudeUsageTray.Tests/DesktopHistoryPathTests.cs` (create)

**Interfaces:**
- Produces:
  - `DesktopHistoryPath.Candidates(string? overridePath, string appData, string localAppData) : IReadOnlyList<string>`
  - `DesktopHistoryPath.ByFreshness(IEnumerable<string> candidates) : IReadOnlyList<string>` (existing files only, newest write first)
  - `DesktopHistoryPath.DefaultAppData`, `DesktopHistoryPath.DefaultLocalAppData` (the two special folders)
  - Task 10 calls all four.

- [ ] **Step 1: Write the failing tests**

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class DesktopHistoryPathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cut-desktop-path-").FullName;
    private string AppData => Path.Combine(_dir, "Roaming");
    private string LocalAppData => Path.Combine(_dir, "Local");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Classic => Path.Combine(AppData, "Claude", DesktopHistoryPath.FileName);

    private string Container(string package = "Claude_pzs8sxrjxfjjc") => Path.Combine(
        LocalAppData, "Packages", package, "LocalCache", "Roaming", "Claude", DesktopHistoryPath.FileName);

    private static string Write(string path, DateTime writtenUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    private static readonly DateTime T0 = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    // ---- Candidates ----

    [Fact]
    public void Candidates_ClassicPathAlwaysListed_EvenWhenNothingExists()
    {
        var c = DesktopHistoryPath.Candidates(null, AppData, LocalAppData);
        Assert.Equal([Classic], c);
    }

    [Fact]
    public void Candidates_IncludesEveryClaudePackageContainer()
    {
        Directory.CreateDirectory(Path.Combine(LocalAppData, "Packages", "Claude_aaaa"));
        Directory.CreateDirectory(Path.Combine(LocalAppData, "Packages", "Claude_bbbb"));
        Directory.CreateDirectory(Path.Combine(LocalAppData, "Packages", "Other_cccc"));

        var c = DesktopHistoryPath.Candidates(null, AppData, LocalAppData);

        Assert.Equal(3, c.Count);
        Assert.Contains(Classic, c);
        Assert.Contains(Container("Claude_aaaa"), c);
        Assert.Contains(Container("Claude_bbbb"), c);
    }

    [Fact]
    public void Candidates_Override_IsTheOnlyCandidate()
    {
        Write(Classic, T0);
        var overridePath = Path.Combine(_dir, "elsewhere.json");
        Assert.Equal([overridePath], DesktopHistoryPath.Candidates(overridePath, AppData, LocalAppData));
    }

    [Fact]
    public void Candidates_BlankOverride_IsIgnored()
        => Assert.Equal([Classic], DesktopHistoryPath.Candidates("   ", AppData, LocalAppData));

    [Fact]
    public void Candidates_InvalidOverride_IsEmptyAndDoesNotThrow()
        => Assert.Empty(DesktopHistoryPath.Candidates("C:\\bad\0path.json", AppData, LocalAppData));

    // ---- ByFreshness ----

    [Fact]
    public void ByFreshness_DropsMissingFiles()
    {
        Write(Classic, T0);
        var ordered = DesktopHistoryPath.ByFreshness([Classic, Container()]);
        Assert.Equal([Classic], ordered);
    }

    [Fact]
    public void ByFreshness_NoneExist_IsEmpty()
        => Assert.Empty(DesktopHistoryPath.ByFreshness([Classic, Container()]));

    [Fact]
    public void ByFreshness_NewerFileFirst_ClassicNewer()
    {
        Write(Classic, T0);
        Write(Container(), T0.AddHours(-1));
        Assert.Equal([Classic, Container()], DesktopHistoryPath.ByFreshness([Classic, Container()]));
    }

    [Fact]
    public void ByFreshness_NewerFileFirst_ContainerNewer()
    {
        Write(Classic, T0.AddDays(-30));
        Write(Container(), T0);
        Assert.Equal([Container(), Classic], DesktopHistoryPath.ByFreshness([Classic, Container()]));
    }

    /// <summary>An orphaned %APPDATA%\Claude keeps getting touched while its usage file is weeks old;
    /// the ordering must read the file, not its directory.</summary>
    [Fact]
    public void ByFreshness_IgnoresDirectoryWriteTime()
    {
        Write(Classic, T0.AddDays(-30));
        Write(Container(), T0.AddHours(-1));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(Classic)!, T0);

        Assert.Equal([Container(), Classic], DesktopHistoryPath.ByFreshness([Classic, Container()]));
    }

    [Fact]
    public void ByFreshness_InvalidCandidate_IsDroppedNotThrown()
    {
        Write(Classic, T0);
        Assert.Equal([Classic], DesktopHistoryPath.ByFreshness(["C:\\bad\0path.json", Classic]));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~DesktopHistoryPathTests`
Expected: build error, `DesktopHistoryPath` does not exist.

- [ ] **Step 3: Implement**

`src/ClaudeUsageTray/Core/DesktopHistoryPath.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>Where the Claude Desktop app keeps plan-usage-history.json. Two locations exist in the
/// wild and the install kind does not predict which: the same MSIX package family wrote to the
/// classic %APPDATA% on one version and to its package container on later ones. Both are probed
/// unconditionally and the caller reads them newest-first. Finding the file is the only evidence
/// that the desktop app is present — %APPDATA%\Claude existing proves nothing (it can be a
/// hand-placed config, or an orphaned profile that is still touched).</summary>
public static class DesktopHistoryPath
{
    public const string FileName = "plan-usage-history.json";

    public static string DefaultAppData
        => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string DefaultLocalAppData
        => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The paths worth probing, existence not checked. With an override, only that path;
    /// an override that is not even a valid path yields nothing rather than throwing.</summary>
    public static IReadOnlyList<string> Candidates(string? overridePath, string appData, string localAppData)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return IsValidPath(overridePath) ? [overridePath] : [];

        var list = new List<string>();
        Guard(() => list.Add(Path.Combine(appData, "Claude", FileName)));
        Guard(() =>
        {
            var packages = Path.Combine(localAppData, "Packages");
            if (!Directory.Exists(packages)) return;
            // Glob the package family: the publisher hash is stable in practice but not guaranteed.
            foreach (var dir in Directory.EnumerateDirectories(packages, "Claude_*"))
                list.Add(Path.Combine(dir, "LocalCache", "Roaming", "Claude", FileName));
        });
        return list;
    }

    /// <summary>The existing candidates, newest LastWriteTimeUtc first. Keyed on the usage file's
    /// own write time, never its directory's. A candidate whose probe fails is dropped; the others
    /// survive. Never throws.</summary>
    public static IReadOnlyList<string> ByFreshness(IEnumerable<string> candidates)
    {
        var existing = new List<(string Path, DateTime Written)>();
        foreach (var candidate in candidates)
        {
            Guard(() =>
            {
                var info = new FileInfo(candidate);
                if (info.Exists) existing.Add((candidate, info.LastWriteTimeUtc));
            });
        }
        return existing.OrderByDescending(e => e.Written).Select(e => e.Path).ToList();
    }

    private static bool IsValidPath(string path)
    {
        try { _ = Path.GetFullPath(path); return true; }
        catch (Exception e) when (IsIo(e)) { return false; }
    }

    private static void Guard(Action probe)
    {
        try { probe(); }
        catch (Exception e) when (IsIo(e)) { /* this candidate is dropped, the others survive */ }
    }

    // PathTooLongException derives from IOException; SecurityException covers ACL'd package dirs.
    private static bool IsIo(Exception e) => e is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~DesktopHistoryPathTests`
Expected: 11 passed. If `Candidates_InvalidOverride_IsEmptyAndDoesNotThrow` fails because .NET accepted the path, change the invalid path in both tests to `"C:\\bad\0path.json"` with the literal NUL kept, which `Path.GetFullPath` and `FileInfo` reject with `ArgumentException`.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/DesktopHistoryPath.cs tests/ClaudeUsageTray.Tests/DesktopHistoryPathTests.cs
git commit -m "feat(desktop-history): locate plan-usage-history.json in both known places, newest first

Refs #5

Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 4: `DesktopUsageReader`

**Files:**
- Create: `src/ClaudeUsageTray/Core/DesktopUsageReader.cs`
- Test: `tests/ClaudeUsageTray.Tests/DesktopUsageReaderTests.cs` (create)

**Interfaces:**
- Consumes: `UsageSource.DesktopHistory` (Task 1), `UsageJson.ReadRoundedPercent` (existing, internal).
- Produces:
  - `public enum DesktopHistoryStatus { Ok, NotFound, Unreadable, NoSamples }` (`Ok` is the reader's own success marker; `NoDataReason` in Task 7 only looks at the other three)
  - `public sealed record DesktopHistoryResult(UsageSnapshot? Snapshot, DesktopHistoryStatus Status)`
  - `DesktopUsageReader.Read(string path) : DesktopHistoryResult`
  - `DesktopUsageReader.TryRead(string path) : UsageSnapshot?`
  - `DesktopUsageReader.ReadFirst(IReadOnlyList<string> byFreshness) : DesktopHistoryResult`

- [ ] **Step 1: Write the failing tests**

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class DesktopUsageReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cut-desktop-reader-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string json, string name = "plan-usage-history.json")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    // Real shape, ascending, two samples; the newest has no xu.
    private const string Fixture = """
        {"version":2,"samples":[
          {"t":1785247200000,"org":"11111111-1111-1111-1111-111111111111","u":{"fh":63,"sd":29,"xu":66.68333333333332}},
          {"t":1785247500144,"org":"11111111-1111-1111-1111-111111111111","u":{"fh":64,"sd":29}}
        ]}
        """;

    [Fact]
    public void Fixture_NewestSampleBecomesTheSnapshot()
    {
        var r = DesktopUsageReader.Read(Write(Fixture));

        Assert.Equal(DesktopHistoryStatus.Ok, r.Status);
        var s = Assert.IsType<UsageSnapshot>(r.Snapshot);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1785247500144), s.FetchedAt);
        Assert.Equal(64, s.FiveHour!.Percent);
        Assert.Null(s.FiveHour.ResetsAt);
        Assert.Equal(29, s.SevenDay!.Percent);
        Assert.Null(s.SevenDay.ResetsAt);
        Assert.Null(s.Credits);
        Assert.Empty(s.ScopedLimits);
        Assert.Equal(UsageSource.DesktopHistory, s.Source);
    }

    [Fact]
    public void Descending_MaxByT_Wins()
    {
        var s = DesktopUsageReader.TryRead(Write("""
            {"version":2,"samples":[
              {"t":300,"org":"a","u":{"fh":30,"sd":3}},
              {"t":200,"org":"a","u":{"fh":20,"sd":2}},
              {"t":100,"org":"a","u":{"fh":10,"sd":1}}
            ]}
            """));
        Assert.Equal(30, s!.FiveHour!.Percent);
    }

    [Fact]
    public void Shuffled_MaxByT_Wins()
    {
        var s = DesktopUsageReader.TryRead(Write("""
            {"samples":[
              {"t":200,"u":{"fh":20,"sd":2}},
              {"t":300,"u":{"fh":30,"sd":3}},
              {"t":100,"u":{"fh":10,"sd":1}}
            ]}
            """));
        Assert.Equal(30, s!.FiveHour!.Percent);
    }

    [Fact]
    public void MultiOrg_NewestWinsEvenFromMinorityOrg()
    {
        var s = DesktopUsageReader.TryRead(Write("""
            {"samples":[
              {"t":100,"org":"majority","u":{"fh":10,"sd":1}},
              {"t":200,"org":"majority","u":{"fh":20,"sd":2}},
              {"t":300,"org":"minority","u":{"fh":77,"sd":7}}
            ]}
            """));
        Assert.Equal(77, s!.FiveHour!.Percent);
    }

    [Fact]
    public void Xu_BecomesPercentOnlyCredits()
    {
        var s = DesktopUsageReader.TryRead(Write("""{"samples":[{"t":1,"u":{"fh":1,"sd":1,"xu":66.68333333333332}}]}"""));
        var c = Assert.IsType<CreditUsage>(s!.Credits);
        Assert.Equal(67, c.Percent);
        Assert.Null(c.Used);
        Assert.Null(c.Limit);
        Assert.Null(c.PayloadSeverity);
        Assert.True(c.State.Enabled);
        Assert.False(c.State.LimitReached);
        Assert.Null(c.State.DisabledReason);
    }

    [Fact]
    public void Xu_Integer_IsAccepted()
    {
        var s = DesktopUsageReader.TryRead(Write("""{"samples":[{"t":1,"u":{"fh":1,"sd":1,"xu":100}}]}"""));
        Assert.Equal(100, s!.Credits!.Percent);
    }

    [Fact]
    public void MissingFh_LeavesFiveHourNull()
    {
        var s = DesktopUsageReader.TryRead(Write("""{"samples":[{"t":1,"u":{"sd":17}}]}"""));
        Assert.Null(s!.FiveHour);
        Assert.Equal(17, s.SevenDay!.Percent);
    }

    [Fact]
    public void BadSamplesAreSkipped_ValidSiblingStillSelected()
    {
        var s = DesktopUsageReader.TryRead(Write("""
            {"samples":[
              "not an object",
              {"u":{"fh":99,"sd":99}},
              {"t":"abc","u":{"fh":98,"sd":98}},
              {"t":99999999999999999999,"u":{"fh":97,"sd":97}},
              {"t":253402300800000,"u":{"fh":96,"sd":96}},
              {"t":500,"org":"a"},
              {"t":500,"org":"a","u":"nope"},
              {"t":400,"org":"a","u":{"fh":40,"sd":4}}
            ]}
            """));
        Assert.Equal(40, s!.FiveHour!.Percent);
    }

    [Fact]
    public void EmptySamples_IsNoSamples()
    {
        var r = DesktopUsageReader.Read(Write("""{"version":2,"samples":[]}"""));
        Assert.Null(r.Snapshot);
        Assert.Equal(DesktopHistoryStatus.NoSamples, r.Status);
    }

    [Fact]
    public void OnlyUnusableSamples_IsNoSamples()
        => Assert.Equal(DesktopHistoryStatus.NoSamples,
            DesktopUsageReader.Read(Write("""{"samples":[{"org":"a"},{"t":1}]}""")).Status);

    [Fact]
    public void SamplesNotAnArray_IsUnreadable()
        => Assert.Equal(DesktopHistoryStatus.Unreadable,
            DesktopUsageReader.Read(Write("""{"samples":{"t":1}}""")).Status);

    [Fact]
    public void NoSamplesKey_IsUnreadable()
        => Assert.Equal(DesktopHistoryStatus.Unreadable,
            DesktopUsageReader.Read(Write("""{"version":2}""")).Status);

    [Fact]
    public void MalformedJson_IsUnreadable()
        => Assert.Equal(DesktopHistoryStatus.Unreadable,
            DesktopUsageReader.Read(Write("{ not json !!")).Status);

    [Fact]
    public void MissingFile_IsNotFound()
    {
        var r = DesktopUsageReader.Read(Path.Combine(_dir, "nope.json"));
        Assert.Null(r.Snapshot);
        Assert.Equal(DesktopHistoryStatus.NotFound, r.Status);
    }

    [Fact]
    public void OversizeFile_IsUnreadable()
    {
        var path = Path.Combine(_dir, "big.json");
        using (var f = File.Create(path)) f.SetLength(16 * 1024 * 1024 + 1);
        Assert.Equal(DesktopHistoryStatus.Unreadable, DesktopUsageReader.Read(path).Status);
    }

    [Fact]
    public void UnknownVersion_StillParsed()
    {
        var s = DesktopUsageReader.TryRead(Write("""{"version":3,"samples":[{"t":1,"u":{"fh":5,"sd":6}}]}"""));
        Assert.Equal(5, s!.FiveHour!.Percent);
    }

    // ---- ReadFirst ----

    [Fact]
    public void ReadFirst_SkipsAMalformedNewerFile()
    {
        var broken = Write("{ half-written", "newer.json");
        var good = Write(Fixture, "older.json");
        var r = DesktopUsageReader.ReadFirst([broken, good]);
        Assert.Equal(DesktopHistoryStatus.Ok, r.Status);
        Assert.Equal(64, r.Snapshot!.FiveHour!.Percent);
    }

    [Fact]
    public void ReadFirst_AllFail_ReportsTheNewestExistingFilesStatus()
    {
        var empty = Write("""{"samples":[]}""", "newer.json");
        var broken = Write("{", "older.json");
        var r = DesktopUsageReader.ReadFirst([empty, broken]);
        Assert.Null(r.Snapshot);
        Assert.Equal(DesktopHistoryStatus.NoSamples, r.Status);
    }

    [Fact]
    public void ReadFirst_Empty_IsNotFound()
        => Assert.Equal(DesktopHistoryStatus.NotFound, DesktopUsageReader.ReadFirst([]).Status);

    [Fact]
    public void ReadFirst_OnlyMissingFiles_IsNotFound()
        => Assert.Equal(DesktopHistoryStatus.NotFound,
            DesktopUsageReader.ReadFirst([Path.Combine(_dir, "a.json"), Path.Combine(_dir, "b.json")]).Status);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~DesktopUsageReaderTests`
Expected: build error, `DesktopUsageReader` does not exist.

- [ ] **Step 3: Implement**

`src/ClaudeUsageTray/Core/DesktopUsageReader.cs`:

```csharp
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Why a read of the desktop history produced no snapshot, for the no-data message.
/// Ok is the reader's own success marker.</summary>
public enum DesktopHistoryStatus { Ok, NotFound, Unreadable, NoSamples }

public sealed record DesktopHistoryResult(UsageSnapshot? Snapshot, DesktopHistoryStatus Status);

/// <summary>
/// Read-only parse of the Claude Desktop app's plan-usage-history.json. Field semantics are
/// inferred from observation on three machines, not documented: <c>u.fh</c> is the five-hour
/// utilization, <c>u.sd</c> the seven-day one, <c>u.xu</c> the extra-usage (credits) utilization,
/// present only while credits are enabled. There are no reset timestamps, so every window is
/// emitted with ResetsAt null and pace colouring falls back to the absolute thresholds.
/// The newest sample by <c>t</c> wins; array order and <c>org</c> are ignored (samples from a
/// second org appear after an org switch, and the newest is still the current one).
/// Never throws for IO/JSON errors.
/// </summary>
public static class DesktopUsageReader
{
    // The largest observed file is 172 KB; this guards against reading something else by mistake.
    private const long MaxBytes = 16 * 1024 * 1024;

    // DateTimeOffset.FromUnixTimeMilliseconds bounds; anything outside is not a timestamp.
    private const long MinUnixMs = -62_135_596_800_000;
    private const long MaxUnixMs = 253_402_300_799_999;

    public static UsageSnapshot? TryRead(string path) => Read(path).Snapshot;

    public static DesktopHistoryResult Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(null, DesktopHistoryStatus.NotFound);
            if (new FileInfo(path).Length > MaxBytes) return new(null, DesktopHistoryStatus.Unreadable);
            // FileShare.ReadWrite: the desktop app may be appending while we read.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("samples", out var samples)
                || samples.ValueKind != JsonValueKind.Array)
                return new(null, DesktopHistoryStatus.Unreadable);

            // One pass, max-by-t. Ascending order has held on every machine measured, but nobody
            // guarantees it, and the pass costs nothing at a few thousand elements.
            JsonElement? newest = null;
            long newestT = long.MinValue;
            foreach (var sample in samples.EnumerateArray())
            {
                if (sample.ValueKind != JsonValueKind.Object) continue;
                if (!sample.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number
                    || !t.TryGetInt64(out var ms) || ms < MinUnixMs || ms > MaxUnixMs) continue;
                if (!sample.TryGetProperty("u", out var u) || u.ValueKind != JsonValueKind.Object) continue;
                if (newest is null || ms > newestT) { newest = u; newestT = ms; }
            }
            if (newest is not { } usage) return new(null, DesktopHistoryStatus.NoSamples);

            var five = UsageJson.ReadRoundedPercent(usage, "fh") is { } fh ? new WindowUsage(fh, null) : null;
            var seven = UsageJson.ReadRoundedPercent(usage, "sd") is { } sd ? new WindowUsage(sd, null) : null;
            // Percent-only credits: no money, and no state the file could tell us about. This is the
            // same shape the legacy extra_usage block produces, which the credit row already renders.
            var credits = UsageJson.ReadRoundedPercent(usage, "xu") is { } xu
                ? new CreditUsage(null, null, xu, null, new CreditState(Enabled: true, null, LimitReached: false))
                : null;

            var snapshot = new UsageSnapshot(DateTimeOffset.FromUnixTimeMilliseconds(newestT), five, seven, [], credits)
            {
                Source = UsageSource.DesktopHistory,
            };
            return new(snapshot, DesktopHistoryStatus.Ok);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new(null, DesktopHistoryStatus.Unreadable);
        }
    }

    /// <summary>Reads the candidates in the given order (newest first, see
    /// <see cref="DesktopHistoryPath.ByFreshness"/>) and returns the first usable snapshot, so a
    /// half-written newer file cannot mask an older good one. When none is usable, the status is the
    /// newest existing candidate's; NotFound when no candidate exists at all.</summary>
    public static DesktopHistoryResult ReadFirst(IReadOnlyList<string> byFreshness)
    {
        DesktopHistoryResult? firstFailure = null;
        foreach (var path in byFreshness)
        {
            var result = Read(path);
            if (result.Snapshot is not null) return result;
            if (result.Status != DesktopHistoryStatus.NotFound) firstFailure ??= result;
        }
        return firstFailure ?? new(null, DesktopHistoryStatus.NotFound);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~DesktopUsageReaderTests`
Expected: 20 passed. `BadSamplesAreSkipped_ValidSiblingStillSelected` depends on `TryGetInt64` rejecting `99999999999999999999` and the range check rejecting `253402300800000`; both are skipped, so the sample at `t=400` wins.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/DesktopUsageReader.cs tests/ClaudeUsageTray.Tests/DesktopUsageReaderTests.cs
git commit -m "feat(desktop-history): parse plan-usage-history.json into a percentages-only snapshot

Refs #5

Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 5: `SourceSelection`

**Files:**
- Create: `src/ClaudeUsageTray/Core/SourceSelection.cs`
- Test: `tests/ClaudeUsageTray.Tests/SourceSelectionTests.cs` (create)

**Interfaces:**
- Consumes: `Settings.StalenessMinutes`, `Settings.DesktopStalenessHours` (Task 2).
- Produces:
  - `public sealed record DisplayChoice(UsageSnapshot? Snapshot, bool Stale)`
  - `SourceSelection.Choose(UsageSnapshot? cli, UsageSnapshot? desktop, DateTimeOffset now, Settings settings) : DisplayChoice`
  - `SourceSelection.Age(UsageSnapshot snapshot, DateTimeOffset now) : TimeSpan` (future beyond tolerance → `TimeSpan.MaxValue`; small skew → zero)
  - `SourceSelection.FutureTolerance` (5 min)
  - Tasks 9 and 10 use `DisplayChoice`, `Choose` and `Age`.

- [ ] **Step 1: Write the failing tests**

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class SourceSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Settings Defaults = new(); // 15 min, 3 h

    private static UsageSnapshot Cli(TimeSpan age)
        => new(Now - age, new WindowUsage(10, Now.AddHours(1)), new WindowUsage(20, Now.AddDays(1)));

    private static UsageSnapshot Desktop(TimeSpan age)
        => new(Now - age, new WindowUsage(11, null), new WindowUsage(21, null)) { Source = UsageSource.DesktopHistory };

    [Fact]
    public void FreshCli_BeatsFreshDesktop()
    {
        var choice = SourceSelection.Choose(Cli(TimeSpan.FromMinutes(2)), Desktop(TimeSpan.FromMinutes(1)), Now, Defaults);
        Assert.Equal(UsageSource.ClaudeCode, choice.Snapshot!.Source);
        Assert.False(choice.Stale);
    }

    [Fact]
    public void StaleCli_YieldsToFreshDesktop()
    {
        var choice = SourceSelection.Choose(Cli(TimeSpan.FromDays(7)), Desktop(TimeSpan.FromMinutes(80)), Now, Defaults);
        Assert.Equal(UsageSource.DesktopHistory, choice.Snapshot!.Source);
        Assert.False(choice.Stale);
    }

    [Fact]
    public void DesktopAllowance_IsHours_NotStalenessMinutes()
    {
        // 80 min is far past 15 min but well inside 3 h.
        var choice = SourceSelection.Choose(null, Desktop(TimeSpan.FromMinutes(80)), Now, Defaults);
        Assert.False(choice.Stale);

        var tight = new Settings { DesktopStalenessHours = 1 };
        Assert.True(SourceSelection.Choose(null, Desktop(TimeSpan.FromMinutes(80)), Now, tight).Stale);
    }

    [Fact]
    public void BothStale_NewerWins_Flagged()
    {
        var choice = SourceSelection.Choose(Cli(TimeSpan.FromHours(5)), Desktop(TimeSpan.FromHours(4)), Now, Defaults);
        Assert.Equal(UsageSource.DesktopHistory, choice.Snapshot!.Source);
        Assert.True(choice.Stale);

        choice = SourceSelection.Choose(Cli(TimeSpan.FromHours(4)), Desktop(TimeSpan.FromHours(5)), Now, Defaults);
        Assert.Equal(UsageSource.ClaudeCode, choice.Snapshot!.Source);
        Assert.True(choice.Stale);
    }

    [Fact]
    public void OnlyCli_Stale_IsShownFlagged()
    {
        var choice = SourceSelection.Choose(Cli(TimeSpan.FromHours(1)), null, Now, Defaults);
        Assert.Equal(UsageSource.ClaudeCode, choice.Snapshot!.Source);
        Assert.True(choice.Stale);
    }

    [Fact]
    public void OnlyDesktop_Stale_IsShownFlagged()
    {
        var choice = SourceSelection.Choose(null, Desktop(TimeSpan.FromDays(20)), Now, Defaults);
        Assert.Equal(UsageSource.DesktopHistory, choice.Snapshot!.Source);
        Assert.True(choice.Stale);
    }

    [Fact]
    public void Neither_IsNullNotStale()
    {
        var choice = SourceSelection.Choose(null, null, Now, Defaults);
        Assert.Null(choice.Snapshot);
        Assert.False(choice.Stale);
    }

    [Fact]
    public void ExactlyAtTheCutoff_IsStillFresh()
        => Assert.False(SourceSelection.Choose(Cli(TimeSpan.FromMinutes(15)), null, Now, Defaults).Stale);

    // ---- clock skew ----

    [Fact]
    public void FourMinutesInTheFuture_CountsAsFresh()
    {
        var choice = SourceSelection.Choose(null, Desktop(TimeSpan.FromMinutes(-4)), Now, Defaults);
        Assert.False(choice.Stale);
        Assert.Equal(TimeSpan.Zero, SourceSelection.Age(choice.Snapshot!, Now));
    }

    [Fact]
    public void AnHourInTheFuture_IsStale_AndLosesToAFreshAlternative()
    {
        var future = Desktop(TimeSpan.FromHours(-1));
        Assert.Equal(TimeSpan.MaxValue, SourceSelection.Age(future, Now));

        var choice = SourceSelection.Choose(Cli(TimeSpan.FromMinutes(10)), future, Now, Defaults);
        Assert.Equal(UsageSource.ClaudeCode, choice.Snapshot!.Source);
        Assert.False(choice.Stale);

        Assert.True(SourceSelection.Choose(null, future, Now, Defaults).Stale);
    }

    [Fact]
    public void BothStale_FutureOneLosesToAnOldRealOne()
    {
        var choice = SourceSelection.Choose(Cli(TimeSpan.FromDays(2)), Desktop(TimeSpan.FromHours(-1)), Now, Defaults);
        Assert.Equal(UsageSource.ClaudeCode, choice.Snapshot!.Source);
        Assert.True(choice.Stale);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SourceSelectionTests`
Expected: build error, `SourceSelection` does not exist.

- [ ] **Step 3: Implement**

`src/ClaudeUsageTray/Core/SourceSelection.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>What the icons and the popup show, and whether to mark it stale.</summary>
public sealed record DisplayChoice(UsageSnapshot? Snapshot, bool Stale);

/// <summary>Fallback-only precedence between Claude Code's data (cache + live, already merged by
/// <see cref="SnapshotPrecedence"/>) and the Claude Desktop history. A current Claude Code snapshot
/// always wins: it is the richer one (reset times, scoped limits, money). The desktop history steps
/// in when Claude Code's is absent or past its cutoff — which is what fixes the desktop-only case,
/// and what replaces a Claude Code cache frozen for days by design rather than by accident. Each
/// source has its own allowance, because their cadences differ by an order of magnitude.</summary>
public static class SourceSelection
{
    /// <summary>Clock skew up to this counts as an age of zero. Beyond it the timestamp is not
    /// trusted, and the snapshot can only ever be shown as stale.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public static DisplayChoice Choose(UsageSnapshot? cli, UsageSnapshot? desktop, DateTimeOffset now, Settings settings)
    {
        if (cli is not null && Age(cli, now) <= TimeSpan.FromMinutes(settings.StalenessMinutes))
            return new(cli, false);
        if (desktop is not null && Age(desktop, now) <= TimeSpan.FromHours(settings.DesktopStalenessHours))
            return new(desktop, false);

        // Both stale, or only one present: a dead source degrades to stale, never to blank.
        if (cli is null && desktop is null) return new(null, false);
        if (cli is null) return new(desktop, true);
        if (desktop is null) return new(cli, true);
        return new(Age(desktop, now) < Age(cli, now) ? desktop : cli, true);
    }

    /// <summary>now - FetchedAt, clamped: small future skew is zero, anything further in the future
    /// is TimeSpan.MaxValue so it fails every freshness test and loses every "newer" comparison.</summary>
    public static TimeSpan Age(UsageSnapshot snapshot, DateTimeOffset now)
    {
        var age = now - snapshot.FetchedAt;
        if (age < -FutureTolerance) return TimeSpan.MaxValue;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~SourceSelectionTests`
Expected: 11 passed.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/SourceSelection.cs tests/ClaudeUsageTray.Tests/SourceSelectionTests.cs
git commit -m "feat(desktop-history): fallback-only source selection with per-source staleness

Refs #5

Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 6: Status probes on the existing readers

**Files:**
- Create: `src/ClaudeUsageTray/Core/NoDataReason.cs` (enums only in this task; `Describe` comes in Task 7)
- Modify: `src/ClaudeUsageTray/Core/UsageCacheReader.cs`
- Modify: `src/ClaudeUsageTray/Core/CredentialsReader.cs`
- Test: `tests/ClaudeUsageTray.Tests/UsageCacheReaderTests.cs`, `tests/ClaudeUsageTray.Tests/CredentialsReaderTests.cs`

**Interfaces:**
- Produces:
  - `public enum ConfigStatus { Missing, NoUsageKey, Unreadable }`
  - `public enum CredentialStatus { Missing, Unusable, Valid }`
  - `UsageCacheReader.Status(string path) : ConfigStatus`
  - `CredentialsReader.Status(string path, DateTimeOffset now) : CredentialStatus`
  - Tasks 7 and 10 use all four.

- [ ] **Step 1: Write the failing tests**

Append inside `UsageCacheReaderTests`:

```csharp
    [Fact]
    public void Status_MissingFile_IsMissing()
        => Assert.Equal(ConfigStatus.Missing, UsageCacheReader.Status(Path.Combine(_dir, "nope.json")));

    [Fact]
    public void Status_FileWithoutTheKey_IsNoUsageKey()
        => Assert.Equal(ConfigStatus.NoUsageKey,
            UsageCacheReader.Status(WriteFixture("""{ "oauthAccount": {}, "userID": "x" }""")));

    [Fact]
    public void Status_FileWithTheKey_IsUnreadable()
    {
        // Status is only consulted when TryRead returned null, so "key present" means it did not parse.
        Assert.Equal(ConfigStatus.Unreadable,
            UsageCacheReader.Status(WriteFixture("""{ "cachedUsageUtilization": { "utilization": {} } }""")));
    }

    [Fact]
    public void Status_MalformedFileWithTheKey_IsUnreadable()
        => Assert.Equal(ConfigStatus.Unreadable,
            UsageCacheReader.Status(WriteFixture("""{ "cachedUsageUtilization": { not json""")));

    [Fact]
    public void Status_MalformedFileWithoutTheKey_IsNoUsageKey()
        => Assert.Equal(ConfigStatus.NoUsageKey, UsageCacheReader.Status(WriteFixture("{ not json !!")));
```

Append inside `CredentialsReaderTests`:

```csharp
    [Fact]
    public void Status_MissingFile_IsMissing()
        => Assert.Equal(CredentialStatus.Missing,
            CredentialsReader.Status(Path.Combine(_dir, "nope.json"), Now));

    [Fact]
    public void Status_ValidToken_IsValid()
        => Assert.Equal(CredentialStatus.Valid, CredentialsReader.Status(
            WriteFixture(CredsJson("dummy-token-abc", Now.AddHours(2).ToUnixTimeMilliseconds())), Now));

    [Fact]
    public void Status_ExpiredToken_IsUnusable()
        => Assert.Equal(CredentialStatus.Unusable, CredentialsReader.Status(
            WriteFixture(CredsJson("dummy-token-abc", Now.AddMinutes(-1).ToUnixTimeMilliseconds())), Now));

    [Fact]
    public void Status_MalformedFile_IsUnusable()
        => Assert.Equal(CredentialStatus.Unusable, CredentialsReader.Status(WriteFixture("{ nope"), Now));
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~UsageCacheReaderTests|FullyQualifiedName~CredentialsReaderTests"`
Expected: build error, `ConfigStatus` does not exist.

- [ ] **Step 3: Add the enums**

Create `src/ClaudeUsageTray/Core/NoDataReason.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>State of ~\.claude.json when the cache reader produced no snapshot. Unreadable means the
/// usage key is present but did not parse — the status is only consulted after TryRead returned null.</summary>
public enum ConfigStatus { Missing, NoUsageKey, Unreadable }

/// <summary>State of ~\.claude\.credentials.json. Unusable: the file exists but yields no valid token
/// (expired, near expiry, malformed).</summary>
public enum CredentialStatus { Missing, Unusable, Valid }
```

- [ ] **Step 4: Add `UsageCacheReader.Status`**

In `UsageCacheReader`, after `TryRead`:

```csharp
    /// <summary>Why <see cref="TryRead"/> may have returned null, for the no-data message. A guarded
    /// string search rather than a parse: the file is Claude Code's and can be mid-rewrite.</summary>
    public static ConfigStatus Status(string path)
    {
        try
        {
            if (!File.Exists(path)) return ConfigStatus.Missing;
            var info = new FileInfo(path);
            if (info.Length > 32 * 1024 * 1024) return ConfigStatus.Unreadable;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains("\"cachedUsageUtilization\"", StringComparison.Ordinal)
                ? ConfigStatus.Unreadable
                : ConfigStatus.NoUsageKey;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return ConfigStatus.Unreadable;
        }
    }
```

- [ ] **Step 5: Add `CredentialsReader.Status`**

In `CredentialsReader`, after `TryReadAccessToken`:

```csharp
    /// <summary>Existence plus the same validation <see cref="TryReadAccessToken"/> applies. The token
    /// itself is not returned or retained.</summary>
    public static CredentialStatus Status(string path, DateTimeOffset now)
    {
        bool exists;
        try { exists = File.Exists(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { return CredentialStatus.Unusable; }
        if (!exists) return CredentialStatus.Missing;
        return TryReadAccessToken(path, now) is null ? CredentialStatus.Unusable : CredentialStatus.Valid;
    }
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~UsageCacheReaderTests|FullyQualifiedName~CredentialsReaderTests"`
Expected: all passed, including the nine new ones.

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Core/NoDataReason.cs src/ClaudeUsageTray/Core/UsageCacheReader.cs src/ClaudeUsageTray/Core/CredentialsReader.cs tests/ClaudeUsageTray.Tests/UsageCacheReaderTests.cs tests/ClaudeUsageTray.Tests/CredentialsReaderTests.cs
git commit -m "feat(desktop-history): report why the cache and credentials readers came up empty

Refs #5

Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 7: `NoDataReason.Describe`

**Files:**
- Modify: `src/ClaudeUsageTray/Core/NoDataReason.cs`
- Test: `tests/ClaudeUsageTray.Tests/NoDataReasonTests.cs` (create)

**Interfaces:**
- Consumes: `ConfigStatus`, `CredentialStatus` (Task 6), `DesktopHistoryStatus` (Task 4).
- Produces: `public sealed record NoDataFacts(ConfigStatus Config, CredentialStatus Credentials, DesktopHistoryStatus Desktop)`, `NoDataReason.Describe(NoDataFacts) : string`, `NoDataReason.Default : string` (the "open Claude Code or Claude Desktop" line). Tasks 9 and 10 use them.

- [ ] **Step 1: Write the failing tests**

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class NoDataReasonTests
{
    private static string Describe(ConfigStatus config, CredentialStatus creds, DesktopHistoryStatus desktop)
        => NoDataReason.Describe(new NoDataFacts(config, creds, desktop));

    [Fact]
    public void DesktopNoSamples_NamesTheDesktopFile()
        => Assert.Equal("Claude Desktop history found, but no samples yet.",
            Describe(ConfigStatus.Missing, CredentialStatus.Missing, DesktopHistoryStatus.NoSamples));

    [Fact]
    public void DesktopUnreadable_NamesTheDesktopFile()
        => Assert.Equal("Claude Desktop history found, but it could not be read.",
            Describe(ConfigStatus.Missing, CredentialStatus.Missing, DesktopHistoryStatus.Unreadable));

    [Fact]
    public void NothingAnywhere_TellsTheUserWhatToOpen()
    {
        var text = Describe(ConfigStatus.Missing, CredentialStatus.Missing, DesktopHistoryStatus.NotFound);
        Assert.Equal("No usage data yet — open Claude Code or Claude Desktop.", text);
        Assert.Equal(NoDataReason.Default, text);
    }

    [Fact]
    public void NoKey_NoCredentials_IsTheDesktopBundledCliCase()
        => Assert.Equal("Claude Code has not cached usage data, and there is no credentials file for a live fetch.",
            Describe(ConfigStatus.NoUsageKey, CredentialStatus.Missing, DesktopHistoryStatus.NotFound));

    [Fact]
    public void NoKey_UnusableCredentials()
        => Assert.Equal("Claude Code has not cached usage data, and its credentials are not usable for a live fetch.",
            Describe(ConfigStatus.NoUsageKey, CredentialStatus.Unusable, DesktopHistoryStatus.NotFound));

    [Fact]
    public void NoKey_ValidCredentials_IsTransient()
        => Assert.Equal("Claude Code has not cached usage data yet — waiting for the first live fetch.",
            Describe(ConfigStatus.NoUsageKey, CredentialStatus.Valid, DesktopHistoryStatus.NotFound));

    [Fact]
    public void KeyPresentButUnparsable()
        => Assert.Equal("Claude Code's cached usage data could not be read.",
            Describe(ConfigStatus.Unreadable, CredentialStatus.Valid, DesktopHistoryStatus.NotFound));

    /// <summary>The desktop rows come first: a found-but-empty desktop file is the more specific fact.</summary>
    [Fact]
    public void DesktopFacts_TakePrecedenceOverConfigFacts()
        => Assert.StartsWith("Claude Desktop history found",
            Describe(ConfigStatus.NoUsageKey, CredentialStatus.Missing, DesktopHistoryStatus.NoSamples));

    /// <summary>Only one state tells the user to run anything.</summary>
    [Theory]
    [InlineData(ConfigStatus.NoUsageKey, CredentialStatus.Missing, DesktopHistoryStatus.NotFound)]
    [InlineData(ConfigStatus.NoUsageKey, CredentialStatus.Valid, DesktopHistoryStatus.NotFound)]
    [InlineData(ConfigStatus.Unreadable, CredentialStatus.Missing, DesktopHistoryStatus.NotFound)]
    [InlineData(ConfigStatus.Missing, CredentialStatus.Missing, DesktopHistoryStatus.NoSamples)]
    public void OtherStates_DoNotSayOpen(ConfigStatus c, CredentialStatus k, DesktopHistoryStatus d)
        => Assert.DoesNotContain("open ", Describe(c, k, d));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~NoDataReasonTests`
Expected: build error, `NoDataFacts` does not exist.

- [ ] **Step 3: Implement**

Append to `src/ClaudeUsageTray/Core/NoDataReason.cs`:

```csharp
public sealed record NoDataFacts(ConfigStatus Config, CredentialStatus Credentials, DesktopHistoryStatus Desktop);

/// <summary>The one sentence shown when no source produced a snapshot. Names what is missing
/// instead of assuming Claude Code: on a desktop-only machine ~\.claude.json exists (the desktop
/// app installs its own Claude Code), so "run Claude Code" was never the right hint there.
/// Desktop facts come first because a found-but-empty file is the more specific one.</summary>
public static class NoDataReason
{
    public const string Default = "No usage data yet — open Claude Code or Claude Desktop.";

    public static string Describe(NoDataFacts f)
    {
        if (f.Desktop == DesktopHistoryStatus.NoSamples) return "Claude Desktop history found, but no samples yet.";
        if (f.Desktop == DesktopHistoryStatus.Unreadable) return "Claude Desktop history found, but it could not be read.";
        return f.Config switch
        {
            ConfigStatus.Missing => Default,
            ConfigStatus.NoUsageKey => f.Credentials switch
            {
                CredentialStatus.Missing =>
                    "Claude Code has not cached usage data, and there is no credentials file for a live fetch.",
                CredentialStatus.Unusable =>
                    "Claude Code has not cached usage data, and its credentials are not usable for a live fetch.",
                _ => "Claude Code has not cached usage data yet — waiting for the first live fetch.",
            },
            _ => "Claude Code's cached usage data could not be read.",
        };
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~NoDataReasonTests`
Expected: 12 passed.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/NoDataReason.cs tests/ClaudeUsageTray.Tests/NoDataReasonTests.cs
git commit -m "feat(desktop-history): say what is missing instead of always 'run Claude Code'

Refs #5

Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 8: Settings dialog spinner

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/SettingsDialog.cs`
- Test: `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`

**Interfaces:**
- Consumes: `Settings.DesktopStalenessHours`, `Settings.DesktopHistoryPathOverride`, `ThresholdRules.DefaultDesktopStalenessHours` (Task 2).
- Produces: a `NumericUpDown` named `desktopStaleness` reachable via `Controls.Find`.

- [ ] **Step 1: Write the failing tests**

Append inside `SettingsDialogTests` (the class already has `Dialog(...)`, `Spinner(...)` and `Button(...)` helpers):

```csharp
    [Fact]
    public void DesktopStaleness_RoundTripsThroughTheDraft()
    {
        var dialog = Dialog(new Settings { DesktopStalenessHours = 6, DesktopHistoryPathOverride = @"C:\alt\h.json" });
        Assert.Equal(6, Spinner(dialog, "desktopStaleness").Value);

        Spinner(dialog, "desktopStaleness").Value = 12;
        var draft = dialog.Draft();
        Assert.Equal(12, draft.DesktopStalenessHours);
        Assert.Equal(@"C:\alt\h.json", draft.DesktopHistoryPathOverride); // file-only, carried through untouched
    }

    [Fact]
    public void DesktopStaleness_SpinnerRangeIsOneToOneSixtyEight()
    {
        var spinner = Spinner(Dialog(new Settings()), "desktopStaleness");
        Assert.Equal(1, spinner.Minimum);
        Assert.Equal(168, spinner.Maximum);
    }

    [Fact]
    public void Reset_RestoresDesktopStaleness()
    {
        var dialog = Dialog(new Settings { DesktopStalenessHours = 48 });
        Button(dialog, "reset").PerformClick();
        Assert.Equal(ThresholdRules.DefaultDesktopStalenessHours, dialog.Draft().DesktopStalenessHours);
    }

    [Fact]
    public void DesktopStaleness_DoesNotTouchTheLiveSettings()
    {
        var live = new Settings { DesktopStalenessHours = 3 };
        Spinner(Dialog(live), "desktopStaleness").Value = 24;
        Assert.Equal(3, live.DesktopStalenessHours);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SettingsDialogTests`
Expected: the four new tests fail (`Controls.Find("desktopStaleness")` returns nothing → `Single()` throws).

- [ ] **Step 3: Add the spinner**

In `SettingsDialog`, after the `_staleness` field:

```csharp
    private readonly NumericUpDown _desktopStaleness = new() { Name = "desktopStaleness", Minimum = 1, Maximum = 168, Width = 60 };
```

In `BuildLayout()`, replace the single `Refresh` spinner row:

```csharp
        layout.Controls.Add(Heading("Refresh"));
        layout.Controls.Add(Spinners(
            ("Treat data as stale after", _staleness, "minutes"),
            ("Claude Desktop history stale after", _desktopStaleness, "hours")));
```

In the tab-order array, insert `_desktopStaleness` right after `_staleness`:

```csharp
                 { _modeFive, _modeSeven, _modeBoth, _startup, _orange, _red, _paceColors, _staleness,
                   _desktopStaleness, _betaReleases, reset, cancel, save })
```

In `LoadFrom`, change the last line to pass the new value:

```csharp
        SetThresholds(source.Thresholds.Orange, source.Thresholds.Red, source.StalenessMinutes,
            source.DesktopStalenessHours);
```

In `ResetThresholdsToDefaults`:

```csharp
        SetThresholds(ThresholdRules.DefaultOrange, ThresholdRules.DefaultRed,
            ThresholdRules.DefaultStalenessMinutes, ThresholdRules.DefaultDesktopStalenessHours);
```

Change `SetThresholds` to take and assign the fourth value:

```csharp
    private void SetThresholds(int orange, int red, int stalenessMinutes, int desktopStalenessHours)
    {
        // ... existing body unchanged up to the _staleness assignment, then:
        _staleness.Value = Math.Clamp(stalenessMinutes, (int)_staleness.Minimum, (int)_staleness.Maximum);
        _desktopStaleness.Value = Math.Clamp(desktopStalenessHours,
            (int)_desktopStaleness.Minimum, (int)_desktopStaleness.Maximum);
        _suspendSync = false;
        SyncRangesAndPreview();
    }
```

In `Draft()`, after the `StalenessMinutes` line:

```csharp
        draft.DesktopStalenessHours = (int)_desktopStaleness.Value;
```

In `Clone`, add both new fields:

```csharp
        StalenessMinutes = source.StalenessMinutes,
        DesktopStalenessHours = source.DesktopStalenessHours,
        // ...
        ConfigPathOverride = source.ConfigPathOverride,
        DesktopHistoryPathOverride = source.DesktopHistoryPathOverride,
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~SettingsDialog`
Expected: all passed (both `SettingsDialogTests` and `SettingsDialogUpdateTests`).

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Tray/SettingsDialog.cs tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs
git commit -m "feat(desktop-history): desktop staleness spinner in the settings dialog

Refs #5

Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 9: `UsagePopup` takes a `DisplayChoice`

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/UsagePopup.cs`
- Test: `tests/ClaudeUsageTray.Tests/UsagePopupWidthTests.cs`, `tests/ClaudeUsageTray.Tests/UsagePopupSourceTests.cs` (create)

**Interfaces:**
- Consumes: `DisplayChoice` (Task 5), `UsageSource` (Task 1), `NoDataReason.Default` (Task 7).
- Produces: `new UsagePopup(DisplayChoice choice, Settings settings, DateTimeOffset now, PlatformStatus? platformStatus = null, string? lastFetchStatus = null, string? noDataText = null)`. Task 10 calls it with the real selection. Changing the signature breaks the one call in `TrayApp.ShowPopup`, so Step 3b makes the minimal one-line change there to keep the solution compiling; Task 10 replaces that line.

- [ ] **Step 1: Update the existing width tests and write the new ones**

In `UsagePopupWidthTests`, change the three constructor calls so the snapshot is wrapped:

```csharp
        var popup = new UsagePopup(new DisplayChoice(Snapshot, false), new Settings(), Now, status);
```

```csharp
        var baselinePopup = new UsagePopup(new DisplayChoice(Snapshot, false), new Settings(), Now,
            Degraded("Elevated errors", Incident("Short")));
```

```csharp
        var widePopup = new UsagePopup(new DisplayChoice(Snapshot, false), new Settings(), Now, Degraded("Elevated errors", Incident(
```

Create `tests/ClaudeUsageTray.Tests/UsagePopupSourceTests.cs`:

```csharp
using System.Windows.Forms;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>What the popup says about where its numbers came from, and what it says when there are none.</summary>
public class UsagePopupSourceTests : IDisposable
{
    private readonly List<UsagePopup> _open = [];
    public void Dispose() { foreach (var popup in _open) popup.Dispose(); }

    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private UsagePopup Popup(DisplayChoice choice, string? noDataText = null)
    {
        var popup = new UsagePopup(choice, new Settings(), Now, null, null, noDataText);
        _open.Add(popup);
        popup.PerformLayout();
        return popup;
    }

    private static IEnumerable<string> Texts(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Label l) yield return l.Text;
            foreach (var t in Texts(c)) yield return t;
        }
    }

    private static UsageSnapshot Desktop(TimeSpan age, WindowUsage? five, WindowUsage? seven, CreditUsage? credits = null)
        => new(Now - age, five, seven, [], credits) { Source = UsageSource.DesktopHistory };

    [Fact]
    public void DesktopSource_NamesItselfInTheUpdatedLine()
    {
        var popup = Popup(new DisplayChoice(Desktop(TimeSpan.FromMinutes(40), new(7, null), new(17, null)), false));
        Assert.Contains("Claude Desktop history · updated 40m ago", Texts(popup));
    }

    [Fact]
    public void DesktopSource_Stale_IsFlagged()
    {
        var popup = Popup(new DisplayChoice(Desktop(TimeSpan.FromHours(5), new(7, null), new(17, null)), true));
        Assert.Contains("Claude Desktop history · updated 5h ago · stale", Texts(popup));
    }

    [Fact]
    public void ClaudeCodeSource_KeepsTheExistingWording()
    {
        var cli = new UsageSnapshot(Now.AddMinutes(-2), new(7, Now.AddHours(1)), new(17, Now.AddDays(1)));
        var popup = Popup(new DisplayChoice(cli, false));
        Assert.Contains("Last updated 2m ago", Texts(popup));
    }

    [Fact]
    public void StaleFlag_ComesFromTheChoice_NotFromStalenessMinutes()
    {
        // 40 min is past the 15 min default, but the choice says fresh — the desktop allowance decided.
        var popup = Popup(new DisplayChoice(Desktop(TimeSpan.FromMinutes(40), new(7, null), new(17, null)), false));
        Assert.DoesNotContain(Texts(popup), t => t.EndsWith("· stale"));
    }

    [Fact]
    public void DesktopSource_OneWindowNullAndPercentOnlyCredits_Render()
    {
        var credits = new CreditUsage(null, null, 67, null, new CreditState(true, null, false));
        var popup = Popup(new DisplayChoice(Desktop(TimeSpan.FromMinutes(1), null, new(17, null), credits), false));
        var texts = Texts(popup).ToList();
        Assert.Contains("5-hour window: no data", texts);
        Assert.Contains(texts, t => t.StartsWith("7-day window — 17%"));
        Assert.Contains("Credits — 67%", texts);
    }

    [Fact]
    public void NoData_ShowsTheGivenReason()
    {
        var popup = Popup(new DisplayChoice(null, false),
            "Claude Code has not cached usage data, and there is no credentials file for a live fetch.");
        Assert.Contains("Claude Code has not cached usage data, and there is no credentials file for a live fetch.", Texts(popup));
    }

    [Fact]
    public void NoData_WithoutAReason_FallsBackToTheDefaultLine()
        => Assert.Contains(NoDataReason.Default, Texts(Popup(new DisplayChoice(null, false))));

    [Fact]
    public void NoDataText_WrapsInsteadOfWideningThePopup()
    {
        var shortPopup = Popup(new DisplayChoice(null, false), "Short.");
        var longPopup = Popup(new DisplayChoice(null, false),
            "Claude Code has not cached usage data, and its credentials are not usable for a live fetch, "
            + "and this sentence keeps going to make sure the label wraps rather than stretches.");
        Assert.True(longPopup.PreferredSize.Width <= Math.Max(shortPopup.PreferredSize.Width, UsageBar.DefaultWidth + 40),
            $"long={longPopup.PreferredSize.Width} short={shortPopup.PreferredSize.Width}");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~UsagePopup`
Expected: build error, no `UsagePopup` constructor takes a `DisplayChoice`.

- [ ] **Step 3a: Change the constructor and the two branches**

In `UsagePopup.cs`, replace the constructor signature and the `snapshot is null` / `else` block:

```csharp
    public UsagePopup(DisplayChoice choice, Settings settings, DateTimeOffset now,
        PlatformStatus? platformStatus = null, string? lastFetchStatus = null, string? noDataText = null)
    {
        // ... form setup and `layout` unchanged ...

        AddPlatformStatus(layout, platformStatus, settings, now);

        if (choice.Snapshot is not { } snapshot)
        {
            // Sentences here name what is missing and can run long; wrap at the bar width like the
            // status banner does rather than stretching the form.
            layout.Controls.Add(WrappingLabel(noDataText ?? NoDataReason.Default,
                SystemColors.ControlText, new Padding(0)));
        }
        else
        {
            // Staleness is decided by SourceSelection with each source's own allowance; recomputing
            // it here against StalenessMinutes would flag a desktop-only user most of the time.
            bool stale = choice.Stale;
            AddWindowRow(layout, "5-hour window", snapshot.FiveHour, TimeSpan.FromHours(5), settings, now);
            AddWindowRow(layout, "7-day window", snapshot.SevenDay, TimeSpan.FromDays(7), settings, now);

            // ... scoped rows, hidden count, credit row unchanged ...

            var ago = RelativeTime.Ago(snapshot.FetchedAt, now);
            var updated = snapshot.Source == UsageSource.DesktopHistory
                ? $"Claude Desktop history · updated {ago}"
                : $"Last updated {ago}";
            if (stale) updated += " · stale";
            layout.Controls.Add(new Label
            {
                Text = updated,
                AutoSize = true,
                ForeColor = stale ? Color.Firebrick : SystemColors.GrayText,
                Margin = new Padding(0, 8, 0, 0),
            });
        }

        // ... lastFetchStatus label, Controls.Add(layout), PositionNearCursor() unchanged ...
    }
```

Remove the old `bool stale = now - snapshot.FetchedAt > ...` line; nothing else in the class referenced it.

- [ ] **Step 3b: Keep `TrayApp` compiling**

In `TrayApp.ShowPopup()` replace the constructor call with the wrapped form for now (Task 10 replaces it again):

```csharp
        _popup = new UsagePopup(new DisplayChoice(_snapshot, false), _settings, DateTimeOffset.UtcNow, _status, _lastFetchStatus);
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~UsagePopup`
Expected: all passed (4 width tests, 8 source tests). If `DesktopSource_Stale_IsFlagged` fails on the age text, check `RelativeTime.Ago(Now.AddHours(-5), Now)` returns `"5h ago"` (it does: whole hours print without minutes).

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Tray/UsagePopup.cs src/ClaudeUsageTray/Tray/TrayApp.cs tests/ClaudeUsageTray.Tests/UsagePopupWidthTests.cs tests/ClaudeUsageTray.Tests/UsagePopupSourceTests.cs
git commit -m "feat(desktop-history): popup shows the selected source and the no-data reason

Refs #5

Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 10: `TrayApp` wiring

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs`

**Interfaces:**
- Consumes: everything produced by Tasks 1 through 9.
- Produces: nothing new for later tasks. Behaviour is verified by the Core tests plus a manual run.

- [ ] **Step 1: Rename the slot and add the new state**

Replace the field `private UsageSnapshot? _snapshot;` with:

```csharp
    // Two sources, one shown: Claude Code's (cache + live, merged by SnapshotPrecedence) and the
    // Claude Desktop history. SourceSelection picks between them at render time; each slot keeps its
    // last-known-good value until its own allowance runs out, so a transient read failure never
    // blanks the display.
    private UsageSnapshot? _cliSnapshot;
    private UsageSnapshot? _desktopSnapshot;
    private DesktopHistoryStatus _desktopStatus = DesktopHistoryStatus.NotFound;
    private string? _noDataText;
    private UsageSource? _lastLoggedSource;
    private DateTimeOffset? _lastLoggedDesktopAt;
```

Then replace every remaining `_snapshot` in the file with `_cliSnapshot` (in `Refresh()` and `OnApiFetchCompleted`; `Render`, `BuildTooltip` and `ShowPopup` are rewritten below). Build to find any stragglers: `dotnet build src/ClaudeUsageTray`.

- [ ] **Step 2: Extend `Refresh()`**

Keep the existing cache block exactly as it is (with `_cliSnapshot`), and insert the desktop block plus the facts before the final `Render();`:

```csharp
        var now = DateTimeOffset.UtcNow;
        var desktop = DesktopUsageReader.ReadFirst(DesktopHistoryPath.ByFreshness(
            DesktopHistoryPath.Candidates(_settings.DesktopHistoryPathOverride,
                DesktopHistoryPath.DefaultAppData, DesktopHistoryPath.DefaultLocalAppData)));
        _desktopStatus = desktop.Status;
        if (desktop.Snapshot is not null)
        {
            if (SnapshotPrecedence.IsNewer(desktop.Snapshot, _desktopSnapshot))
            {
                _desktopSnapshot = desktop.Snapshot;
                LogDesktopSample(now);
            }
        }
        else if (_desktopSnapshot is not null
            && SourceSelection.Age(_desktopSnapshot, now) > TimeSpan.FromHours(_settings.DesktopStalenessHours))
        {
            _desktopSnapshot = null; // past its allowance and no longer readable: let it go
        }

        // Computed here, not in Render(): it reads .claude.json again, and Render runs on every tick.
        _noDataText = _cliSnapshot is null && _desktopSnapshot is null
            ? NoDataReason.Describe(new NoDataFacts(
                UsageCacheReader.Status(_configPath),
                CredentialsReader.Status(CredentialsReader.DefaultPath, now),
                _desktopStatus))
            : null;

        Render();
```

- [ ] **Step 3: Rewrite `Render()` and `Apply`**

```csharp
    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        var choice = SourceSelection.Choose(_cliSnapshot, _desktopSnapshot, now, _settings);
        LogSourceChange(choice, now);
        bool degraded = _status is { Degraded: true };
        // A real outage must not vanish because *our* network is down: the state keeps being
        // displayed once fetched, only marked stale.
        bool statusStale = _status is not null
            && now - _status.FetchedAt > TimeSpan.FromMinutes(_settings.StalenessMinutes);

        if (_iconFive is not null)
            Apply(_iconFive, '5', choice, choice.Snapshot?.FiveHour, "5h", TimeSpan.FromHours(5),
                clockwise: true, degraded, statusStale, now);
        if (_iconSeven is not null)
            Apply(_iconSeven, '7', choice, choice.Snapshot?.SevenDay, "7d", TimeSpan.FromDays(7),
                clockwise: false, degraded, statusStale, now);

        _updatedItem.Text = _settingsSaveFailed
            ? "Settings could not be saved"
            : choice.Snapshot is null
                ? "No usage data"
                : $"Updated {RelativeTime.Ago(choice.Snapshot.FetchedAt, now)}";

        _restartToUpdateItem.Enabled = UpdateCheck.IsUpdateReady;
    }

    private void Apply(NotifyIcon icon, char digit, DisplayChoice choice, WindowUsage? usage, string label,
        TimeSpan period, bool clockwise, bool degraded, bool statusStale, DateTimeOffset now)
    {
        int size = IconRenderer.SystemTrayIconSize();
        var old = icon.Icon;

        if (usage is null)
        {
            icon.Icon = IconRenderer.RenderNeutral(size, warning: degraded);
            icon.Text = TrimTooltip((_noDataText ?? NoDataReason.Default) + StatusSuffix(statusStale));
        }
        else
        {
            // No hysteresis: fetches are minutes apart and the ratio only moves fast early in a
            // period, which SeverityRules' dead zone already keeps out of the badge.
            var elapsed = TimeMarker.ElapsedFraction(usage.ResetsAt, period, now);
            var severity = _settings.PaceColors
                ? SeverityRules.ForPace(usage.Percent, elapsed, _settings.Thresholds.Orange, _settings.Thresholds.Red)
                : SeverityRules.For(usage.Percent, _settings.Thresholds.Orange, _settings.Thresholds.Red);
            icon.Icon = IconRenderer.Render(digit, usage.Percent, severity, clockwise,
                dimmed: choice.Stale, size, warning: degraded);
            icon.Text = TrimTooltip(BuildTooltip(label, usage, elapsed, choice, now) + StatusSuffix(statusStale));
        }
        old?.Dispose();
    }

    private string BuildTooltip(string label, WindowUsage usage, double? elapsedFraction, DisplayChoice choice,
        DateTimeOffset now)
    {
        var parts = new List<string> { label, $"{usage.Percent}%" };
        // Only when pace decided the colour — otherwise the badge means percent and needs no gloss.
        if (_settings.PaceColors
            && PaceFormat.Describe(SeverityRules.PaceRatio(
                usage.Percent, elapsedFraction, _settings.Thresholds.Red)) is { Length: > 0 } pace)
            parts.Add(pace);
        if (usage.ResetsAt is { } resetsAt)
        {
            parts.Add($"resets in {RelativeTime.In(resetsAt, now)}");
            if (choice.Stale && resetsAt <= now) parts.Add("awaiting refresh"); // cached % may be the prior window
        }
        if (choice.Snapshot is { } snapshot)
        {
            bool desktop = snapshot.Source == UsageSource.DesktopHistory;
            if (choice.Stale)
                parts.Add($"stale · {(desktop ? "Claude Desktop history · " : "")}updated {RelativeTime.Ago(snapshot.FetchedAt, now)}");
            else if (desktop)
                parts.Add($"Claude Desktop history · updated {RelativeTime.Ago(snapshot.FetchedAt, now)}");
        }
        return string.Join(" · ", parts);
    }
```

- [ ] **Step 4: The popup, the fetch status, the log lines, the settings save**

`ShowPopup()`:

```csharp
        var now = DateTimeOffset.UtcNow;
        _popup = new UsagePopup(SourceSelection.Choose(_cliSnapshot, _desktopSnapshot, now, _settings),
            _settings, now, _status, _lastFetchStatus, _noDataText);
```

In `StartApiFetch()`, replace the `token is null` line:

```csharp
        if (token is null)
        {
            // Normal on a desktop-only machine: the Claude Code the desktop app installs never writes
            // a credentials file. Say so in the popup rather than leaving "no fetch yet" forever.
            _lastFetchStatus = CredentialsReader.Status(CredentialsReader.DefaultPath, now) == CredentialStatus.Missing
                ? "no credentials file · live fetch off"
                : "no valid credentials · live fetch off";
            _log.Write(now, "skip: no valid access token (missing/expired/near-expiry)");
            return;
        }
```

Add the two log helpers next to `Render()`:

```csharp
    /// <summary>One line per adopted desktop sample. Percentages and age only.</summary>
    private void LogDesktopSample(DateTimeOffset now)
    {
        if (_desktopSnapshot is not { } s || s.FetchedAt == _lastLoggedDesktopAt) return;
        _lastLoggedDesktopAt = s.FetchedAt;
        string five = s.FiveHour?.Percent.ToString() ?? "-";
        string seven = s.SevenDay?.Percent.ToString() ?? "-";
        _log.Write(now, $"desktop: adopted 5h={five}% 7d={seven}% updated {RelativeTime.Ago(s.FetchedAt, now)}");
    }

    /// <summary>One line whenever the displayed source changes, so a "shows the wrong numbers" report
    /// can be traced to which file they came from.</summary>
    private void LogSourceChange(DisplayChoice choice, DateTimeOffset now)
    {
        var source = choice.Snapshot?.Source;
        if (source == _lastLoggedSource) return;
        _lastLoggedSource = source;
        if (source is null) _log.Write(now, "source: none");
        else if (source == UsageSource.DesktopHistory)
        {
            var cli = _cliSnapshot is null ? "absent" : $"stale, updated {RelativeTime.Ago(_cliSnapshot.FetchedAt, now)}";
            _log.Write(now, $"source: desktop history (claude code {cli})");
        }
        else _log.Write(now, "source: claude code");
    }
```

`_lastLoggedSource` starts as `null`, and the first `Render()` with no data would then log nothing; that is fine — the first line appears when a source is adopted.

In the settings save block (the run of `_settings.X = edited.X;` lines), after `StalenessMinutes`:

```csharp
        _settings.DesktopStalenessHours = edited.DesktopStalenessHours;
```

- [ ] **Step 5: Build and run the whole suite**

Run: `dotnet build src/ClaudeUsageTray` then `dotnet test`
Expected: build clean with no new warnings; all tests pass. Any remaining `_snapshot` reference shows up as a build error here.

- [ ] **Step 6: Manual check from source**

Run: `dotnet run --project src/ClaudeUsageTray`. Verify: the icons render; hovering shows the tooltip; left-click opens the popup; `%APPDATA%\ClaudeUsageTray\fetch.log` gained a `source: ...` line and, if a desktop file exists on this machine, a `desktop: adopted ...` line with no uuid in it. Quit the app from the menu. Do not leave it running from the tool shell (it would die with the shell and look like a crash).

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Tray/TrayApp.cs
git commit -m "feat(desktop-history): fall back to Claude Desktop history when Claude Code's data is absent or stale

Two snapshot slots with their own allowances, SourceSelection at render time, the no-data reason in
the tooltip and popup, and fetch.log lines for the adopted desktop sample and source changes.

Refs #5

Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 11: README and CHANGELOG

**Files:**
- Modify: `README.md`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: README icon table**

In the table under `### The icons`, change the last two rows:

```markdown
| Dimmed | Stale data (Claude Code data older than 15 min, or Claude Desktop history older than 3 h) |
| Grey `—` | No data yet — open Claude Code or Claude Desktop once |
```

- [ ] **Step 2: README data sources**

Replace the `Two sources, newest wins:` list inside `<summary><b>Where the data comes from</b></summary>` with:

```markdown
Three usage sources plus the status page. Claude Code's data wins whenever it is current; the
Claude Desktop history steps in when it is not.

1. **Live** — read-only `GET` against Anthropic's OAuth usage endpoint every 5 minutes,
   the same source claude.ai uses, authenticated with **Claude Code's existing token**.
   The app never stores, refreshes, or logs that token, and respects `Retry-After` on 429s.
2. **Offline fallback** — the `cachedUsageUtilization` block Claude Code writes to
   `%USERPROFILE%\.claude.json`, used whenever no valid token is available.
3. **Claude Desktop history** — `plan-usage-history.json`, which the Claude Desktop app keeps in
   `%APPDATA%\Claude\` or, on newer versions, inside its package container under
   `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\`. Both places are checked and the
   newer file wins. It is used only when the two Claude Code sources are absent or stale, and it
   carries percentages only: no reset times (so no countdowns, no pace colouring, no time marker),
   no per-model limits, no credit amounts. The desktop app writes it only while you work in it, so
   gaps of an hour are normal; it counts as stale after `desktopStalenessHours`. If you only use
   Claude Desktop, this is where your numbers come from. The field meanings are inferred from
   observation, not documented, so a desktop app update can silently change them.
4. **Platform status** — the public status page at status.claude.com, polled once a minute with
   no auth and no token involved. The page's own banner decides the warning badge; incident
   details are the page's own words.

When nothing yields data, the popup says which of these is missing — `.claude.json` absent, present
without a usage block, no credentials file for the live fetch, or a desktop history file with no
samples — instead of a blanket "run Claude Code". Note that installing Claude Desktop also installs
Claude Code, so `.claude.json` usually exists even on a machine that never ran the CLI directly.
```

- [ ] **Step 3: README settings**

Change the first sentence under `## Settings`:

```markdown
**Right-click → `Settings…`** covers everything except the two path overrides: which icons to show,
run-at-startup, the two colour thresholds, pace colouring, and the two staleness cutoffs.
```

Add to the settings table after the `stalenessMinutes` row and after the `configPathOverride` row respectively:

```markdown
| `desktopStalenessHours` | hours before Claude Desktop history data is flagged stale | `3` |
```

```markdown
| `desktopHistoryPathOverride` | explicit path to the desktop app's `plan-usage-history.json`; file-only, and re-read at launch | unset |
```

- [ ] **Step 4: CHANGELOG**

Insert before `## [0.7.2] - 2026-09-04`:

```markdown
## [Unreleased]

### Added

- **Usage for Claude Desktop users.** The tray now reads the Claude Desktop app's own usage history
  when Claude Code's data is missing or stale, so a machine that only uses the desktop app shows
  its 5-hour and 7-day percentages (and credits, when enabled) instead of a permanent `—`. Both
  places the desktop app is known to keep the file are checked. This source has no reset times,
  so those rows show percentages without countdowns or pace colouring, and the popup says
  *Claude Desktop history* so you know which numbers you are looking at.
- **`desktopStalenessHours`** in Settings (default 3). The desktop app records usage only while
  you work in it, so its data is judged by an hours-scale cutoff rather than the minutes-scale one
  used for Claude Code.

### Changed

- When there is no usage data at all, the popup and tooltip now say what is missing — `.claude.json`
  absent, present without a usage block, no credentials file for the live fetch, or an empty desktop
  history — instead of always suggesting you run Claude Code. The popup's *Fetch* line also explains
  when the live fetch is off for lack of credentials.
```

The `[Unreleased]:` compare link at the bottom of the file already exists; leave it.

- [ ] **Step 5: Final full run and commit**

Run: `dotnet test --configuration Release`
Expected: all passed (this is what CI runs).

```bash
git add README.md CHANGELOG.md
git commit -m "docs: Claude Desktop history as a usage source, new settings, no-data messages

Refs #5

Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

## Self-review notes

- Spec coverage: data source and candidates (Task 3), reader incl. max-by-`t`, skipping, size guard, `xu` credits, `Source` (Tasks 1, 4), `SourceSelection` incl. future tolerance (Task 5), snapshot lifetime rules (Task 10 Step 2), `NoDataReason` with enums and `Status` probes (Tasks 6, 7), popup wording and `Stale` from the choice (Task 9), tooltip wording, fetch status and log lines (Task 10), settings keys, normalisation and dialog paths (Tasks 2, 8), README and CHANGELOG (Task 11).
- Deviation from the spec, deliberate: `DesktopHistoryStatus` has an extra `Ok` member as the reader's success marker; `NoDataReason` ignores it. Recorded in the enum's doc comment.
- Names used across tasks: `UsageSource`, `DisplayChoice`, `SourceSelection.Choose/Age`, `DesktopHistoryPath.Candidates/ByFreshness/DefaultAppData/DefaultLocalAppData/FileName`, `DesktopUsageReader.Read/TryRead/ReadFirst`, `DesktopHistoryResult`, `DesktopHistoryStatus`, `ConfigStatus`, `CredentialStatus`, `NoDataFacts`, `NoDataReason.Describe/Default`, `Settings.DesktopStalenessHours/DesktopHistoryPathOverride`, `ThresholdRules.DefaultDesktopStalenessHours`. Checked consistent.
