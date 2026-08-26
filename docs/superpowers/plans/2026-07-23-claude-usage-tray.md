# Claude Usage Tray Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows tray app that renders the local Claude Code 5-hour and 7-day usage cache as progress-ring tray icons, with zero network calls to Anthropic.

**Architecture:** Pure, unit-tested core (`UsageCacheReader`, `Severity`, `RelativeTime`, `Settings`, `IconRenderer`) feeding a thin WinForms glue layer (`TrayApp` with `NotifyIcon`s, `FileSystemWatcher` + timer, context menu, popup). Packaged per-user with Velopack auto-updates from GitHub Releases.

**Tech Stack:** C# / .NET 10 (LTS), WinForms (`net10.0-windows`), `System.Text.Json`, GDI+ (`System.Drawing`), xUnit, Velopack (`vpk`).

## Global Constraints

- Read-only consumer of `%USERPROFILE%\.claude.json` → key `cachedUsageUtilization`. **No writes to that file, no HTTP to Anthropic, never touch `.credentials.json` or OAuth tokens.**
- The only permitted outbound network traffic is the Velopack update check to `https://github.com/wus-technik/win_systray-claude-usage`.
- Target framework: `net10.0-windows` (latest LTS). `RollForward=LatestMajor` is NOT used — pin exactly.
- Severity defaults: `< 50 %` green, `50–85 %` orange, `> 85 %` red. Staleness default: **15 min**.
- Settings file: `%APPDATA%\ClaudeUsageTray\settings.json` with keys `displayMode` (`"fiveHour"` | `"sevenDay"` | `"both"`, default `"both"`), `thresholds` (`{ "orange": 50, "red": 85 }`), `stalenessMinutes` (`15`), `runAtStartup` (`true`), `configPathOverride` (unset).
- Icon semantics: ring fill = usage (clamped at 100), ring color = severity, center digit `5` or `7`, 5h fills clockwise / 7d counter-clockwise, arc starts at 12 o'clock. Exact percentage lives in tooltip/popup only. Tray order fixed: 5 left, 7 right.
- Per-user install only, no admin rights. App/product ID: `ClaudeUsageTray`.
- Commit after every task. Never add AI co-author trailers to commits.
- All git/GitHub operations via `gh`-authenticated HTTPS (already configured).

## File Structure

```
ClaudeUsageTray.sln
Directory.Build.props                                  (shared TFM/lang settings)
src/ClaudeUsageTray/ClaudeUsageTray.csproj
src/ClaudeUsageTray/Program.cs                         (entry point; Velopack hook first)
src/ClaudeUsageTray/Core/UsageSnapshot.cs              (domain records)
src/ClaudeUsageTray/Core/SeverityRules.cs              (percent → Green|Orange|Red)
src/ClaudeUsageTray/Core/RelativeTime.cs               ("resets in 2h 13m", "4m ago")
src/ClaudeUsageTray/Core/ConfigPath.cs                 (.claude.json path resolution)
src/ClaudeUsageTray/Core/UsageCacheReader.cs           (file → UsageSnapshot?)
src/ClaudeUsageTray/Core/Settings.cs                   (load/save settings.json)
src/ClaudeUsageTray/Tray/IconRenderer.cs               (GDI+ progress-ring icons)
src/ClaudeUsageTray/Tray/StartupRegistration.cs        (HKCU Run key)
src/ClaudeUsageTray/Tray/TrayApp.cs                    (NotifyIcons, watcher, menu)
src/ClaudeUsageTray/Tray/UsagePopup.cs                 (left-click popup form)
src/ClaudeUsageTray/UpdateCheck.cs                     (Velopack periodic check)
tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj
tests/ClaudeUsageTray.Tests/SeverityRulesTests.cs
tests/ClaudeUsageTray.Tests/RelativeTimeTests.cs
tests/ClaudeUsageTray.Tests/UsageCacheReaderTests.cs
tests/ClaudeUsageTray.Tests/SettingsTests.cs
tests/ClaudeUsageTray.Tests/IconRendererTests.cs
build/build-release.ps1                                (publish + vpk pack)
```

---

### Task 1: Toolchain + solution scaffold

**Files:**
- Create: `ClaudeUsageTray.sln`
- Create: `Directory.Build.props`
- Create: `src/ClaudeUsageTray/ClaudeUsageTray.csproj`
- Create: `src/ClaudeUsageTray/Program.cs`
- Create: `tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`
- Create: `tests/ClaudeUsageTray.Tests/SmokeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: a solution where `dotnet build` and `dotnet test` succeed; namespace root `ClaudeUsageTray`; test project references the app project. All later tasks add files to these two projects.

- [ ] **Step 1: Install the .NET 10 SDK (skip if `dotnet --version` already reports 10.x)**

Run: `winget install Microsoft.DotNet.SDK.10 --accept-source-agreements --accept-package-agreements`
Then open a **new** shell (PATH refresh) and run: `dotnet --version`
Expected: a `10.0.x` version string.

- [ ] **Step 2: Create solution and projects**

Run (from repo root):

```powershell
dotnet new sln -n ClaudeUsageTray
dotnet new winforms -n ClaudeUsageTray -o src/ClaudeUsageTray
dotnet new xunit -n ClaudeUsageTray.Tests -o tests/ClaudeUsageTray.Tests
dotnet sln add src/ClaudeUsageTray tests/ClaudeUsageTray.Tests
dotnet add tests/ClaudeUsageTray.Tests reference src/ClaudeUsageTray
```

Delete the template's `Form1.cs`, `Form1.Designer.cs`, and `UnitTest1.cs`:

```powershell
Remove-Item src/ClaudeUsageTray/Form1.cs, src/ClaudeUsageTray/Form1.Designer.cs, tests/ClaudeUsageTray.Tests/UnitTest1.cs
```

- [ ] **Step 3: Write `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Write `src/ClaudeUsageTray/ClaudeUsageTray.csproj`** (replace template content entirely)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <UseWindowsForms>true</UseWindowsForms>
    <RootNamespace>ClaudeUsageTray</RootNamespace>
    <AssemblyName>ClaudeUsageTray</AssemblyName>
    <Version>0.1.0</Version>
    <ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

- [ ] **Step 5: Write `tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`** (replace template content, keep the package versions the template generated if newer)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <UseWindowsForms>true</UseWindowsForms>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\ClaudeUsageTray\ClaudeUsageTray.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Write minimal `src/ClaudeUsageTray/Program.cs`** (Velopack comes in Task 10)

```csharp
namespace ClaudeUsageTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        // TrayApp is wired up in a later task; for now the scaffold just exits.
    }
}
```

- [ ] **Step 7: Write `tests/ClaudeUsageTray.Tests/SmokeTests.cs`**

```csharp
using Xunit;

namespace ClaudeUsageTray.Tests;

public class SmokeTests
{
    [Fact]
    public void SolutionBuildsAndTestsRun() => Assert.True(true);
}
```

- [ ] **Step 8: Verify build and tests**

Run: `dotnet test`
Expected: build succeeds, `Passed! - Failed: 0, Passed: 1`.

- [ ] **Step 9: Commit**

```powershell
git add -A
git commit -m "chore: scaffold .NET 10 WinForms solution with xUnit test project"
```

---

### Task 2: SeverityRules

**Files:**
- Create: `src/ClaudeUsageTray/Core/SeverityRules.cs`
- Test: `tests/ClaudeUsageTray.Tests/SeverityRulesTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `enum Severity { Green, Orange, Red }` and `static Severity SeverityRules.For(int percent, int orangeAt = 50, int redAbove = 85)` in namespace `ClaudeUsageTray.Core`. Semantics: `percent > redAbove` → Red; else `percent >= orangeAt` → Orange; else Green.

- [ ] **Step 1: Write the failing tests** — `tests/ClaudeUsageTray.Tests/SeverityRulesTests.cs`

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class SeverityRulesTests
{
    [Theory]
    [InlineData(0, Severity.Green)]
    [InlineData(49, Severity.Green)]
    [InlineData(50, Severity.Orange)]
    [InlineData(85, Severity.Orange)]
    [InlineData(86, Severity.Red)]
    [InlineData(100, Severity.Red)]
    [InlineData(150, Severity.Red)]
    public void DefaultThresholds(int percent, Severity expected)
        => Assert.Equal(expected, SeverityRules.For(percent));

    [Theory]
    [InlineData(29, Severity.Green)]
    [InlineData(30, Severity.Orange)]
    [InlineData(60, Severity.Orange)]
    [InlineData(61, Severity.Red)]
    public void CustomThresholds(int percent, Severity expected)
        => Assert.Equal(expected, SeverityRules.For(percent, orangeAt: 30, redAbove: 60));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter SeverityRulesTests`
Expected: FAIL to compile — `Severity` / `SeverityRules` not defined.

- [ ] **Step 3: Write the implementation** — `src/ClaudeUsageTray/Core/SeverityRules.cs`

```csharp
namespace ClaudeUsageTray.Core;

public enum Severity { Green, Orange, Red }

public static class SeverityRules
{
    /// <summary>&lt; orangeAt → Green, orangeAt..redAbove → Orange, &gt; redAbove → Red.</summary>
    public static Severity For(int percent, int orangeAt = 50, int redAbove = 85)
        => percent > redAbove ? Severity.Red
         : percent >= orangeAt ? Severity.Orange
         : Severity.Green;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter SeverityRulesTests`
Expected: PASS (11 tests).

- [ ] **Step 5: Commit**

```powershell
git add src/ClaudeUsageTray/Core/SeverityRules.cs tests/ClaudeUsageTray.Tests/SeverityRulesTests.cs
git commit -m "feat: severity classification with configurable thresholds"
```

---

### Task 3: RelativeTime

**Files:**
- Create: `src/ClaudeUsageTray/Core/RelativeTime.cs`
- Test: `tests/ClaudeUsageTray.Tests/RelativeTimeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static class RelativeTime` in `ClaudeUsageTray.Core` with:
  - `string Ago(DateTimeOffset then, DateTimeOffset now)` → `"just now"` (< 60 s), `"4m ago"`, `"2h 13m ago"`, `"3d 20h ago"`.
  - `string In(DateTimeOffset target, DateTimeOffset now)` → `"45m"`, `"2h 13m"`, `"3d 20h"`; `"now"` when target ≤ now.

- [ ] **Step 1: Write the failing tests** — `tests/ClaudeUsageTray.Tests/RelativeTimeTests.cs`

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1m ago")]
    [InlineData(4 * 60, "4m ago")]
    [InlineData(2 * 3600 + 13 * 60, "2h 13m ago")]
    [InlineData(2 * 3600, "2h ago")]
    [InlineData(3 * 86400 + 20 * 3600, "3d 20h ago")]
    [InlineData(3 * 86400, "3d ago")]
    public void Ago(int secondsBefore, string expected)
        => Assert.Equal(expected, RelativeTime.Ago(Now.AddSeconds(-secondsBefore), Now));

    [Theory]
    [InlineData(45 * 60, "45m")]
    [InlineData(30, "1m")]                       // sub-minute rounds up to 1m
    [InlineData(2 * 3600 + 13 * 60, "2h 13m")]
    [InlineData(3 * 3600, "3h")]
    [InlineData(3 * 86400 + 20 * 3600, "3d 20h")]
    public void In(int secondsAhead, string expected)
        => Assert.Equal(expected, RelativeTime.In(Now.AddSeconds(secondsAhead), Now));

    [Theory]
    [InlineData(0)]
    [InlineData(-3600)] // target already passed
    public void In_PastOrNow_ReturnsNow(int secondsAhead)
        => Assert.Equal("now", RelativeTime.In(Now.AddSeconds(secondsAhead), Now));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter RelativeTimeTests`
Expected: FAIL to compile — `RelativeTime` not defined.

- [ ] **Step 3: Write the implementation** — `src/ClaudeUsageTray/Core/RelativeTime.cs`

```csharp
namespace ClaudeUsageTray.Core;

public static class RelativeTime
{
    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var elapsed = now - then;
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        return Span(elapsed) + " ago";
    }

    public static string In(DateTimeOffset target, DateTimeOffset now)
    {
        var remaining = target - now;
        if (remaining <= TimeSpan.Zero) return "now";
        return Span(remaining);
    }

    private static string Span(TimeSpan d)
    {
        if (d.TotalDays >= 1)
        {
            int days = (int)d.TotalDays;
            return d.Hours > 0 ? $"{days}d {d.Hours}h" : $"{days}d";
        }
        if (d.TotalHours >= 1)
        {
            int hours = (int)d.TotalHours;
            return d.Minutes > 0 ? $"{hours}h {d.Minutes}m" : $"{hours}h";
        }
        return $"{Math.Max(1, (int)Math.Ceiling(d.TotalMinutes))}m";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter RelativeTimeTests`
Expected: PASS (15 tests).

- [ ] **Step 5: Commit**

```powershell
git add src/ClaudeUsageTray/Core/RelativeTime.cs tests/ClaudeUsageTray.Tests/RelativeTimeTests.cs
git commit -m "feat: relative time formatting for reset countdowns and staleness"
```

---

### Task 4: Domain model, ConfigPath, UsageCacheReader

**Files:**
- Create: `src/ClaudeUsageTray/Core/UsageSnapshot.cs`
- Create: `src/ClaudeUsageTray/Core/ConfigPath.cs`
- Create: `src/ClaudeUsageTray/Core/UsageCacheReader.cs`
- Test: `tests/ClaudeUsageTray.Tests/UsageCacheReaderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `ClaudeUsageTray.Core`):
  - `sealed record WindowUsage(int Percent, DateTimeOffset? ResetsAt)`
  - `sealed record UsageSnapshot(DateTimeOffset FetchedAt, WindowUsage? FiveHour, WindowUsage? SevenDay)`
  - `static string ConfigPath.Resolve(string? overridePath)` — override if non-blank, else `%USERPROFILE%\.claude.json`.
  - `static UsageSnapshot? UsageCacheReader.TryRead(string path)` — `null` on missing file, missing `cachedUsageUtilization`/`fetchedAtMs`, or malformed JSON. Never throws for IO/JSON problems. Opens with `FileShare.ReadWrite` (Claude Code may be mid-write).

- [ ] **Step 1: Write the failing tests** — `tests/ClaudeUsageTray.Tests/UsageCacheReaderTests.cs`

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class UsageCacheReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cut-reader-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFixture(string json)
    {
        var path = Path.Combine(_dir, ".claude.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string ValidJson = """
        {
          "otherTopLevelKey": true,
          "cachedUsageUtilization": {
            "fetchedAtMs": 1784815176543,
            "utilization": {
              "five_hour": { "utilization": 42, "resets_at": "2026-07-23T18:39:59Z" },
              "seven_day": { "utilization": 13, "resets_at": "2026-07-27T15:59:59Z" }
            },
            "extra_usage": {}, "spend": 0
          }
        }
        """;

    [Fact]
    public void Valid_ParsesBothWindows()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(ValidJson));
        Assert.NotNull(s);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1784815176543), s!.FetchedAt);
        Assert.Equal(42, s.FiveHour!.Percent);
        Assert.Equal(new DateTimeOffset(2026, 7, 23, 18, 39, 59, TimeSpan.Zero), s.FiveHour.ResetsAt);
        Assert.Equal(13, s.SevenDay!.Percent);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 15, 59, 59, TimeSpan.Zero), s.SevenDay.ResetsAt);
    }

    [Fact]
    public void MissingFile_ReturnsNull()
        => Assert.Null(UsageCacheReader.TryRead(Path.Combine(_dir, "does-not-exist.json")));

    [Fact]
    public void MissingCachedUsageKey_ReturnsNull()
        => Assert.Null(UsageCacheReader.TryRead(WriteFixture("""{ "someOtherKey": 1 }""")));

    [Fact]
    public void MalformedJson_ReturnsNull()
        => Assert.Null(UsageCacheReader.TryRead(WriteFixture("{ not json !!")));

    [Fact]
    public void MissingFetchedAtMs_ReturnsNull()
        => Assert.Null(UsageCacheReader.TryRead(WriteFixture(
            """{ "cachedUsageUtilization": { "utilization": {} } }""")));

    [Fact]
    public void StaleFetchedAt_StillParses_AgeIsCallersConcern()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(
            """{ "cachedUsageUtilization": { "fetchedAtMs": 946684800000, "utilization": {} } }"""));
        Assert.NotNull(s);
        Assert.Equal(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), s!.FetchedAt);
        Assert.Null(s.FiveHour);
        Assert.Null(s.SevenDay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(150)] // >100 is preserved; clamping is the renderer's job
    public void BoundaryPercentages_PreservedAsIs(int percent)
    {
        var s = UsageCacheReader.TryRead(WriteFixture($$"""
            { "cachedUsageUtilization": { "fetchedAtMs": 1784815176543,
              "utilization": { "five_hour": { "utilization": {{percent}}, "resets_at": "2026-07-23T18:39:59Z" } } } }
            """));
        Assert.Equal(percent, s!.FiveHour!.Percent);
        Assert.Null(s.SevenDay);
    }

    [Fact]
    public void WindowWithoutResetsAt_ParsesWithNullReset()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(
            """{ "cachedUsageUtilization": { "fetchedAtMs": 1, "utilization": { "seven_day": { "utilization": 7 } } } }"""));
        Assert.Equal(7, s!.SevenDay!.Percent);
        Assert.Null(s.SevenDay.ResetsAt);
    }

    [Fact]
    public void ConfigPath_Override_Wins()
        => Assert.Equal(@"C:\x\claude.json", ConfigPath.Resolve(@"C:\x\claude.json"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ConfigPath_Default_IsUserProfileClaudeJson(string? overridePath)
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        Assert.Equal(expected, ConfigPath.Resolve(overridePath));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter UsageCacheReaderTests`
Expected: FAIL to compile — types not defined.

- [ ] **Step 3: Write `src/ClaudeUsageTray/Core/UsageSnapshot.cs`**

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>Usage for one rolling window. Percent is the raw integer from the cache (may exceed 100).</summary>
public sealed record WindowUsage(int Percent, DateTimeOffset? ResetsAt);

/// <summary>The parsed cachedUsageUtilization payload. Windows are null when absent from the cache.</summary>
public sealed record UsageSnapshot(DateTimeOffset FetchedAt, WindowUsage? FiveHour, WindowUsage? SevenDay);
```

- [ ] **Step 4: Write `src/ClaudeUsageTray/Core/ConfigPath.cs`**

```csharp
namespace ClaudeUsageTray.Core;

public static class ConfigPath
{
    public static string Resolve(string? overridePath)
        => string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json")
            : overridePath;
}
```

- [ ] **Step 5: Write `src/ClaudeUsageTray/Core/UsageCacheReader.cs`**

```csharp
using System.Globalization;
using System.Text.Json;

namespace ClaudeUsageTray.Core;

public static class UsageCacheReader
{
    /// <summary>
    /// Read-only parse of .claude.json → cachedUsageUtilization. Returns null when the file,
    /// key, or fetchedAtMs is missing or the JSON is malformed. Never throws for IO/JSON errors.
    /// </summary>
    public static UsageSnapshot? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // FileShare.ReadWrite: Claude Code may be rewriting the file while we read.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("cachedUsageUtilization", out var cached)
                || cached.ValueKind != JsonValueKind.Object) return null;
            if (!cached.TryGetProperty("fetchedAtMs", out var fetched)
                || fetched.ValueKind != JsonValueKind.Number) return null;

            var fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(fetched.GetInt64());
            WindowUsage? five = null, seven = null;
            if (cached.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                five = ReadWindow(u, "five_hour");
                seven = ReadWindow(u, "seven_day");
            }
            return new UsageSnapshot(fetchedAt, five, seven);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static WindowUsage? ReadWindow(JsonElement utilization, string name)
    {
        if (!utilization.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("utilization", out var p) || p.ValueKind != JsonValueKind.Number) return null;

        DateTimeOffset? resetsAt = null;
        if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            resetsAt = parsed;
        }
        return new WindowUsage((int)Math.Round(p.GetDouble()), resetsAt);
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter UsageCacheReaderTests`
Expected: PASS (14 tests).

- [ ] **Step 7: Commit**

```powershell
git add src/ClaudeUsageTray/Core/UsageSnapshot.cs src/ClaudeUsageTray/Core/ConfigPath.cs src/ClaudeUsageTray/Core/UsageCacheReader.cs tests/ClaudeUsageTray.Tests/UsageCacheReaderTests.cs
git commit -m "feat: read-only parser for cachedUsageUtilization in .claude.json"
```

---

### Task 5: Settings (load/save round-trip)

**Files:**
- Create: `src/ClaudeUsageTray/Core/Settings.cs`
- Test: `tests/ClaudeUsageTray.Tests/SettingsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `ClaudeUsageTray.Core`):
  - `enum DisplayMode { FiveHour, SevenDay, Both }` — serialized camelCase (`"fiveHour"`, `"sevenDay"`, `"both"`).
  - `sealed class Thresholds { int Orange = 50; int Red = 85; }` (get/set properties)
  - `sealed class Settings` with get/set properties `DisplayMode DisplayMode = Both`, `Thresholds Thresholds`, `int StalenessMinutes = 15`, `bool RunAtStartup = true`, `string? ConfigPathOverride = null`; plus `static string DefaultPath` (`%APPDATA%\ClaudeUsageTray\settings.json`), `static Settings Load(string path)` (defaults on missing/broken file or keys), `void Save(string path)` (creates directory).

- [ ] **Step 1: Write the failing tests** — `tests/ClaudeUsageTray.Tests/SettingsTests.cs`

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cut-settings-").FullName;
    private string PathFor(string name) => Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTrip_PreservesAllValues()
    {
        var path = PathFor("sub/settings.json"); // Save must create the directory
        var original = new Settings
        {
            DisplayMode = DisplayMode.FiveHour,
            Thresholds = new Thresholds { Orange = 30, Red = 60 },
            StalenessMinutes = 5,
            RunAtStartup = false,
            ConfigPathOverride = @"C:\alt\.claude.json",
        };
        original.Save(path);
        var loaded = Settings.Load(path);

        Assert.Equal(DisplayMode.FiveHour, loaded.DisplayMode);
        Assert.Equal(30, loaded.Thresholds.Orange);
        Assert.Equal(60, loaded.Thresholds.Red);
        Assert.Equal(5, loaded.StalenessMinutes);
        Assert.False(loaded.RunAtStartup);
        Assert.Equal(@"C:\alt\.claude.json", loaded.ConfigPathOverride);
    }

    [Fact]
    public void Save_WritesDocumentedCamelCaseKeys()
    {
        var path = PathFor("settings.json");
        new Settings().Save(path);
        var json = File.ReadAllText(path);
        Assert.Contains("\"displayMode\": \"both\"", json);
        Assert.Contains("\"orange\": 50", json);
        Assert.Contains("\"red\": 85", json);
        Assert.Contains("\"stalenessMinutes\": 15", json);
        Assert.Contains("\"runAtStartup\": true", json);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var s = Settings.Load(PathFor("nope.json"));
        Assert.Equal(DisplayMode.Both, s.DisplayMode);
        Assert.Equal(50, s.Thresholds.Orange);
        Assert.Equal(85, s.Thresholds.Red);
        Assert.Equal(15, s.StalenessMinutes);
        Assert.True(s.RunAtStartup);
        Assert.Null(s.ConfigPathOverride);
    }

    [Fact]
    public void Load_PartialFile_FillsDefaultsForMissingKeys()
    {
        var path = PathFor("partial.json");
        File.WriteAllText(path, """{ "displayMode": "sevenDay" }""");
        var s = Settings.Load(path);
        Assert.Equal(DisplayMode.SevenDay, s.DisplayMode);
        Assert.Equal(50, s.Thresholds.Orange);  // default
        Assert.Equal(15, s.StalenessMinutes);   // default
        Assert.True(s.RunAtStartup);            // default
    }

    [Fact]
    public void Load_MalformedFile_ReturnsDefaults()
    {
        var path = PathFor("broken.json");
        File.WriteAllText(path, "{ nope");
        Assert.Equal(DisplayMode.Both, Settings.Load(path).DisplayMode);
    }

    [Fact]
    public void DefaultPath_IsUnderAppData()
        => Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClaudeUsageTray", "settings.json"),
            Settings.DefaultPath);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter SettingsTests`
Expected: FAIL to compile — `Settings` / `DisplayMode` / `Thresholds` not defined.

- [ ] **Step 3: Write the implementation** — `src/ClaudeUsageTray/Core/Settings.cs`

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageTray.Core;

public enum DisplayMode { FiveHour, SevenDay, Both }

public sealed class Thresholds
{
    public int Orange { get; set; } = 50;
    public int Red { get; set; } = 85;
}

public sealed class Settings
{
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Both;
    public Thresholds Thresholds { get; set; } = new();
    public int StalenessMinutes { get; set; } = 15;
    public bool RunAtStartup { get; set; } = true;
    public string? ConfigPathOverride { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsageTray", "settings.json");

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall through to defaults
        }
        return new Settings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter SettingsTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```powershell
git add src/ClaudeUsageTray/Core/Settings.cs tests/ClaudeUsageTray.Tests/SettingsTests.cs
git commit -m "feat: JSON settings with defaults for missing keys"
```

---

### Task 6: IconRenderer (progress-ring icons)

**Files:**
- Create: `src/ClaudeUsageTray/Tray/IconRenderer.cs`
- Test: `tests/ClaudeUsageTray.Tests/IconRendererTests.cs`

**Interfaces:**
- Consumes: `Severity` (Task 2).
- Produces (namespace `ClaudeUsageTray.Tray`):
  - `static Icon IconRenderer.Render(char digit, int percent, Severity severity, bool clockwise, bool dimmed, int size)` — progress ring starting at 12 o'clock, fill proportional to `percent` clamped 0–100, colored by severity with a faint same-hue track, single centered digit; `dimmed` reduces alpha (staleness).
  - `static Icon IconRenderer.RenderNeutral(int size)` — grey empty ring with centered `—` (no-data state).
  - `static int IconRenderer.SystemTrayIconSize()` — `GetSystemMetrics(SM_CXSMICON)`, min 16.

- [ ] **Step 1: Write the failing smoke tests** — `tests/ClaudeUsageTray.Tests/IconRendererTests.cs`

```csharp
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class IconRendererTests
{
    [Theory]
    [InlineData('5', 0, Severity.Green, true, false, 16)]
    [InlineData('5', 42, Severity.Green, true, false, 16)]
    [InlineData('7', 63, Severity.Orange, false, false, 20)]
    [InlineData('7', 100, Severity.Red, false, false, 32)]
    [InlineData('5', 150, Severity.Red, true, false, 16)]  // >100 clamps, must not throw
    [InlineData('5', 42, Severity.Green, true, true, 16)]  // dimmed/stale variant
    public void Render_ProducesIconOfRequestedSize(char digit, int percent, Severity sev, bool cw, bool dimmed, int size)
    {
        using var icon = IconRenderer.Render(digit, percent, sev, cw, dimmed, size);
        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
    }

    [Fact]
    public void Render_IsNotBlank()
    {
        using var icon = IconRenderer.Render('5', 42, Severity.Green, clockwise: true, dimmed: false, size: 32);
        using var bmp = icon.ToBitmap();
        bool anyPixel = false;
        for (int x = 0; x < bmp.Width && !anyPixel; x++)
            for (int y = 0; y < bmp.Height && !anyPixel; y++)
                if (bmp.GetPixel(x, y).A > 0) anyPixel = true;
        Assert.True(anyPixel, "rendered icon has no visible pixels");
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void RenderNeutral_ProducesIcon(int size)
    {
        using var icon = IconRenderer.RenderNeutral(size);
        Assert.Equal(size, icon.Width);
    }

    [Fact]
    public void SystemTrayIconSize_IsAtLeast16()
        => Assert.True(IconRenderer.SystemTrayIconSize() >= 16);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter IconRendererTests`
Expected: FAIL to compile — `IconRenderer` not defined.

- [ ] **Step 3: Write the implementation** — `src/ClaudeUsageTray/Tray/IconRenderer.cs`

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
    /// Progress-ring icon: arc from 12 o'clock, fill = percent (clamped 0–100), color = severity,
    /// faint same-hue track for the remainder, single centered digit for the window (5/7).
    /// clockwise=true for the 5h window, false (counter-clockwise) for 7d. dimmed = stale data.
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

    /// <summary>Grey empty ring with a centered em-dash: the "no usage data yet" state.</summary>
    public static Icon RenderNeutral(int size)
        => Draw("\u2014", percent: 0, Color.FromArgb(150, 150, 150), clockwise: true, dimmed: false, size);

    private static Icon Draw(string glyph, int percent, Color color, bool clockwise, bool dimmed, int size)
    {
        if (dimmed) color = Color.FromArgb(120, color);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float stroke = Math.Max(2f, size / 8f);
            var ringRect = new RectangleF(stroke / 2f, stroke / 2f, size - stroke, size - stroke);

            // Faint same-hue track for the unused remainder.
            using (var trackPen = new Pen(Color.FromArgb(dimmed ? 40 : 70, color), stroke))
                g.DrawEllipse(trackPen, ringRect);

            // Usage arc, from 12 o'clock (-90°). Counter-clockwise = negative sweep.
            if (percent > 0)
            {
                float sweep = 360f * percent / 100f;
                if (!clockwise) sweep = -sweep;
                using var arcPen = new Pen(color, stroke)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arcPen, ringRect, -90f, sweep);
            }

            // Single centered glyph. White reads on the (typically dark) taskbar.
            var textColor = dimmed ? Color.FromArgb(160, Color.White) : Color.White;
            using var font = new Font("Segoe UI", size * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(textColor);
            using var format = new StringFormat
                { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
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

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter IconRendererTests`
Expected: PASS (10 tests).

- [ ] **Step 5: Visually spot-check against the design preview**

Open `docs/icon-preview.html` in a browser and compare mentally: ring proportions, 12 o'clock start, severity colors, center digit. No code change expected — this is a sanity look, not a gate.

- [ ] **Step 6: Commit**

```powershell
git add src/ClaudeUsageTray/Tray/IconRenderer.cs tests/ClaudeUsageTray.Tests/IconRendererTests.cs
git commit -m "feat: DPI-aware progress-ring icon renderer with severity colors"
```

---

### Task 7: StartupRegistration

**Files:**
- Create: `src/ClaudeUsageTray/Tray/StartupRegistration.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `ClaudeUsageTray.Tray`): `static class StartupRegistration` with `void Enable()`, `void Disable()`, `bool IsEnabled()` — per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value name `ClaudeUsageTray`, data = quoted `Environment.ProcessPath`.

No unit test (writes to the real registry); verified manually below. This is the one unit the spec marks "manual".

- [ ] **Step 1: Write the implementation** — `src/ClaudeUsageTray/Tray/StartupRegistration.cs`

```csharp
using Microsoft.Win32;

namespace ClaudeUsageTray.Tray;

/// <summary>Per-user run-at-login toggle. No admin rights required (HKCU only).</summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeUsageTray";

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: success, no warnings about the new file.

- [ ] **Step 3: Manual verification via a throwaway C# snippet**

Run:

```powershell
dotnet run --project src/ClaudeUsageTray --no-build 2>$null; reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v ClaudeUsageTray
```

Expected at this point: `ERROR: The system was unable to find the specified registry key or value.` (nothing calls Enable yet — that wiring lands in Tasks 8 and 10). The full manual check (toggle via tray menu → value appears/disappears) is part of Task 8 Step 6.

- [ ] **Step 4: Commit**

```powershell
git add src/ClaudeUsageTray/Tray/StartupRegistration.cs
git commit -m "feat: per-user run-at-startup registry toggle"
```

---

### Task 8: TrayApp glue (icons, watcher, timer, context menu)

**Files:**
- Create: `src/ClaudeUsageTray/Tray/TrayApp.cs`
- Modify: `src/ClaudeUsageTray/Program.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–7 — `UsageCacheReader.TryRead(string)`, `ConfigPath.Resolve(string?)`, `SeverityRules.For(int, int, int)`, `RelativeTime.Ago/In`, `Settings` (with `DisplayMode`, `Thresholds`, `StalenessMinutes`), `IconRenderer.Render/RenderNeutral/SystemTrayIconSize`, `StartupRegistration.Enable/Disable/IsEnabled`.
- Produces: `sealed class TrayApp : ApplicationContext` (namespace `ClaudeUsageTray.Tray`) with constructor `TrayApp(Settings settings, string settingsPath)` and a `public void ShowPopup()` hook point that Task 9 fills in (stub here). `Program.Main` runs it.

WinForms glue — no unit tests; verified manually per spec §10.

- [ ] **Step 1: Write `src/ClaudeUsageTray/Tray/TrayApp.cs`**

```csharp
using System.Diagnostics;
using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Tray;

public sealed class TrayApp : ApplicationContext
{
    private const string UsagePageUrl = "https://claude.ai/settings/usage";

    private readonly Settings _settings;
    private readonly string _settingsPath;
    private readonly string _configPath;

    // Hidden control: marshals FileSystemWatcher events onto the UI thread.
    private readonly Control _sync = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 30_000 };

    private readonly ContextMenuStrip _menu;
    private ToolStripMenuItem _modeFive = null!, _modeSeven = null!, _modeBoth = null!;
    private ToolStripMenuItem _startupItem = null!, _updatedItem = null!;

    private NotifyIcon? _iconFive;
    private NotifyIcon? _iconSeven;
    private UsageSnapshot? _snapshot;

    public TrayApp(Settings settings, string settingsPath)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        _configPath = ConfigPath.Resolve(settings.ConfigPathOverride);
        _sync.CreateControl();
        _menu = BuildMenu();

        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_configPath))
            {
                SynchronizingObject = _sync,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            // Debounce ~500 ms: Claude Code rewrites the file in bursts.
            FileSystemEventHandler onChange = (_, _) => { _debounce.Stop(); _debounce.Start(); };
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Renamed += (_, _) => { _debounce.Stop(); _debounce.Start(); };
            _watcher.EnableRaisingEvents = true;
        }

        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        _tick.Tick += (_, _) => Render(); // keep relative strings & staleness current
        _tick.Start();

        ApplyDisplayMode();
        Refresh();
    }

    // ---- data ----

    private void Refresh()
    {
        _snapshot = UsageCacheReader.TryRead(_configPath);
        Render();
    }

    // ---- rendering ----

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        bool stale = _snapshot is not null
            && now - _snapshot.FetchedAt > TimeSpan.FromMinutes(_settings.StalenessMinutes);

        if (_iconFive is not null) Apply(_iconFive, '5', _snapshot?.FiveHour, "5h", clockwise: true, stale, now);
        if (_iconSeven is not null) Apply(_iconSeven, '7', _snapshot?.SevenDay, "7d", clockwise: false, stale, now);

        _updatedItem.Text = _snapshot is null
            ? "No usage data"
            : $"Updated {RelativeTime.Ago(_snapshot.FetchedAt, now)}";
    }

    private void Apply(NotifyIcon icon, char digit, WindowUsage? usage, string label, bool clockwise,
        bool stale, DateTimeOffset now)
    {
        int size = IconRenderer.SystemTrayIconSize();
        var old = icon.Icon;

        if (usage is null)
        {
            icon.Icon = IconRenderer.RenderNeutral(size);
            icon.Text = "No Claude usage data yet — run Claude Code.";
        }
        else
        {
            var severity = SeverityRules.For(usage.Percent, _settings.Thresholds.Orange, _settings.Thresholds.Red);
            icon.Icon = IconRenderer.Render(digit, usage.Percent, severity, clockwise, dimmed: stale, size);
            icon.Text = TrimTooltip(BuildTooltip(label, usage, stale, now));
        }
        old?.Dispose();
    }

    private string BuildTooltip(string label, WindowUsage usage, bool stale, DateTimeOffset now)
    {
        var parts = new List<string> { label, $"{usage.Percent}%" };
        if (usage.ResetsAt is { } resetsAt)
        {
            parts.Add($"resets in {RelativeTime.In(resetsAt, now)}");
            if (stale && resetsAt <= now) parts.Add("awaiting refresh"); // cached % may be the prior window
        }
        if (stale && _snapshot is not null)
            parts.Add($"stale · updated {RelativeTime.Ago(_snapshot.FetchedAt, now)}");
        return string.Join(" · ", parts);
    }

    private static string TrimTooltip(string text)
        => text.Length <= 127 ? text : text[..126] + "…"; // NotifyIcon.Text hard limit

    // ---- display mode / icons ----

    private void ApplyDisplayMode()
    {
        bool wantFive = _settings.DisplayMode is DisplayMode.FiveHour or DisplayMode.Both;
        bool wantSeven = _settings.DisplayMode is DisplayMode.SevenDay or DisplayMode.Both;

        // Fixed order 5 then 7: create/recreate in order so 5 registers first.
        if (!wantFive) { _iconFive?.Dispose(); _iconFive = null; }
        if (!wantSeven) { _iconSeven?.Dispose(); _iconSeven = null; }
        if (wantFive && _iconFive is null) _iconFive = CreateIcon();
        if (wantSeven && _iconSeven is null) _iconSeven = CreateIcon();

        _modeFive.Checked = _settings.DisplayMode == DisplayMode.FiveHour;
        _modeSeven.Checked = _settings.DisplayMode == DisplayMode.SevenDay;
        _modeBoth.Checked = _settings.DisplayMode == DisplayMode.Both;
    }

    private NotifyIcon CreateIcon()
    {
        var icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPopup(); };
        return icon;
    }

    /// <summary>Left-click popup; implemented in UsagePopup (later task).</summary>
    public void ShowPopup()
    {
        // Filled in by the popup task.
    }

    // ---- menu ----

    private ContextMenuStrip BuildMenu()
    {
        _modeFive = new ToolStripMenuItem("Show 5h", null, (_, _) => SetDisplayMode(DisplayMode.FiveHour));
        _modeSeven = new ToolStripMenuItem("Show 7d", null, (_, _) => SetDisplayMode(DisplayMode.SevenDay));
        _modeBoth = new ToolStripMenuItem("Show both", null, (_, _) => SetDisplayMode(DisplayMode.Both));

        _startupItem = new ToolStripMenuItem("Run at startup") { Checked = StartupRegistration.IsEnabled() };
        _startupItem.Click += (_, _) =>
        {
            if (StartupRegistration.IsEnabled()) StartupRegistration.Disable();
            else StartupRegistration.Enable();
            _startupItem.Checked = StartupRegistration.IsEnabled();
            _settings.RunAtStartup = _startupItem.Checked;
            _settings.Save(_settingsPath);
        };

        _updatedItem = new ToolStripMenuItem("Updated —") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_modeFive);
        menu.Items.Add(_modeSeven);
        menu.Items.Add(_modeBoth);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open claude.ai usage page", null, (_, _) =>
            Process.Start(new ProcessStartInfo(UsagePageUrl) { UseShellExecute = true })));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripMenuItem("Refresh now", null, (_, _) => Refresh()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_updatedItem);
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => ExitThread()));
        return menu;
    }

    private void SetDisplayMode(DisplayMode mode)
    {
        _settings.DisplayMode = mode;
        _settings.Save(_settingsPath);
        ApplyDisplayMode();
        Render();
    }

    // ---- teardown ----

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _iconFive?.Dispose();
            _iconSeven?.Dispose();
            _watcher?.Dispose();
            _debounce.Dispose();
            _tick.Dispose();
            _menu.Dispose();
            _sync.Dispose();
        }
        base.Dispose(disposing);
    }
}
```

- [ ] **Step 2: Modify `src/ClaudeUsageTray/Program.cs`** (full replacement)

```csharp
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;

namespace ClaudeUsageTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var settingsPath = Settings.DefaultPath;
        var settings = Settings.Load(settingsPath);
        Application.Run(new TrayApp(settings, settingsPath));
    }
}
```

- [ ] **Step 3: Build and run full test suite (regression)**

Run: `dotnet test`
Expected: PASS, all tests from Tasks 1–6 still green.

- [ ] **Step 4: Manual verification — live data**

Run: `dotnet run --project src/ClaudeUsageTray`
Check (with a real `%USERPROFILE%\.claude.json` present):
- Two tray icons appear: `5` (created first) and `7`, ring fill matching the cached percentages, 5h arc clockwise / 7d counter-clockwise.
- Hover each icon → tooltip like `5h · 42% · resets in 3h 10m`.
- Right-click → radio switches between Show 5h / Show 7d / Show both add/remove icons and persist across app restart (check `%APPDATA%\ClaudeUsageTray\settings.json`).
- "Refresh now" re-reads; disabled "Updated Xm ago" item shows a sane age.
- "Open claude.ai usage page" opens the browser.
- Quit removes all icons and exits the process.

- [ ] **Step 5: Manual verification — missing data & staleness**

Point the app at an empty directory: edit `%APPDATA%\ClaudeUsageTray\settings.json`, set `"configPathOverride": "C:\\temp\\nonexistent.json"`, restart the app.
- Icons show the grey neutral ring with `—`, tooltip `No Claude usage data yet — run Claude Code.`, no crash.

Then set `"stalenessMinutes": 0`, point back at the real file (`"configPathOverride": null`), restart:
- Icons render dimmed; tooltip ends with `stale · updated Xm ago`.
Restore `"stalenessMinutes": 15` afterwards.

- [ ] **Step 6: Manual verification — startup toggle and live file updates**

- Toggle "Run at startup" on: `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v ClaudeUsageTray` shows the exe path. Toggle off: value gone.
- While the tray app runs, run Claude Code briefly (or `Copy-Item $env:USERPROFILE\.claude.json $env:USERPROFILE\.claude.json -Force` won't fire; instead append+revert a byte via `(Get-Content $env:USERPROFILE\.claude.json -Raw) | Set-Content $env:USERPROFILE\.claude.json` **only if comfortable** — otherwise just use "Refresh now"): icon updates within ~1 s of a file change.

- [ ] **Step 7: Commit**

```powershell
git add src/ClaudeUsageTray/Tray/TrayApp.cs src/ClaudeUsageTray/Program.cs
git commit -m "feat: tray app with dual progress-ring icons, watcher, and context menu"
```

---

### Task 9: Left-click popup

**Files:**
- Create: `src/ClaudeUsageTray/Tray/UsagePopup.cs`
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs` (fill in `ShowPopup()`)

**Interfaces:**
- Consumes: `UsageSnapshot`, `Settings`, `SeverityRules`, `RelativeTime` (Tasks 2–5).
- Produces: `sealed class UsagePopup : Form` with constructor `UsagePopup(UsageSnapshot? snapshot, Settings settings, DateTimeOffset now)`; shows both windows as colored bars + text, closes on deactivate. `TrayApp.ShowPopup()` opens it near the cursor.

Manual verification (WinForms glue).

- [ ] **Step 1: Write `src/ClaudeUsageTray/Tray/UsagePopup.cs`**

```csharp
using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Tray;

/// <summary>Compact popup near the tray: both windows as colored bars, countdowns, last-updated line.</summary>
public sealed class UsagePopup : Form
{
    public UsagePopup(UsageSnapshot? snapshot, Settings settings, DateTimeOffset now)
    {
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Text = "Claude Usage";
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };

        if (snapshot is null)
        {
            layout.Controls.Add(new Label
            {
                Text = "No Claude usage data yet — run Claude Code.",
                AutoSize = true,
            });
        }
        else
        {
            bool stale = now - snapshot.FetchedAt > TimeSpan.FromMinutes(settings.StalenessMinutes);
            AddWindowRow(layout, "5-hour window", snapshot.FiveHour, settings, now);
            AddWindowRow(layout, "7-day window", snapshot.SevenDay, settings, now);

            var updated = $"Last updated {RelativeTime.Ago(snapshot.FetchedAt, now)}" + (stale ? " · stale" : "");
            layout.Controls.Add(new Label
            {
                Text = updated,
                AutoSize = true,
                ForeColor = stale ? Color.Firebrick : SystemColors.GrayText,
                Margin = new Padding(0, 8, 0, 0),
            });
        }

        Controls.Add(layout);
        PositionNearCursor();
    }

    private static void AddWindowRow(TableLayoutPanel layout, string title, WindowUsage? usage,
        Settings settings, DateTimeOffset now)
    {
        if (usage is null)
        {
            layout.Controls.Add(new Label { Text = $"{title}: no data", AutoSize = true });
            return;
        }

        var severity = SeverityRules.For(usage.Percent, settings.Thresholds.Orange, settings.Thresholds.Red);
        var barColor = severity switch
        {
            Severity.Red => Color.FromArgb(224, 68, 68),
            Severity.Orange => Color.FromArgb(232, 150, 40),
            _ => Color.FromArgb(64, 184, 96),
        };
        var resets = usage.ResetsAt is { } r ? $" · resets in {RelativeTime.In(r, now)}" : "";

        layout.Controls.Add(new Label
        {
            Text = $"{title} — {usage.Percent}%{resets}",
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 2),
        });

        // Custom-drawn bar (ProgressBar can't be recolored per-severity).
        var bar = new Panel { Width = 240, Height = 12, Margin = new Padding(0, 0, 0, 4) };
        int percent = Math.Clamp(usage.Percent, 0, 100);
        bar.Paint += (_, e) =>
        {
            e.Graphics.FillRectangle(SystemBrushes.ControlLight, 0, 0, bar.Width, bar.Height);
            using var brush = new SolidBrush(barColor);
            e.Graphics.FillRectangle(brush, 0, 0, bar.Width * percent / 100, bar.Height);
            e.Graphics.DrawRectangle(SystemPens.ControlDark, 0, 0, bar.Width - 1, bar.Height - 1);
        };
        layout.Controls.Add(bar);
    }

    private void PositionNearCursor()
    {
        var cursor = Cursor.Position;
        var area = Screen.FromPoint(cursor).WorkingArea;
        // Above/left of the cursor, clamped to the working area (tray is bottom-right).
        var x = Math.Max(area.Left, Math.Min(cursor.X - 130, area.Right - 280));
        var y = Math.Max(area.Top, cursor.Y - 170);
        Location = new Point(x, y);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Dispose();
    }
}
```

- [ ] **Step 2: Fill in `TrayApp.ShowPopup()`** — replace the stub in `src/ClaudeUsageTray/Tray/TrayApp.cs`:

```csharp
    private UsagePopup? _popup;

    /// <summary>Left-click popup with both windows, countdowns, and last-updated line.</summary>
    public void ShowPopup()
    {
        if (_popup is { IsDisposed: false }) { _popup.Close(); }
        _popup = new UsagePopup(_snapshot, _settings, DateTimeOffset.UtcNow);
        _popup.Show();
        _popup.Activate();
    }
```

(Remove the empty-body stub and its comment; keep the XML doc.)

- [ ] **Step 3: Build and run tests (regression)**

Run: `dotnet test`
Expected: PASS, no regressions.

- [ ] **Step 4: Manual verification**

Run: `dotnet run --project src/ClaudeUsageTray`
- Left-click either tray icon → popup near the tray shows both windows with colored bars, percentages, "resets in …" countdowns, and "Last updated Xm ago".
- Clicking anywhere else closes the popup (deactivate).
- With `configPathOverride` pointing at a nonexistent file, popup shows the no-data message.

- [ ] **Step 5: Commit**

```powershell
git add src/ClaudeUsageTray/Tray/UsagePopup.cs src/ClaudeUsageTray/Tray/TrayApp.cs
git commit -m "feat: left-click popup with usage bars, countdowns, and staleness"
```

---

### Task 10: Velopack packaging, auto-update, release script

**Files:**
- Modify: `src/ClaudeUsageTray/ClaudeUsageTray.csproj` (add Velopack package)
- Modify: `src/ClaudeUsageTray/Program.cs` (Velopack hook + update check)
- Create: `src/ClaudeUsageTray/UpdateCheck.cs`
- Create: `build/build-release.ps1`
- Create: `README.md`

**Interfaces:**
- Consumes: `StartupRegistration` (Task 7), `Settings` (Task 5), `TrayApp` (Task 8).
- Produces: an installable per-user setup exe + delta packages in `Releases/`; `static Task UpdateCheck.RunPeriodicAsync()` checking `https://github.com/wus-technik/win_systray-claude-usage` every 6 h and staging silent updates for next restart.

- [ ] **Step 1: Add the Velopack package and install the vpk tool**

```powershell
dotnet add src/ClaudeUsageTray package Velopack
dotnet tool install -g vpk
```

- [ ] **Step 2: Write `src/ClaudeUsageTray/UpdateCheck.cs`**

```csharp
using Velopack;
using Velopack.Sources;

namespace ClaudeUsageTray;

public static class UpdateCheck
{
    // Default feed: GitHub Releases of this repo. One-line swap for an internal file share:
    //   new UpdateManager(@"\\server\share\claude-usage-tray")
    // Private-repo note: reading a private GitHub Releases feed requires a token as the second
    // GithubSource argument; a public repo (or file share) needs none.
    private const string FeedUrl = "https://github.com/wus-technik/win_systray-claude-usage";

    /// <summary>Check on launch and every 6 h; stage delta updates silently, applied on next restart.</summary>
    public static async Task RunPeriodicAsync()
    {
        while (true)
        {
            try { await CheckOnceAsync(); }
            catch { /* update failures must never disturb the tray */ }
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    private static async Task CheckOnceAsync()
    {
        var manager = new UpdateManager(new GithubSource(FeedUrl, accessToken: null, prerelease: false));
        if (!manager.IsInstalled) return; // dev runs (dotnet run) are not updatable

        var updates = await manager.CheckForUpdatesAsync();
        if (updates is null) return;

        await manager.DownloadUpdatesAsync(updates);
        manager.WaitExitThenApplyUpdates(updates, silent: true, restart: false);
    }
}
```

- [ ] **Step 3: Modify `src/ClaudeUsageTray/Program.cs`** (full replacement — `VelopackApp` must be the very first call in `Main`)

```csharp
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Velopack;

namespace ClaudeUsageTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack install/update/uninstall hooks — MUST run before anything else.
        VelopackApp.Build()
            .OnFirstRun(_ =>
            {
                if (Settings.Load(Settings.DefaultPath).RunAtStartup)
                    StartupRegistration.Enable();
            })
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.Disable())
            .Run();

        ApplicationConfiguration.Initialize();
        var settingsPath = Settings.DefaultPath;
        var settings = Settings.Load(settingsPath);

        _ = UpdateCheck.RunPeriodicAsync(); // fire-and-forget; never blocks the tray

        Application.Run(new TrayApp(settings, settingsPath));
    }
}
```

Note: if the installed Velopack version has renamed these hook methods (the API has shifted between majors — older names: `WithFirstRun`, `WithBeforeUninstallFastCallback`), use the current names from IntelliSense/compile errors; the two hook bodies stay exactly as above.

- [ ] **Step 4: Write `build/build-release.ps1`**

```powershell
# Builds a Velopack release: per-user setup exe + delta packages into .\Releases
# Usage: .\build\build-release.ps1
# Upload afterwards: vpk upload github --repoUrl <repo> --token (gh auth token) --publish
#   (or copy .\Releases to \\server\share\claude-usage-tray for a file-share feed)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

[xml]$csproj = Get-Content src/ClaudeUsageTray/ClaudeUsageTray.csproj
$version = $csproj.Project.PropertyGroup.Version
if (-not $version) { throw 'No <Version> in ClaudeUsageTray.csproj' }

dotnet publish src/ClaudeUsageTray -c Release -r win-x64 --self-contained -o publish
vpk pack --packId ClaudeUsageTray --packVersion $version --packDir publish --mainExe ClaudeUsageTray.exe

Write-Host "Release $version built in .\Releases"
```

- [ ] **Step 5: Build the release and test-install it**

Run: `.\build\build-release.ps1`
Expected: `Releases\ClaudeUsageTray-win-Setup.exe` (plus `.nupkg` full package and `RELEASES` metadata) exists.

Run the setup exe:
- Installs under `%LOCALAPPDATA%\ClaudeUsageTray` **without any UAC prompt** (per-user, no admin).
- Start-menu shortcut "ClaudeUsageTray" exists.
- App launches after install; tray icons appear.
- First-run hook: `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v ClaudeUsageTray` shows the installed exe path.

- [ ] **Step 6: Verify the update path end-to-end (local feed)**

Temporarily point `FeedUrl` logic at the local output by editing `UpdateCheck.CheckOnceAsync` first line to `var manager = new UpdateManager(Path.GetFullPath("Releases"));` — **do not commit this edit**. Then:
1. Bump `<Version>` to `0.1.1` in the csproj, run `.\build\build-release.ps1` again.
2. Restart the installed app twice (first restart checks + stages, second applies).
3. Installed app version (exe properties or `%LOCALAPPDATA%\ClaudeUsageTray\current`) is `0.1.1`.
4. Revert the `UpdateManager` edit and the version bump: `git checkout -- src/ClaudeUsageTray/UpdateCheck.cs src/ClaudeUsageTray/ClaudeUsageTray.csproj` (after Step 7's commit exists, or just revert manually before committing).

- [ ] **Step 7: Write `README.md`**

```markdown
# Claude Usage Tray

Windows tray app showing the current user's Claude **5-hour** and **7-day** usage
as progress-ring icons next to the clock. Passive reader of the cache Claude Code
writes to `%USERPROFILE%\.claude.json` (`cachedUsageUtilization`) — **no network
calls to Anthropic, no tokens, no credentials**. Spec: `docs/superpowers/spec/claude-usage-tray.md`.

## Install

Run `ClaudeUsageTray-win-Setup.exe` from the latest release. Per-user install
(no admin), auto-updates via Velopack, starts at login by default.

## Usage

- **Icons:** ring fill = usage, color = severity (green < 50 %, orange 50–85 %,
  red > 85 %), center digit = window (`5` = 5-hour, `7` = 7-day). Dimmed = stale
  cache (> 15 min old). Grey `—` = no data yet (run Claude Code once).
- **Hover** an icon for the exact percentage and reset countdown.
- **Left-click** for a popup with both windows.
- **Right-click** to switch Show 5h / Show 7d / Show both, toggle run-at-startup,
  refresh, or quit.

## Settings

`%APPDATA%\ClaudeUsageTray\settings.json` (edit by hand; no UI in v1):

| Key | Meaning | Default |
|---|---|---|
| `displayMode` | `"fiveHour"` \| `"sevenDay"` \| `"both"` | `"both"` |
| `thresholds` | `{ "orange": 50, "red": 85 }` severity boundaries (%) | as shown |
| `stalenessMinutes` | minutes before cached data is flagged stale | `15` |
| `runAtStartup` | mirror of the HKCU Run key | `true` |
| `configPathOverride` | explicit path to `.claude.json` (mainly tests) | unset |

## Development

    dotnet test                              # unit tests (core logic)
    dotnet run --project src/ClaudeUsageTray # run the tray app
    .\build\build-release.ps1                # publish + vpk pack → .\Releases

Releases publish to GitHub Releases of this repo:
`vpk upload github --repoUrl https://github.com/wus-technik/win_systray-claude-usage --token (gh auth token) --publish`
```

- [ ] **Step 8: Run full test suite (regression)**

Run: `dotnet test`
Expected: PASS, all tests green.

- [ ] **Step 9: Commit**

```powershell
git add src/ClaudeUsageTray/ClaudeUsageTray.csproj src/ClaudeUsageTray/Program.cs src/ClaudeUsageTray/UpdateCheck.cs build/build-release.ps1 README.md
git commit -m "feat: Velopack packaging with per-user install and silent auto-update"
```

---

## Spec coverage map (self-review)

| Spec section | Task(s) |
|---|---|
| §2 stack (WinForms, net10.0-windows, xUnit, Velopack) | 1, 10 |
| §3 data contract (read-only parse, tolerant) | 4 |
| §4 unit decomposition | 2–9 (one unit per task) |
| §5.1 watcher + debounce + 30 s timer + refresh-now | 8 |
| §5.2 thresholds (defaults + settings-editable) | 2, 5 |
| §5.3 staleness (dimmed icon, "stale · updated", "awaiting refresh") | 8 |
| §5.4 missing data (neutral icon, no crash) | 6, 8 |
| §6.1 icons (ring, direction, digit, DPI, order, tooltip format) | 6, 8 |
| §6.2 display mode radio + persistence | 5, 8 |
| §6.3 left-click popup | 9 |
| §6.4 context menu items | 8 |
| §6.5 accessibility (fill + text redundancy) | 6, 8, 9 |
| §7 settings keys/defaults | 5 |
| §8 Velopack per-user install, first-run startup key, delta updates, feed choice | 10 |
| §9 compliance (no HTTP to Anthropic anywhere) | Global Constraints; enforced by construction — only `UpdateCheck` touches the network |
| §10 test matrix | 2–6 test steps mirror it 1:1 |
