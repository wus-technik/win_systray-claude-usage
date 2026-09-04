# Setup Stub Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `ClaudeUsageTraySetup.exe`, a ~2 MB NativeAOT launcher at one permanent GitHub URL that installs Claude Usage Tray on the stable or beta ring, switches the ring of an existing install, and is scriptable for unattended deployment via `--ring` / `--silent`.

**Architecture:** A second executable project `src/ClaudeUsageTraySetupStub/` with no WinForms and no Velopack dependency. Every decision is a pure, unit-tested static function (argument parsing, SemVer ordering, release selection, wording, install detection, current-ring resolution, the `settings.json` DOM edit, the flow decision); the thin shell around them does HTTP, file IO, process control via P/Invoke, and Win32 task dialogs. The stub never installs a package itself — it downloads the release's channel `Setup.exe` and runs it, and against an existing install it only writes `useBetaReleases` and restarts the tray. `Core/UpdateRing.cs` is linked in as shared source for the channel names. A separate test project `tests/ClaudeUsageTraySetupStub.Tests/` covers the pure functions; a new workflow publishes the exe to the permanent `setup-stub` release, and `release.yml` copies that asset onto every release.

**Tech Stack:** C# on .NET 10 (`net10.0-windows`), NativeAOT (`PublishAot`), `System.Text.Json` with a source-generated context, `System.Xml.Linq`, `Microsoft.Win32.Registry`, Win32 `TaskDialogIndirect` / `CreateProcessW` via P/Invoke, xUnit, GitHub Actions + `gh`.

**Spec:** `docs/superpowers/specs/2026-09-04-setup-stub-design.md` — read it before starting; every rule below argues from it.
**Issue:** [#21](https://github.com/wus-technik/win_systray-claude-usage/issues/21) — put `Refs #21` in every commit body.

## Global Constraints

- Build: `dotnet build ClaudeUsageTray.sln`. Test: `dotnet test tests/ClaudeUsageTraySetupStub.Tests/ClaudeUsageTraySetupStub.Tests.csproj`. Run the stub from source: `dotnet run --project src/ClaudeUsageTraySetupStub -- --help` (no MSVC needed; ILC only runs at `dotnet publish`).
- `Directory.Build.props` already sets `net10.0-windows`, `Nullable`, `ImplicitUsings`, `LangVersion latest` for every project. Do not override per project.
- Stub namespace is `ClaudeUsageTraySetupStub`; the linked `UpdateRing` keeps its own `ClaudeUsageTray.Core` namespace. The stub uses only `UpdateRing.StableChannel`, `UpdateRing.BetaChannel`, `UpdateRing.IsBetaChannel`. **It never calls `UpdateRing.For`** and never compares versions to decide a downgrade.
- **The stub test project references only the stub project.** Referencing `ClaudeUsageTray.csproj` too yields CS0433 on `UpdateRing`. Never add the stub's tests to `tests/ClaudeUsageTray.Tests`.
- Everything decidable is a pure static function with no clock, no network, no registry, no dialog. Shell code (`Program.cs`, `SetupRun.cs`, `Wizard.cs`, `NativeProcess.cs`, `ProcessControl.cs`) holds no decisions beyond calling those functions.
- AOT rules: `System.Text.Json` only through `GitHubJsonContext` (source-generated) or `JsonNode`; regexes via `[GeneratedRegex]`; no `Marshal.GetFunctionPointerForDelegate` — callbacks are `[UnmanagedCallersOnly]` function pointers; no `System.Reflection` beyond reading the entry assembly's `AssemblyInformationalVersionAttribute`.
- Asset names are always derived: `WusTechnik.ClaudeUsageTray-{channel}-Setup.exe`. Never hardcode a per-ring name.
- Exit codes exactly as the spec table: `0` converged; child code propagated verbatim; `3001` bad arguments / SYSTEM or session-0 context; `3002` ring resolution failed; `3003` download or verification failed; `3004` `--silent` with no `--ring` against an existing install; `3005` could not stop/relaunch the app or the settings write did not persist; `3006` user cancelled.
- `settings.json` is edited as a `JsonNode` DOM, key matched case-insensitively and rewritten in place, temp file + atomic move, read back after writing. A malformed file or a non-bool value is never overwritten.
- `setup.log` (`%APPDATA%\ClaudeUsageTray\setup.log`) records ring, resolved version, URL, HTTP status, outcome. **Never** the `--token` value or any credential.
- Wizard strings are English-only. The two switch messages and the resolved-build description are pure functions, not strings built in dialog code.
- Comments explain *why*, matching the density of `Core/UpdateRing.cs` and `Core/UsageApiClient.cs`. No narration comments.
- All produced text in English. No `Co-Authored-By` trailer.

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `src/ClaudeUsageTraySetupStub/ClaudeUsageTraySetupStub.csproj` | Create | NativeAOT WinExe, links `Core/UpdateRing.cs`, comctl32 v6 manifest. |
| `src/ClaudeUsageTraySetupStub/app.manifest` | Create | comctl32 v6 dependency (task dialogs), DPI awareness, UTF-8. |
| `src/ClaudeUsageTraySetupStub/ExitCode.cs` | Create | The exit-code constants. |
| `src/ClaudeUsageTraySetupStub/Rings.cs` | Create | `Ring` enum, channel ↔ ring mapping, asset-name and `/releases/latest` URL derivation, repository constant. |
| `src/ClaudeUsageTraySetupStub/CliArgs.cs` | Create | Argument parsing → `StubOptions` or an error message. |
| `src/ClaudeUsageTraySetupStub/SemVer.cs` | Create | SemVer 2 parse + ordering (prerelease identifiers numeric-aware). |
| `src/ClaudeUsageTraySetupStub/GitHubReleases.cs` | Create | GitHub release/asset DTOs, source-generated JSON context, `ReleaseSelection.Select`. |
| `src/ClaudeUsageTraySetupStub/ResolvedBuild.cs` | Create | `ResolvedBuild` record with `Describe()`, `Wording.SwitchStaged`. |
| `src/ClaudeUsageTraySetupStub/HttpRetry.cs` | Create | Send with 3 retries / exponential backoff on transient failures; injectable delays. |
| `src/ClaudeUsageTraySetupStub/ReleaseResolver.cs` | Create | Stable → redirect URL; beta → API, two distinct failure kinds, fallback unless silent. |
| `src/ClaudeUsageTraySetupStub/Downloader.cs` | Create | Streamed download with progress; `DownloadVerification` (zero-length, PE header, sha256 digest). |
| `src/ClaudeUsageTraySetupStub/InstallState.cs` | Create | `InstallPaths`, `SqVersion.Parse`, `InstallDetection` (manifest → HKCU uninstall key), `CurrentRing.Resolve`. |
| `src/ClaudeUsageTraySetupStub/SettingsEdit.cs` | Create | Pure DOM read/edit of `useBetaReleases`, reconciliation rule; `SettingsFile` atomic write + read-back. |
| `src/ClaudeUsageTraySetupStub/Decision.cs` | Create | The flow decision: ask / install / change ring / converged / ambiguous. |
| `src/ClaudeUsageTraySetupStub/SetupLog.cs` | Create | Append-only `setup.log`. |
| `src/ClaudeUsageTraySetupStub/ConsoleOutput.cs` | Create | `AttachConsole` so a WinExe can print `--help`/`--version`/silent errors. |
| `src/ClaudeUsageTraySetupStub/NativeProcess.cs` | Create | `CreateProcessW` with `CREATE_BREAKAWAY_FROM_JOB`, wait, exit code. |
| `src/ClaudeUsageTraySetupStub/ProcessControl.cs` | Create | SYSTEM/session-0 refusal, mutex probe, find/stop/relaunch the tray, `Update.exe` mid-apply check. |
| `src/ClaudeUsageTraySetupStub/Wizard.cs` | Create | `TaskDialogIndirect` P/Invoke: ring page, progress page, info/error boxes. |
| `src/ClaudeUsageTraySetupStub/SetupRun.cs` | Create | Orchestration of one run; returns the exit code. |
| `src/ClaudeUsageTraySetupStub/Program.cs` | Create | Entry point: parse, `--help`/`--version`, hand off to `SetupRun`. |
| `tests/ClaudeUsageTraySetupStub.Tests/ClaudeUsageTraySetupStub.Tests.csproj` | Create | xUnit, references the stub only. |
| `tests/ClaudeUsageTraySetupStub.Tests/*Tests.cs` | Create | One test class per pure unit (listed per task). |
| `ClaudeUsageTray.sln` | Modify | Add both projects. |
| `.github/workflows/setup-stub.yml` | Create | Publish AOT exe to the permanent `setup-stub` release (`--latest=false`, `--clobber`); `main` + paths filter. |
| `.github/workflows/release.yml` | Modify | Assert `/releases/latest` is a `v*` tag; copy `ClaudeUsageTraySetup.exe` onto each release. |
| `README.md` | Modify | Install section: canonical URL, beta, unattended flags, exit codes; design-doc table row. |
| `CLAUDE.md` | Modify | How to work on the stub: layout, CI-only publish, separate tests, the `latest` trap. |

Fifteen tasks. Tasks 1–10 are the pure units, each with its own tests; 11–12 the untestable shell (process control, dialogs); 13 wires it into a runnable exe; 14 the pipeline; 15 the docs. Nothing in 11–13 makes a decision that is not already tested in 1–10.

---

### Task 1: Project scaffold, `ExitCode`, `Rings`

**Files:**
- Create: `src/ClaudeUsageTraySetupStub/ClaudeUsageTraySetupStub.csproj`
- Create: `src/ClaudeUsageTraySetupStub/app.manifest`
- Create: `src/ClaudeUsageTraySetupStub/ExitCode.cs`
- Create: `src/ClaudeUsageTraySetupStub/Rings.cs`
- Create: `src/ClaudeUsageTraySetupStub/Program.cs` (placeholder `Main` so the WinExe builds; replaced in Task 13)
- Create: `tests/ClaudeUsageTraySetupStub.Tests/ClaudeUsageTraySetupStub.Tests.csproj`
- Create: `tests/ClaudeUsageTraySetupStub.Tests/RingsTests.cs`
- Modify: `ClaudeUsageTray.sln`

**Interfaces:**
- Consumes: `ClaudeUsageTray.Core.UpdateRing.StableChannel/BetaChannel/IsBetaChannel` (linked source).
- Produces:
  - `public enum Ring { Stable, Beta }`
  - `public static class Rings` — `string Channel(Ring)`, `Ring FromChannel(string?)`, `string SetupAssetName(string channel)`, `Uri LatestAssetUrl(string channel)`, `const string Repository = "wus-technik/win_systray-claude-usage"`, `const string ProductName = "Claude Usage Tray"`.
  - `public static class ExitCode` — `Converged = 0`, `BadArguments = 3001`, `ResolutionFailed = 3002`, `DownloadFailed = 3003`, `AmbiguousRequest = 3004`, `AppControlFailed = 3005`, `Cancelled = 3006`.

- [ ] **Step 1: Create the stub project**

`src/ClaudeUsageTraySetupStub/ClaudeUsageTraySetupStub.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <RootNamespace>ClaudeUsageTraySetupStub</RootNamespace>
    <AssemblyName>ClaudeUsageTraySetup</AssemblyName>
    <!-- The stub's own version. Independent of the app's: it is the one component that cannot
         auto-update, so support asks users for `ClaudeUsageTraySetup.exe --version`. CI appends
         the commit via -p:SourceRevisionId. -->
    <Version>1.0.0</Version>
    <Company>W&amp;S Technik GmbH</Company>
    <Authors>W&amp;S Technik GmbH</Authors>
    <Product>Claude Usage Tray Setup</Product>
    <AssemblyTitle>Claude Usage Tray Setup</AssemblyTitle>
    <Copyright>Copyright (c) 2026 W&amp;S Technik GmbH</Copyright>
    <ApplicationIcon>..\ClaudeUsageTray\app.ico</ApplicationIcon>
    <!-- comctl32 v6 is what makes TaskDialogIndirect exist at all. -->
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <!-- NativeAOT for size (~2 MB vs a 60+ MB framework-dependent-or-not exe). Needs the MSVC
         linker, so `dotnet publish` runs in CI only; `dotnet build` and the tests do not need it. -->
    <PublishAot>true</PublishAot>
    <IsAotCompatible>true</IsAotCompatible>
    <OptimizationPreference>Size</OptimizationPreference>
    <StripSymbols>true</StripSymbols>
    <InvariantGlobalization>true</InvariantGlobalization>
    <UseSystemResourceKeys>true</UseSystemResourceKeys>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <!-- Shared source, not a project reference: the stub must not carry WinForms or Velopack, and
         every asset name derives from these channel strings. The setup-stub workflow's paths filter
         lists this file so a change here rebuilds the stub. -->
    <Compile Include="..\ClaudeUsageTray\Core\UpdateRing.cs" Link="Core\UpdateRing.cs" />
  </ItemGroup>
</Project>
```

`src/ClaudeUsageTraySetupStub/app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="ClaudeUsageTraySetup" />
  <dependency>
    <dependentAssembly>
      <!-- Common Controls 6: without it TaskDialogIndirect is not exported. -->
      <assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0"
                        processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*" />
    </dependentAssembly>
  </dependency>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10 / 11 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <activeCodePage xmlns="http://schemas.microsoft.com/SMI/2019/WindowsSettings">UTF-8</activeCodePage>
    </windowsSettings>
  </application>
</assembly>
```

Placeholder `src/ClaudeUsageTraySetupStub/Program.cs` (Task 13 replaces it):

```csharp
namespace ClaudeUsageTraySetupStub;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => ExitCode.Converged;
}
```

- [ ] **Step 2: Create the test project and add both to the solution**

`tests/ClaudeUsageTraySetupStub.Tests/ClaudeUsageTraySetupStub.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <!-- The stub only. ClaudeUsageTray.csproj also exports ClaudeUsageTray.Core.UpdateRing (the stub
         links the same file), so referencing both is CS0433 on every use of the type. -->
    <ProjectReference Include="..\..\src\ClaudeUsageTraySetupStub\ClaudeUsageTraySetupStub.csproj" />
  </ItemGroup>
</Project>
```

```powershell
dotnet sln ClaudeUsageTray.sln add src/ClaudeUsageTraySetupStub/ClaudeUsageTraySetupStub.csproj
dotnet sln ClaudeUsageTray.sln add tests/ClaudeUsageTraySetupStub.Tests/ClaudeUsageTraySetupStub.Tests.csproj
dotnet build ClaudeUsageTray.sln
```

Expected: build succeeds (the stub is an empty WinExe; the test project has no tests yet). If the sln tool nests the test project under a `tests` solution folder, that is fine.

- [ ] **Step 3: Write the failing tests**

`tests/ClaudeUsageTraySetupStub.Tests/RingsTests.cs`:

```csharp
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class RingsTests
{
    [Fact]
    public void ChannelNamesComeFromTheSharedUpdateRingSource()
    {
        // The app's manifest records these; the stub must derive asset names from the same strings.
        Assert.Equal("win", Rings.Channel(Ring.Stable));
        Assert.Equal("win-beta", Rings.Channel(Ring.Beta));
    }

    [Theory]
    [InlineData("win-beta", Ring.Beta)]
    [InlineData("WIN-BETA", Ring.Beta)]
    [InlineData("win", Ring.Stable)]
    [InlineData(null, Ring.Stable)]
    public void FromChannelFollowsIsBetaChannel(string? channel, Ring expected)
        => Assert.Equal(expected, Rings.FromChannel(channel));

    [Theory]
    [InlineData("win", "WusTechnik.ClaudeUsageTray-win-Setup.exe")]
    [InlineData("win-beta", "WusTechnik.ClaudeUsageTray-win-beta-Setup.exe")]
    public void SetupAssetNameIsDerivedFromTheChannel(string channel, string expected)
        => Assert.Equal(expected, Rings.SetupAssetName(channel));

    [Fact]
    public void LatestAssetUrlUsesTheReleasesLatestRedirect()
    {
        // GitHub's redirect *is* the version independence for stable: no API call, no rate limit.
        Assert.Equal(
            "https://github.com/wus-technik/win_systray-claude-usage/releases/latest/download/WusTechnik.ClaudeUsageTray-win-beta-Setup.exe",
            Rings.LatestAssetUrl("win-beta").ToString());
    }

    [Fact]
    public void StubExitCodesCannotCollideWithSetupExeCodes()
    {
        // Setup.exe's own code is propagated verbatim, so ours live in a range it never uses.
        int[] own = [ExitCode.BadArguments, ExitCode.ResolutionFailed, ExitCode.DownloadFailed,
            ExitCode.AmbiguousRequest, ExitCode.AppControlFailed, ExitCode.Cancelled];
        Assert.Equal(own.Length, own.Distinct().Count());
        Assert.All(own, code => Assert.InRange(code, 3001, 3006));
        Assert.Equal(0, ExitCode.Converged);
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~RingsTests`
Expected: build FAILS with `CS0246: The type or namespace name 'Rings' could not be found` (and `ExitCode`).

- [ ] **Step 5: Implement `ExitCode` and `Rings`**

`src/ClaudeUsageTraySetupStub/ExitCode.cs`:

```csharp
namespace ClaudeUsageTraySetupStub;

/// <summary>Process exit codes. Setup.exe's own non-zero code is propagated unchanged when it ran,
/// so the stub's failures sit in a range no installer uses. 0 means the requested state holds — also
/// when nothing had to change, so repeated runs with the same --ring are idempotent.</summary>
public static class ExitCode
{
    public const int Converged = 0;
    /// <summary>Bad arguments, or a SYSTEM / session-0 context where a per-user install is useless.</summary>
    public const int BadArguments = 3001;
    /// <summary>API unavailable with no usable fallback, or no release carries the channel asset.</summary>
    public const int ResolutionFailed = 3002;
    /// <summary>Download failed, or the file is empty, not a PE, or its digest does not match.</summary>
    public const int DownloadFailed = 3003;
    /// <summary>--silent without --ring against an existing install: the operator never said what
    /// the desired state is, so nothing can be treated as convergence.</summary>
    public const int AmbiguousRequest = 3004;
    /// <summary>Could not stop or relaunch the tray, or the settings write did not persist.</summary>
    public const int AppControlFailed = 3005;
    public const int Cancelled = 3006;
}
```

`src/ClaudeUsageTraySetupStub/Rings.cs`:

```csharp
using ClaudeUsageTray.Core;

namespace ClaudeUsageTraySetupStub;

public enum Ring { Stable, Beta }

/// <summary>Ring ↔ Velopack channel, and everything derived from the channel string. Kept on top of
/// the linked <see cref="UpdateRing"/> constants so a renamed channel cannot desynchronise the app
/// and the installer.</summary>
public static class Rings
{
    public const string Repository = "wus-technik/win_systray-claude-usage";
    public const string ProductName = "Claude Usage Tray";

    public static string Channel(Ring ring)
        => ring == Ring.Beta ? UpdateRing.BetaChannel : UpdateRing.StableChannel;

    public static Ring FromChannel(string? channel)
        => UpdateRing.IsBetaChannel(channel) ? Ring.Beta : Ring.Stable;

    /// <summary>`vpk pack --channel X` names its installer `{packId}-{X}-Setup.exe`.</summary>
    public static string SetupAssetName(string channel) => $"WusTechnik.ClaudeUsageTray-{channel}-Setup.exe";

    /// <summary>The redirect GitHub keeps pointing at the newest non-prerelease release. Stable's whole
    /// resolution; beta's fallback when the API is unavailable (the win-beta mirror exists on every
    /// stable release, which is the third reason that mirror is mandatory).</summary>
    public static Uri LatestAssetUrl(string channel)
        => new($"https://github.com/{Repository}/releases/latest/download/{SetupAssetName(channel)}");
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~RingsTests`
Expected: PASS, 9 tests.

- [ ] **Step 7: Commit**

```bash
git add ClaudeUsageTray.sln src/ClaudeUsageTraySetupStub tests/ClaudeUsageTraySetupStub.Tests
git commit -m "feat(setup-stub): scaffold the NativeAOT setup stub with ring and exit-code constants

A second executable that links Core/UpdateRing.cs as shared source, plus its own test project,
which cannot share tests/ClaudeUsageTray.Tests without CS0433 on UpdateRing.

Refs #21"
```

---

### Task 2: `CliArgs`

**Files:**
- Create: `src/ClaudeUsageTraySetupStub/CliArgs.cs`
- Test: `tests/ClaudeUsageTraySetupStub.Tests/CliArgsTests.cs`

**Interfaces:**
- Consumes: `Ring`.
- Produces:
  - `public sealed record StubOptions(Ring? Ring, bool Silent, string? Token, bool ShowVersion, bool ShowHelp)`
  - `public sealed record ParseResult(StubOptions? Options, string? Error)` — exactly one is non-null.
  - `public static class CliArgs` — `ParseResult Parse(string[] args, string? environmentToken)`, `const string Usage`.

- [ ] **Step 1: Write the failing tests**

`tests/ClaudeUsageTraySetupStub.Tests/CliArgsTests.cs`:

```csharp
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class CliArgsTests
{
    private static StubOptions Ok(params string[] args)
    {
        var result = CliArgs.Parse(args, environmentToken: null);
        Assert.Null(result.Error);
        return result.Options!;
    }

    [Fact]
    public void NoArgumentsMeansInteractiveWithNoRingChosen()
    {
        var o = Ok();
        Assert.Null(o.Ring);
        Assert.False(o.Silent);
        Assert.Null(o.Token);
        Assert.False(o.ShowVersion);
        Assert.False(o.ShowHelp);
    }

    [Theory]
    [InlineData("--ring", "beta", Ring.Beta)]
    [InlineData("--ring", "Stable", Ring.Stable)]
    [InlineData("--RING", "BETA", Ring.Beta)]
    public void RingIsParsedCaseInsensitively(string flag, string value, Ring expected)
        => Assert.Equal(expected, Ok(flag, value).Ring);

    [Fact]
    public void RingAcceptsTheEqualsForm() => Assert.Equal(Ring.Beta, Ok("--ring=beta").Ring);

    [Fact]
    public void UnknownRingIsAnError()
    {
        var r = CliArgs.Parse(["--ring", "nightly"], null);
        Assert.Null(r.Options);
        Assert.Contains("nightly", r.Error);
    }

    [Fact]
    public void RingWithoutAValueIsAnError() => Assert.NotNull(CliArgs.Parse(["--ring"], null).Error);

    [Fact]
    public void UnknownFlagIsAnError()
    {
        var r = CliArgs.Parse(["--installto", "C:\\x"], null);
        Assert.Contains("--installto", r.Error);
    }

    [Fact]
    public void SilentAndTokenAreParsed()
    {
        var o = Ok("--silent", "--token", "ghp_abc");
        Assert.True(o.Silent);
        Assert.Equal("ghp_abc", o.Token);
    }

    [Fact]
    public void EnvironmentTokenIsUsedWhenNoFlagGiven()
        => Assert.Equal("env-token", CliArgs.Parse([], "env-token").Options!.Token);

    [Fact]
    public void TokenFlagBeatsTheEnvironment()
        => Assert.Equal("flag", CliArgs.Parse(["--token", "flag"], "env-token").Options!.Token);

    [Fact]
    public void BlankEnvironmentTokenCountsAsAbsent()
        => Assert.Null(CliArgs.Parse([], "   ").Options!.Token);

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void HelpFlags(string flag) => Assert.True(Ok(flag).ShowHelp);

    [Fact]
    public void VersionFlag() => Assert.True(Ok("--version").ShowVersion);

    [Fact]
    public void SilentWithoutRingParsesFine()
    {
        // Whether that is allowed depends on the install state, which is Decision's job, not the parser's.
        var o = Ok("--silent");
        Assert.True(o.Silent);
        Assert.Null(o.Ring);
    }

    [Fact]
    public void UsageNamesEveryFlag()
    {
        foreach (var flag in new[] { "--ring", "--silent", "--token", "--version", "--help" })
            Assert.Contains(flag, CliArgs.Usage);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~CliArgsTests`
Expected: build FAILS with `CS0246 ... 'CliArgs'`.

- [ ] **Step 3: Implement `CliArgs`**

`src/ClaudeUsageTraySetupStub/CliArgs.cs`:

```csharp
namespace ClaudeUsageTraySetupStub;

/// <summary>What the command line asked for. <c>Ring</c> null means "not said" — interactively the
/// wizard asks, silently it is stable for a fresh install and an error against an existing one.</summary>
public sealed record StubOptions(Ring? Ring, bool Silent, string? Token, bool ShowVersion, bool ShowHelp);

public sealed record ParseResult(StubOptions? Options, string? Error);

public static class CliArgs
{
    public const string Usage = """
        ClaudeUsageTraySetup.exe [--ring stable|beta] [--silent] [--token <t>] [--version] [--help]

          --ring stable|beta   Install on, or switch an existing install to, this ring.
                               Required with --silent when the app is already installed.
          --silent             No wizard; passed through to Setup.exe. Always pass --ring too.
          --token <t>          GitHub token for the release lookup (also read from GH_TOKEN).
                               Raises the per-IP API rate limit for fleet rollouts of --ring beta.
          --version            Print this installer's version and build commit.
          --help               This text.

        Exit codes: 0 done or already so; Setup.exe's own code if it failed; 3001 bad arguments or
        SYSTEM context; 3002 release lookup failed; 3003 download or verification failed;
        3004 --silent without --ring on an existing install; 3005 could not stop/restart the app
        or write its settings; 3006 cancelled.
        """;

    public static ParseResult Parse(string[] args, string? environmentToken)
    {
        Ring? ring = null;
        var silent = false;
        string? token = null;
        var showVersion = false;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? inlineValue = null;
            var equals = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                inlineValue = arg[(equals + 1)..];
                arg = arg[..equals];
            }

            switch (arg.ToLowerInvariant())
            {
                case "--ring":
                    var value = inlineValue ?? (i + 1 < args.Length ? args[++i] : null);
                    if (value is null) return Error("--ring needs a value: stable or beta.");
                    ring = value.ToLowerInvariant() switch
                    {
                        "stable" => Ring.Stable,
                        "beta" => Ring.Beta,
                        _ => null,
                    };
                    if (ring is null) return Error($"Unknown ring '{value}'. Use stable or beta.");
                    break;
                case "--silent":
                    silent = true;
                    break;
                case "--token":
                    token = inlineValue ?? (i + 1 < args.Length ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(token)) return Error("--token needs a value.");
                    break;
                case "--version":
                    showVersion = true;
                    break;
                case "--help" or "-h" or "/?":
                    showHelp = true;
                    break;
                default:
                    return Error($"Unknown argument '{arg}'.");
            }
        }

        if (token is null && !string.IsNullOrWhiteSpace(environmentToken)) token = environmentToken.Trim();
        return new ParseResult(new StubOptions(ring, silent, token, showVersion, showHelp), null);
    }

    private static ParseResult Error(string message) => new(null, message + Environment.NewLine + Environment.NewLine + Usage);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~CliArgsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTraySetupStub/CliArgs.cs tests/ClaudeUsageTraySetupStub.Tests/CliArgsTests.cs
git commit -m "feat(setup-stub): parse the command line

Refs #21"
```

---

### Task 3: `SemVer`

**Files:**
- Create: `src/ClaudeUsageTraySetupStub/SemVer.cs`
- Test: `tests/ClaudeUsageTraySetupStub.Tests/SemVerTests.cs`

**Interfaces:**
- Produces: `public sealed class SemVer : IComparable<SemVer>` with `int Major, Minor, Patch`, `IReadOnlyList<string> Prerelease`, `bool IsPrerelease`, `static SemVer? TryParse(string? text)` (accepts a leading `v`, strips `+build`, returns null for anything else), `int CompareTo(SemVer?)`, `ToString()` (`0.7.2-beta.1`).

- [ ] **Step 1: Write the failing tests**

`tests/ClaudeUsageTraySetupStub.Tests/SemVerTests.cs`:

```csharp
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class SemVerTests
{
    private static SemVer V(string s) => SemVer.TryParse(s) ?? throw new Xunit.Sdk.XunitException($"'{s}' should parse");

    [Theory]
    [InlineData("0.7.2", 0, 7, 2, false)]
    [InlineData("v0.7.2", 0, 7, 2, false)]
    [InlineData("0.7.2-beta.1", 0, 7, 2, true)]
    [InlineData("1.2.3+abc123", 1, 2, 3, false)]
    public void ParsesCoreAndPrereleaseFlag(string text, int major, int minor, int patch, bool prerelease)
    {
        var v = V(text);
        Assert.Equal((major, minor, patch, prerelease), (v.Major, v.Minor, v.Patch, v.IsPrerelease));
    }

    [Theory]
    [InlineData("setup-stub")]
    [InlineData("1.0")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0.7.2-beta 1")]
    public void NonSemVerTagsDoNotParse(string? text) => Assert.Null(SemVer.TryParse(text));

    [Fact]
    public void DotNumberedPrereleasesCompareNumerically()
        // The whole reason release-ring.ps1 rejects "-beta1": beta.10 must sort above beta.9.
        => Assert.True(V("0.7.2-beta.10").CompareTo(V("0.7.2-beta.9")) > 0);

    [Fact]
    public void StableBeatsAnyPrereleaseOfTheSameVersion()
        => Assert.True(V("0.7.2").CompareTo(V("0.7.2-beta.2")) > 0);

    [Fact]
    public void ANewerPrereleaseBeatsAnOlderStable()
        => Assert.True(V("0.7.3-beta.1").CompareTo(V("0.7.2")) > 0);

    [Fact]
    public void LongerPrereleaseWinsWhenPrefixesMatch()
        => Assert.True(V("0.7.2-beta.1").CompareTo(V("0.7.2-beta")) > 0);

    [Fact]
    public void NumericIdentifiersSortBelowAlphanumericOnes()
        => Assert.True(V("0.7.2-beta").CompareTo(V("0.7.2-1")) > 0);

    [Fact]
    public void BuildMetadataIsIgnoredInOrdering()
        => Assert.Equal(0, V("0.7.2+a").CompareTo(V("0.7.2+b")));

    [Fact]
    public void ToStringDropsTheVPrefixAndBuild()
        => Assert.Equal("0.7.2-beta.1", V("v0.7.2-beta.1+sha").ToString());
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~SemVerTests`
Expected: build FAILS with `CS0246 ... 'SemVer'`.

- [ ] **Step 3: Implement `SemVer`**

`src/ClaudeUsageTraySetupStub/SemVer.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeUsageTraySetupStub;

/// <summary>Just enough SemVer 2 to order release tags: the beta resolver picks the highest version
/// *by tag*, not by publish date, so an out-of-order hotfix cannot win. Tags that do not parse are
/// skipped by the caller — the permanent `setup-stub` release is one such tag.</summary>
public sealed partial class SemVer : IComparable<SemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public bool IsPrerelease => Prerelease.Count > 0;

    private SemVer(int major, int minor, int patch, IReadOnlyList<string> prerelease)
        => (Major, Minor, Patch, Prerelease) = (major, minor, patch, prerelease);

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();

    public static SemVer? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return null;
        var prerelease = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : [];
        return new SemVer(major, minor, patch, prerelease);
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // No prerelease outranks any prerelease of the same core version.
        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;
        var shared = Math.Min(Prerelease.Count, other.Prerelease.Count);
        for (var i = 0; i < shared; i++)
        {
            c = CompareIdentifier(Prerelease[i], other.Prerelease[i]);
            if (c != 0) return c;
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    /// <summary>SemVer §11: numeric identifiers compare as numbers and rank below alphanumeric ones;
    /// alphanumeric ones compare in ASCII order.</summary>
    private static int CompareIdentifier(string a, string b)
    {
        var aNumeric = int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var an);
        var bNumeric = int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
        if (aNumeric && bNumeric) return an.CompareTo(bn);
        if (aNumeric) return -1;
        if (bNumeric) return 1;
        return string.CompareOrdinal(a, b);
    }

    public override string ToString()
        => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{string.Join('.', Prerelease)}" : $"{Major}.{Minor}.{Patch}";
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~SemVerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTraySetupStub/SemVer.cs tests/ClaudeUsageTraySetupStub.Tests/SemVerTests.cs
git commit -m "feat(setup-stub): SemVer parsing and prerelease-aware ordering

Refs #21"
```

---

### Task 4: GitHub release DTOs and `ReleaseSelection`

**Files:**
- Create: `src/ClaudeUsageTraySetupStub/GitHubReleases.cs`
- Create: `src/ClaudeUsageTraySetupStub/ResolvedBuild.cs` (record only; `Describe` and `Wording` are Task 5)
- Test: `tests/ClaudeUsageTraySetupStub.Tests/ReleaseSelectionTests.cs`

**Interfaces:**
- Consumes: `SemVer`, `Ring`, `Rings`.
- Produces:
  - `public sealed class GitHubRelease { string? TagName; bool Draft; bool Prerelease; List<GitHubAsset>? Assets; }` and `public sealed class GitHubAsset { string? Name; string? BrowserDownloadUrl; string? Digest; }` — JSON names `tag_name`, `draft`, `prerelease`, `assets`, `name`, `browser_download_url`, `digest`.
  - `internal sealed partial class GitHubJsonContext : JsonSerializerContext` with `ListGitHubRelease`. Mark the stub assembly `[assembly: InternalsVisibleTo("ClaudeUsageTraySetupStub.Tests")]` in `GitHubReleases.cs` so the tests can use the context.
  - `public enum ResolvedVia { Api, LatestRedirect }`
  - `public sealed record ResolvedBuild(Ring Ring, string Channel, SemVer? Version, Uri Url, string? Digest, ResolvedVia Via)` with `static ResolvedBuild LatestOnChannel(Ring ring)` (redirect URL, no version, no digest).
  - `public static class ReleaseSelection` — `ResolvedBuild? Select(IEnumerable<GitHubRelease> releases, Ring ring)`.

- [ ] **Step 1: Write the failing tests**

`tests/ClaudeUsageTraySetupStub.Tests/ReleaseSelectionTests.cs`:

```csharp
using System.Text.Json;
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class ReleaseSelectionTests
{
    private static List<GitHubRelease> Parse(string json)
        => JsonSerializer.Deserialize(json, GitHubJsonContext.Default.ListGitHubRelease)!;

    private static string Release(string tag, bool prerelease, params string[] assets)
    {
        var list = string.Join(",", assets.Select(a =>
            "{ \"name\": \"" + a + "\", \"browser_download_url\": \"https://github.com/x/y/releases/download/" + tag + "/" + a + "\", \"digest\": \"sha256:0011\" }"));
        return "{ \"tag_name\": \"" + tag + "\", \"draft\": false, \"prerelease\": " + (prerelease ? "true" : "false") + ", \"assets\": [" + list + "] }";
    }

    private const string StableAsset = "WusTechnik.ClaudeUsageTray-win-Setup.exe";
    private const string BetaAsset = "WusTechnik.ClaudeUsageTray-win-beta-Setup.exe";

    [Fact]
    public void PicksTheHighestSemVerByTagNotByListOrder()
    {
        // The API lists newest-published first; a hotfix beta published after a later beta would win by date.
        var releases = Parse($"[{Release("v0.7.3-beta.2", true, BetaAsset)}, {Release("v0.7.3-beta.1", true, BetaAsset)}]");
        releases.Reverse();

        var build = ReleaseSelection.Select(releases, Ring.Beta)!;

        Assert.Equal("0.7.3-beta.2", build.Version!.ToString());
        Assert.Equal(ResolvedVia.Api, build.Via);
        Assert.Equal("win-beta", build.Channel);
        Assert.EndsWith("/v0.7.3-beta.2/" + BetaAsset, build.Url.ToString());
        Assert.Equal("sha256:0011", build.Digest);
    }

    [Fact]
    public void ReleasesWithoutTheChannelAssetAreSkipped()
    {
        var releases = Parse($"[{Release("v0.8.0", false, StableAsset)}, {Release("v0.7.2", false, StableAsset, BetaAsset)}]");

        Assert.Equal("0.7.2", ReleaseSelection.Select(releases, Ring.Beta)!.Version!.ToString());
    }

    [Fact]
    public void NonSemVerTagsAreSkippedNotFatal()
    {
        // The permanent setup-stub release carries no installer and has no version; it must be invisible.
        var releases = Parse($"[{Release("setup-stub", false, "ClaudeUsageTraySetup.exe")}, {Release("v0.7.2", false, BetaAsset)}]");

        Assert.Equal("0.7.2", ReleaseSelection.Select(releases, Ring.Beta)!.Version!.ToString());
    }

    [Fact]
    public void DraftsAreSkipped()
    {
        var json = Release("v9.9.9", true, BetaAsset).Replace("\"draft\": false", "\"draft\": true");
        var releases = Parse($"[{json}, {Release("v0.7.2", false, BetaAsset)}]");

        Assert.Equal("0.7.2", ReleaseSelection.Select(releases, Ring.Beta)!.Version!.ToString());
    }

    [Fact]
    public void TheStableMirrorCountsForTheBetaRing()
    {
        // A stable release newer than every beta is what the beta ring should get — it carries the
        // win-beta mirror precisely so beta users never fall behind.
        var releases = Parse($"[{Release("v0.7.2-beta.2", true, BetaAsset)}, {Release("v0.7.2", false, StableAsset, BetaAsset)}]");

        var build = ReleaseSelection.Select(releases, Ring.Beta)!;
        Assert.Equal("0.7.2", build.Version!.ToString());
        Assert.False(build.Version.IsPrerelease);
    }

    [Fact]
    public void NothingUsableYieldsNull()
    {
        Assert.Null(ReleaseSelection.Select([], Ring.Beta));
        Assert.Null(ReleaseSelection.Select(Parse($"[{Release("v0.7.2", false, StableAsset)}]"), Ring.Beta));
    }

    [Fact]
    public void AssetNameMatchIsCaseInsensitive()
    {
        var releases = Parse($"[{Release("v0.7.2", false, BetaAsset.ToUpperInvariant())}]");
        Assert.NotNull(ReleaseSelection.Select(releases, Ring.Beta));
    }

    [Fact]
    public void LatestOnChannelHasNoVersionOrDigest()
    {
        var build = ResolvedBuild.LatestOnChannel(Ring.Beta);
        Assert.Null(build.Version);
        Assert.Null(build.Digest);
        Assert.Equal(ResolvedVia.LatestRedirect, build.Via);
        Assert.Equal(Rings.LatestAssetUrl("win-beta"), build.Url);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~ReleaseSelectionTests`
Expected: build FAILS (`GitHubRelease`, `ReleaseSelection`, `ResolvedBuild` unknown).

- [ ] **Step 3: Implement the DTOs, JSON context, record and selection**

`src/ClaudeUsageTraySetupStub/GitHubReleases.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("ClaudeUsageTraySetupStub.Tests")]

namespace ClaudeUsageTraySetupStub;

/// <summary>The fields of `GET /repos/{owner}/{repo}/releases` the resolver reads. Classes, not
/// records, so the source generator can populate them without a constructor contract.</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    /// <summary>`sha256:&lt;hex&gt;`, reported by the API since 2025. Null on older assets.</summary>
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

/// <summary>Reflection-based System.Text.Json is not AOT-safe; this is the whole JSON surface.</summary>
[JsonSerializable(typeof(List<GitHubRelease>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;

public static class ReleaseSelection
{
    /// <summary>The newest release, by parsed tag, that carries the ring's Setup.exe. Drafts and
    /// tags that are not SemVer are skipped. Null when no release carries the asset — which is a
    /// hard error for the caller, not a fallback case.</summary>
    public static ResolvedBuild? Select(IEnumerable<GitHubRelease> releases, Ring ring)
    {
        var channel = Rings.Channel(ring);
        var assetName = Rings.SetupAssetName(channel);
        ResolvedBuild? best = null;
        foreach (var release in releases)
        {
            if (release.Draft) continue;
            var version = SemVer.TryParse(release.TagName);
            if (version is null) continue;
            var asset = release.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (asset?.BrowserDownloadUrl is null
                || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url)) continue;
            if (best is null || version.CompareTo(best.Version) > 0)
                best = new ResolvedBuild(ring, channel, version, url, asset.Digest, ResolvedVia.Api);
        }
        return best;
    }
}
```

`src/ClaudeUsageTraySetupStub/ResolvedBuild.cs` (Task 5 adds `Describe` and `Wording` to this file):

```csharp
namespace ClaudeUsageTraySetupStub;

/// <summary>How the build was found. The redirect carries no version and no digest, and for the beta
/// ring it means "the API was unavailable" — which the wizard has to say out loud.</summary>
public enum ResolvedVia { Api, LatestRedirect }

public sealed record ResolvedBuild(Ring Ring, string Channel, SemVer? Version, Uri Url, string? Digest, ResolvedVia Via)
{
    /// <summary>The `/releases/latest` redirect for the ring's channel: stable's only resolution, and
    /// beta's fallback. Content is the latest stable build either way.</summary>
    public static ResolvedBuild LatestOnChannel(Ring ring)
    {
        var channel = Rings.Channel(ring);
        return new ResolvedBuild(ring, channel, null, Rings.LatestAssetUrl(channel), null, ResolvedVia.LatestRedirect);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~ReleaseSelectionTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTraySetupStub/GitHubReleases.cs src/ClaudeUsageTraySetupStub/ResolvedBuild.cs tests/ClaudeUsageTraySetupStub.Tests/ReleaseSelectionTests.cs
git commit -m "feat(setup-stub): select the newest release carrying the channel installer

Ordered by parsed tag, not publish date; drafts and non-SemVer tags (the setup-stub release
itself) are skipped.

Refs #21"
```

---

### Task 5: `ResolvedBuild.Describe` and `Wording`

**Files:**
- Modify: `src/ClaudeUsageTraySetupStub/ResolvedBuild.cs`
- Test: `tests/ClaudeUsageTraySetupStub.Tests/WordingTests.cs`

**Interfaces:**
- Produces: `string ResolvedBuild.Describe()`; `public static class Wording` — `string SwitchStaged(Ring target)`, `string Installed(ResolvedBuild build)`.

- [ ] **Step 1: Write the failing tests**

`tests/ClaudeUsageTraySetupStub.Tests/WordingTests.cs`:

```csharp
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class WordingTests
{
    private static ResolvedBuild Api(Ring ring, string version) => new(ring, Rings.Channel(ring), SemVer.TryParse(version),
        new Uri("https://example.test/x.exe"), "sha256:00", ResolvedVia.Api);

    [Fact]
    public void StableNamesTheLatestStableRelease()
        => Assert.Contains("latest stable", ResolvedBuild.LatestOnChannel(Ring.Stable).Describe());

    [Fact]
    public void BetaPrereleaseNamesTheVersionAndSaysPreRelease()
    {
        var text = Api(Ring.Beta, "0.7.3-beta.1").Describe();
        Assert.Contains("0.7.3-beta.1", text);
        Assert.Contains("pre-release build", text);
    }

    [Fact]
    public void BetaOnTheStableMirrorSaysSoPlainly()
    {
        // The user asked for beta and is getting stable content; hiding that is the failure the spec forbids.
        var text = Api(Ring.Beta, "0.7.2").Describe();
        Assert.Contains("0.7.2", text);
        Assert.Contains("stable build", text);
        Assert.DoesNotContain("pre-release build", text);
    }

    [Fact]
    public void BetaViaFallbackSaysTheApiWasUnavailable()
    {
        var text = ResolvedBuild.LatestOnChannel(Ring.Beta).Describe();
        Assert.Contains("could not be read", text);
        Assert.Contains("stable build", text);
        Assert.Contains("newer pre-release may exist", text);
    }

    [Fact]
    public void SwitchToBetaPromisesStagingNotACompletedMove()
    {
        var text = Wording.SwitchStaged(Ring.Beta);
        Assert.StartsWith("Beta releases enabled.", text);
        Assert.Contains("Restart to update", text);
        Assert.Contains("in the background", text);
    }

    [Fact]
    public void SwitchToStableWarnsAboutTheDowngrade()
    {
        var text = Wording.SwitchStaged(Ring.Stable);
        Assert.StartsWith("Beta releases disabled.", text);
        Assert.Contains("older version", text);
        Assert.Contains("Restart to update", text);
    }

    [Fact]
    public void InstalledWordingNamesTheRing()
    {
        Assert.Contains("beta ring", Wording.Installed(Api(Ring.Beta, "0.7.3-beta.1")));
        Assert.Contains("stable ring", Wording.Installed(ResolvedBuild.LatestOnChannel(Ring.Stable)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~WordingTests`
Expected: build FAILS (`Describe`, `Wording` unknown).

- [ ] **Step 3: Add `Describe` and `Wording`**

Append to the `ResolvedBuild` record body in `src/ClaudeUsageTraySetupStub/ResolvedBuild.cs`:

```csharp
    /// <summary>What is about to be installed, shown before the download. A decision, not
    /// string-building: the beta ring has two ways of ending up with stable content (no newer
    /// prerelease exists; the API was unavailable) and both must be said, or a user told "beta" who
    /// got stable has no reason to expect anything else.</summary>
    public string Describe()
    {
        if (Ring == Ring.Stable)
            return $"Installing the latest stable release of {Rings.ProductName}.";
        if (Via == ResolvedVia.LatestRedirect)
            return "GitHub's release list could not be read, so this installs the latest stable build on the beta ring. " +
                   "A newer pre-release may exist; the app's own update check will offer it once installed.";
        if (Version is { IsPrerelease: true })
            return $"Installing {Rings.ProductName} {Version} — a pre-release build on the beta ring.";
        return $"Installing {Rings.ProductName} {Version} on the beta ring. This is the current stable build — " +
               "no pre-release is newer than it. Pre-releases will be offered by the app as they appear.";
    }
```

And add, after the record, in the same file:

```csharp
/// <summary>The messages whose wording is a decision. Both switch texts describe *staging*: the app
/// checks on launch and downloads only, and applying is the user's explicit "Restart to update"
/// (UpdateCheck.cs). Telling a user "you are now on beta" while they still run stable would leave
/// them with no reason to look for that prompt.</summary>
public static class Wording
{
    public static string SwitchStaged(Ring target) => target == Ring.Beta
        ? $"Beta releases enabled. {Rings.ProductName} will download the next beta build in the background and " +
          "offer Restart to update when it is ready."
        : $"Beta releases disabled. {Rings.ProductName} will return to the latest stable build — which may be an " +
          "older version than the beta you are running — and offer Restart to update when it is ready.";

    public static string Installed(ResolvedBuild build)
    {
        var ring = build.Ring == Ring.Beta ? "beta ring" : "stable ring";
        var version = build.Version is null ? "" : $" {build.Version}";
        return $"{Rings.ProductName}{version} is installed on the {ring} and will keep itself up to date.";
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~WordingTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTraySetupStub/ResolvedBuild.cs tests/ClaudeUsageTraySetupStub.Tests/WordingTests.cs
git commit -m "feat(setup-stub): wording for the resolved build and the ring switch

Both switch messages describe staging, not a completed move: the app downloads in the background
and applies only on the user's Restart to update.

Refs #21"
```

---

### Task 6: `HttpRetry` and `ReleaseResolver`

**Files:**
- Create: `src/ClaudeUsageTraySetupStub/HttpRetry.cs`
- Create: `src/ClaudeUsageTraySetupStub/ReleaseResolver.cs`
- Test: `tests/ClaudeUsageTraySetupStub.Tests/ReleaseResolverTests.cs`

**Interfaces:**
- Consumes: `ResolvedBuild`, `ReleaseSelection`, `GitHubJsonContext`, `Ring`, `Rings`.
- Produces:
  - `public static class HttpRetry` — `IReadOnlyList<TimeSpan> DefaultDelays` (2 s, 4 s, 8 s), `Task<HttpResponseMessage?> SendAsync(HttpClient http, Func<HttpRequestMessage> request, IReadOnlyList<TimeSpan> delays, HttpCompletionOption completion, CancellationToken ct)` — null when every attempt threw; the last response otherwise. `bool IsTransient(HttpStatusCode)`. `string UserAgent`.
  - `public enum ResolveFailure { None, ApiUnavailable, NoAssetInAnyRelease }`
  - `public sealed record ResolveResult(ResolvedBuild? Build, ResolveFailure Failure, string Detail)`
  - `public static class ReleaseResolver` — `const string ReleasesApiUrl`, `Task<ResolveResult> ResolveAsync(HttpClient http, Ring ring, string? token, bool silent, IReadOnlyList<TimeSpan> retryDelays, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

`tests/ClaudeUsageTraySetupStub.Tests/ReleaseResolverTests.cs`:

```csharp
using System.Net;
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class ReleaseResolverTests
{
    /// <summary>Canned responses in order; the last one repeats. Records every request.</summary>
    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var responder = responders[Math.Min(Requests.Count - 1, responders.Length - 1)];
            return Task.FromResult(responder(request));
        }
    }

    private static readonly TimeSpan[] NoDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private const string BetaAsset = "WusTechnik.ClaudeUsageTray-win-beta-Setup.exe";
    private const string StableAsset = "WusTechnik.ClaudeUsageTray-win-Setup.exe";

    private static string Releases(params (string Tag, string Asset)[] releases)
        => "[" + string.Join(",", releases.Select(r =>
            "{ \"tag_name\": \"" + r.Tag + "\", \"draft\": false, \"prerelease\": true, \"assets\": [" +
            "{ \"name\": \"" + r.Asset + "\", \"browser_download_url\": \"https://github.com/o/r/releases/download/" + r.Tag + "/" + r.Asset + "\", \"digest\": \"sha256:ab\" }] }")) + "]";

    private static (ResolveResult Result, FakeHandler Handler) Resolve(Ring ring, bool silent, string? token, params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
    {
        var handler = new FakeHandler(responders.Length == 0 ? [_ => Json(HttpStatusCode.OK, "[]")] : responders);
        using var http = new HttpClient(handler);
        var result = ReleaseResolver.ResolveAsync(http, ring, token, silent, NoDelays, CancellationToken.None).GetAwaiter().GetResult();
        return (result, handler);
    }

    [Fact]
    public void StableNeverTouchesTheApi()
    {
        var (r, h) = Resolve(Ring.Stable, silent: true, token: null);
        Assert.Empty(h.Requests);
        Assert.Equal(ResolveFailure.None, r.Failure);
        Assert.Equal(Rings.LatestAssetUrl("win"), r.Build!.Url);
        Assert.Equal(ResolvedVia.LatestRedirect, r.Build.Via);
    }

    [Fact]
    public void BetaSelectsFromTheApi()
    {
        var (r, h) = Resolve(Ring.Beta, false, null, _ => Json(HttpStatusCode.OK, Releases(("v0.7.3-beta.1", BetaAsset))));
        Assert.Equal("0.7.3-beta.1", r.Build!.Version!.ToString());
        Assert.Equal(ResolvedVia.Api, r.Build.Via);
        Assert.Equal(ReleaseResolver.ReleasesApiUrl, h.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public void RequestCarriesTheGitHubHeaders()
    {
        var (_, h) = Resolve(Ring.Beta, false, "tok", _ => Json(HttpStatusCode.OK, "[]"));
        var req = h.Requests.Single();
        Assert.Equal("application/vnd.github+json", req.Headers.Accept.Single().MediaType);
        Assert.Equal("2022-11-28", req.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.StartsWith("ClaudeUsageTraySetup/", req.Headers.UserAgent.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("tok", req.Headers.Authorization.Parameter);
    }

    [Fact]
    public void NoTokenMeansNoAuthorizationHeader()
    {
        var (_, h) = Resolve(Ring.Beta, false, null, _ => Json(HttpStatusCode.OK, "[]"));
        Assert.Null(h.Requests.Single().Headers.Authorization);
    }

    [Fact]
    public void ApiFineButNoAssetIsAHardError()
    {
        // Nothing to fall back to; installing the other ring's content would be wrong.
        var (r, _) = Resolve(Ring.Beta, silent: false, null, _ => Json(HttpStatusCode.OK, Releases(("v0.7.2", StableAsset))));
        Assert.Null(r.Build);
        Assert.Equal(ResolveFailure.NoAssetInAnyRelease, r.Failure);
    }

    [Fact]
    public void ApiUnavailableInteractiveFallsBackToTheLatestRedirect()
    {
        var (r, _) = Resolve(Ring.Beta, silent: false, null, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Equal(ResolveFailure.None, r.Failure);
        Assert.Equal(ResolvedVia.LatestRedirect, r.Build!.Via);
        Assert.Equal(Rings.LatestAssetUrl("win-beta"), r.Build.Url);
        Assert.Contains("503", r.Detail);
    }

    [Fact]
    public void ApiUnavailableSilentFailsClosed()
    {
        // An operator asked for beta; silently installing stable content behind their back is the one thing not to do.
        var (r, _) = Resolve(Ring.Beta, silent: true, null, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Null(r.Build);
        Assert.Equal(ResolveFailure.ApiUnavailable, r.Failure);
    }

    [Fact]
    public void RateLimitedCountsAsUnavailableNotAsNoAsset()
    {
        var (r, _) = Resolve(Ring.Beta, silent: true, null, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        Assert.Equal(ResolveFailure.ApiUnavailable, r.Failure);
    }

    [Fact]
    public void TransientFailuresAreRetriedThreeTimes()
    {
        var (r, h) = Resolve(Ring.Beta, false, null,
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => Json(HttpStatusCode.OK, Releases(("v0.7.3-beta.1", BetaAsset))));
        Assert.Equal(3, h.Requests.Count);
        Assert.Equal("0.7.3-beta.1", r.Build!.Version!.ToString());
    }

    [Fact]
    public void GivesUpAfterTheConfiguredRetries()
    {
        var (_, h) = Resolve(Ring.Beta, true, null, _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        Assert.Equal(1 + NoDelays.Length, h.Requests.Count);
    }

    [Fact]
    public void NetworkExceptionsCountAsUnavailable()
    {
        var (r, _) = Resolve(Ring.Beta, true, null, _ => throw new HttpRequestException("dns"));
        Assert.Equal(ResolveFailure.ApiUnavailable, r.Failure);
    }

    [Fact]
    public void MalformedJsonCountsAsUnavailable()
    {
        var (r, _) = Resolve(Ring.Beta, true, null, _ => Json(HttpStatusCode.OK, "{ not json"));
        Assert.Equal(ResolveFailure.ApiUnavailable, r.Failure);
    }

    [Fact]
    public void DetailNeverContainsTheToken()
    {
        var (r, _) = Resolve(Ring.Beta, true, "sekrit", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.DoesNotContain("sekrit", r.Detail);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~ReleaseResolverTests`
Expected: build FAILS (`ReleaseResolver`, `ResolveFailure` unknown).

- [ ] **Step 3: Implement `HttpRetry` and `ReleaseResolver`**

`src/ClaudeUsageTraySetupStub/HttpRetry.cs`:

```csharp
using System.Net;
using System.Reflection;

namespace ClaudeUsageTraySetupStub;

/// <summary>Three retries with exponential backoff on transient failures — 5xx, 429, 408, connection
/// errors, timeouts. Delays are a parameter so tests run with zeros. 4xx other than those are
/// returned at once: retrying a 401 or 404 only burns the rate limit.</summary>
public static class HttpRetry
{
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    /// <summary>GitHub rejects requests without a User-Agent. The version is the stub's own.</summary>
    public static readonly string UserAgent =
        $"ClaudeUsageTraySetup/{Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0"}";

    public static bool IsTransient(HttpStatusCode status)
        => (int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;

    /// <summary>Null when every attempt threw; otherwise the last response (which may still be a
    /// transient failure if the retries ran out). The request is rebuilt per attempt because an
    /// HttpRequestMessage cannot be sent twice.</summary>
    public static async Task<HttpResponseMessage?> SendAsync(
        HttpClient http, Func<HttpRequestMessage> request, IReadOnlyList<TimeSpan> delays,
        HttpCompletionOption completion, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await http.SendAsync(request(), completion, ct).ConfigureAwait(false);
                if (!IsTransient(response.StatusCode)) return response;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* HttpClient timeout */ }

            if (attempt >= delays.Count) return response;
            response?.Dispose();
            await Task.Delay(delays[attempt], ct).ConfigureAwait(false);
        }
    }
}
```

`src/ClaudeUsageTraySetupStub/ReleaseResolver.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeUsageTraySetupStub;

/// <summary>Why no build was resolved. The two kinds must never be collapsed: *unavailable* has a
/// fallback (interactively), *no asset anywhere* has nothing to fall back to and installing the
/// other ring's content would be wrong.</summary>
public enum ResolveFailure { None, ApiUnavailable, NoAssetInAnyRelease }

public sealed record ResolveResult(ResolvedBuild? Build, ResolveFailure Failure, string Detail);

/// <summary>Finds the newest release for a ring. Stable needs no API call — GitHub's
/// `/releases/latest` redirect is the version-independence. Beta cannot avoid one, because betas are
/// GitHub pre-releases and `/releases/latest` skips them by definition.</summary>
public static class ReleaseResolver
{
    /// <summary>One page of 100 is the whole query: years of releases at this cadence.</summary>
    public const string ReleasesApiUrl = "https://api.github.com/repos/" + Rings.Repository + "/releases?per_page=100";

    public static async Task<ResolveResult> ResolveAsync(
        HttpClient http, Ring ring, string? token, bool silent, IReadOnlyList<TimeSpan> retryDelays, CancellationToken ct)
    {
        if (ring == Ring.Stable)
            return new ResolveResult(ResolvedBuild.LatestOnChannel(Ring.Stable), ResolveFailure.None, "stable: /releases/latest redirect, no API call");

        using var response = await HttpRetry.SendAsync(http, () => BuildRequest(token), retryDelays,
            HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (response is null || !response.IsSuccessStatusCode)
            return Unavailable(response is null ? "no response" : $"HTTP {(int)response.StatusCode}", silent);

        List<GitHubRelease>? releases;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            releases = await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.ListGitHubRelease, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Unavailable("HTTP 200 with unreadable JSON", silent);
        }

        var build = ReleaseSelection.Select(releases ?? [], ring);
        return build is null
            ? new ResolveResult(null, ResolveFailure.NoAssetInAnyRelease,
                $"HTTP 200, {releases?.Count ?? 0} releases, none carries {Rings.SetupAssetName(Rings.Channel(ring))}")
            : new ResolveResult(build, ResolveFailure.None, $"HTTP 200; selected {build.Version}");
    }

    /// <summary>Interactively the beta ring degrades to the latest stable build on the win-beta
    /// channel — the wizard says so via Describe. Silently it fails closed: an operator who asked for
    /// beta must not get stable content behind their back.</summary>
    private static ResolveResult Unavailable(string detail, bool silent) => silent
        ? new ResolveResult(null, ResolveFailure.ApiUnavailable, $"GitHub API unavailable ({detail}); --silent does not fall back")
        : new ResolveResult(ResolvedBuild.LatestOnChannel(Ring.Beta), ResolveFailure.None,
            $"GitHub API unavailable ({detail}); falling back to the latest stable build on win-beta");

    private static HttpRequestMessage BuildRequest(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd(HttpRetry.UserAgent);
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~ReleaseResolverTests`
Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTraySetupStub/HttpRetry.cs src/ClaudeUsageTraySetupStub/ReleaseResolver.cs tests/ClaudeUsageTraySetupStub.Tests/ReleaseResolverTests.cs
git commit -m "feat(setup-stub): resolve the newest release per ring

Stable uses the /releases/latest redirect with no API call. Beta queries the API, retries
transient failures, and keeps 'API unavailable' (fallback, unless --silent) distinct from 'no
release carries the asset' (hard error).

Refs #21"
```

---

### Task 7: `Downloader` and `DownloadVerification`

**Files:**
- Create: `src/ClaudeUsageTraySetupStub/Downloader.cs`
- Test: `tests/ClaudeUsageTraySetupStub.Tests/DownloaderTests.cs`

**Interfaces:**
- Consumes: `HttpRetry`.
- Produces:
  - `public static class Downloader` — `Task<bool> DownloadAsync(HttpClient http, Uri url, string destinationPath, IReadOnlyList<TimeSpan> retryDelays, IProgress<double>? progress, CancellationToken ct)`.
  - `public enum VerifyOutcome { Ok, Empty, NotExecutable, DigestMismatch }`
  - `public static class DownloadVerification` — `VerifyOutcome Verify(string path, string? expectedDigest)`, `bool DigestMatches(string expected, string actualSha256Hex)`.

- [ ] **Step 1: Write the failing tests**

`tests/ClaudeUsageTraySetupStub.Tests/DownloaderTests.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class DownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stub-tests-" + Guid.NewGuid().ToString("N"));
    public DownloaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] FakePe(int length)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    private static string Sha256Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ---- verification ----

    [Fact]
    public void ZeroLengthIsRejected()
        => Assert.Equal(VerifyOutcome.Empty, DownloadVerification.Verify(Write("a.exe", []), null));

    [Fact]
    public void MissingFileIsRejectedAsEmpty()
        => Assert.Equal(VerifyOutcome.Empty, DownloadVerification.Verify(Path.Combine(_dir, "nope.exe"), null));

    [Fact]
    public void NonPeIsRejected()
        => Assert.Equal(VerifyOutcome.NotExecutable, DownloadVerification.Verify(Write("a.exe", "<html>rate limited</html>"u8.ToArray()), null));

    [Fact]
    public void PeWithoutDigestPasses()
    {
        // The stable path has no digest to check (redirect, no API call); TLS is the trust anchor there.
        Assert.Equal(VerifyOutcome.Ok, DownloadVerification.Verify(Write("a.exe", FakePe(64)), null));
    }

    [Fact]
    public void MatchingDigestPasses()
    {
        var bytes = FakePe(4096);
        Assert.Equal(VerifyOutcome.Ok, DownloadVerification.Verify(Write("a.exe", bytes), Sha256Digest(bytes).ToUpperInvariant()));
    }

    [Fact]
    public void MismatchedDigestFails()
    {
        var bytes = FakePe(4096);
        Assert.Equal(VerifyOutcome.DigestMismatch, DownloadVerification.Verify(Write("a.exe", bytes), Sha256Digest(FakePe(4095))));
    }

    [Fact]
    public void UnknownDigestAlgorithmFailsClosed()
        => Assert.False(DownloadVerification.DigestMatches("md5:abc", "abc"));

    // ---- download ----

    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var responder = responders[Math.Min(Calls, responders.Length - 1)];
            Calls++;
            return Task.FromResult(responder(request));
        }
    }

    private static readonly TimeSpan[] NoDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    private static HttpResponseMessage Bytes(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentLength = bytes.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public void DownloadsToTheDestinationAndReportsProgress()
    {
        var bytes = FakePe(200_000);
        var handler = new FakeHandler(_ => Bytes(bytes));
        using var http = new HttpClient(handler);
        var reported = new List<double>();
        var destination = Path.Combine(_dir, "setup.exe");

        var ok = Downloader.DownloadAsync(http, new Uri("https://example.test/s.exe"), destination, NoDelays,
            new Progress<double>(reported.Add), CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(ok);
        Assert.Equal(bytes, File.ReadAllBytes(destination));
        // Progress<T> posts to the thread pool; give it a moment, then only check it ended at 1.
        SpinWait.SpinUntil(() => reported.Contains(1.0), TimeSpan.FromSeconds(2));
        Assert.Contains(1.0, reported);
    }

    [Fact]
    public void TransientFailureIsRetried()
    {
        var bytes = FakePe(1024);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), _ => Bytes(bytes));
        using var http = new HttpClient(handler);

        var ok = Downloader.DownloadAsync(http, new Uri("https://example.test/s.exe"), Path.Combine(_dir, "s.exe"), NoDelays, null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(ok);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void NotFoundIsNotRetriedAndFails()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);

        var ok = Downloader.DownloadAsync(http, new Uri("https://example.test/s.exe"), Path.Combine(_dir, "s.exe"), NoDelays, null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(ok);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void RequestCarriesAUserAgent()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHandler(r => { seen = r; return Bytes(FakePe(16)); });
        using var http = new HttpClient(handler);
        Downloader.DownloadAsync(http, new Uri("https://example.test/s.exe"), Path.Combine(_dir, "s.exe"), NoDelays, null, CancellationToken.None).GetAwaiter().GetResult();
        Assert.StartsWith("ClaudeUsageTraySetup/", seen!.Headers.UserAgent.ToString());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~DownloaderTests`
Expected: build FAILS (`Downloader`, `DownloadVerification` unknown).

- [ ] **Step 3: Implement**

`src/ClaudeUsageTraySetupStub/Downloader.cs`:

```csharp
using System.Security.Cryptography;

namespace ClaudeUsageTraySetupStub;

public static class Downloader
{
    /// <summary>Streams the asset to <paramref name="destinationPath"/>. False on any failure; the
    /// caller decides whether a partial file matters (it deletes the whole temp directory anyway).
    /// No resume: a 58 MB retry is cheaper than the state to track one.</summary>
    public static async Task<bool> DownloadAsync(
        HttpClient http, Uri url, string destinationPath, IReadOnlyList<TimeSpan> retryDelays,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await HttpRetry.SendAsync(http, () => BuildRequest(url), retryDelays,
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response is null || !response.IsSuccessStatusCode) return false;

        try
        {
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total is > 0) progress?.Report(Math.Min(1.0, (double)done / total.Value));
            }
            progress?.Report(1.0);
            return true;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static HttpRequestMessage BuildRequest(Uri url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(HttpRetry.UserAgent);
        return request;
    }
}

public enum VerifyOutcome { Ok, Empty, NotExecutable, DigestMismatch }

/// <summary>The checks the stub *can* make on what it is about to execute. Nothing this project ships
/// is code-signed, so there is no Authenticode to verify; the trust anchor is TLS to github.com plus
/// the API's per-asset sha256 digest where one exists.</summary>
public static class DownloadVerification
{
    public static VerifyOutcome Verify(string path, string? expectedDigest)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) return VerifyOutcome.Empty;

        using var stream = File.OpenRead(path);
        var header = new byte[2];
        // A rate-limit HTML page or an error body saved as .exe must never be executed.
        if (stream.Read(header, 0, 2) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            return VerifyOutcome.NotExecutable;
        if (expectedDigest is null) return VerifyOutcome.Ok;

        stream.Position = 0;
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        return DigestMatches(expectedDigest, actual) ? VerifyOutcome.Ok : VerifyOutcome.DigestMismatch;
    }

    /// <summary>`sha256:&lt;hex&gt;` only. Any other algorithm fails closed: a digest the stub cannot
    /// check is not a digest it may ignore.</summary>
    public static bool DigestMatches(string expected, string actualSha256Hex)
    {
        const string prefix = "sha256:";
        return expected.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected[prefix.Length..].Trim(), actualSha256Hex, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~DownloaderTests`
Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTraySetupStub/Downloader.cs tests/ClaudeUsageTraySetupStub.Tests/DownloaderTests.cs
git commit -m "feat(setup-stub): download the installer and verify it before execution

Zero-length and non-PE files are refused; the API's sha256 digest is checked when present.

Refs #21"
```

---

### Task 8: `InstallPaths`, `SqVersion`, `InstallDetection`, `CurrentRing`

**Files:**
- Create: `src/ClaudeUsageTraySetupStub/InstallState.cs`
- Test: `tests/ClaudeUsageTraySetupStub.Tests/InstallStateTests.cs`

**Interfaces:**
- Consumes: `Ring`, `UpdateRing.IsBetaChannel`.
- Produces:
  - `public static class InstallPaths` — `const string PackId = "WusTechnik.ClaudeUsageTray"`, `const string MutexName = @"Local\WusTechnik.ClaudeUsageTray"`, `const string ExeName = "ClaudeUsageTray"`, `string DefaultRoot` (`%LOCALAPPDATA%\WusTechnik.ClaudeUsageTray`), `string CurrentExe(string root)`, `string Manifest(string root)` (`current\sq.version`), `string UpdateExe(string root)`.
  - `public sealed record InstallManifest(string Version, string? Channel)`
  - `public static class SqVersion` — `InstallManifest? Parse(string xml)`.
  - `public sealed record InstallInfo(string Version, string? Channel)`
  - `public static class InstallDetection` — `InstallInfo? Detect(string root, Func<string?> uninstallKeyVersion)`, `string? ReadUninstallKeyVersion()` (HKCU registry; the production `Func`).
  - `public static class CurrentRing` — `Ring Resolve(bool? useBetaReleases, string? manifestChannel)`.

- [ ] **Step 1: Write the failing tests**

`tests/ClaudeUsageTraySetupStub.Tests/InstallStateTests.cs`:

```csharp
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class InstallStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stub-root-" + Guid.NewGuid().ToString("N"));
    public InstallStateTests() => Directory.CreateDirectory(Path.Combine(_root, "current"));
    public void Dispose() => Directory.Delete(_root, recursive: true);

    // The real file vpk writes: a nuspec with a default namespace, which XPath-by-name would miss.
    private const string RealManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
        <metadata>
        <id>WusTechnik.ClaudeUsageTray</id>
        <title>Claude Usage Tray</title>
        <version>0.7.2</version>
        <channel>win-beta</channel>
        <mainExe>ClaudeUsageTray.exe</mainExe>
        <releaseNotes><![CDATA[ notes ]]></releaseNotes>
        </metadata>
        </package>
        """;

    [Fact]
    public void ParsesVersionAndChannelFromTheNamespacedNuspec()
        => Assert.Equal(new InstallManifest("0.7.2", "win-beta"), SqVersion.Parse(RealManifest));

    [Fact]
    public void MissingChannelIsNullNotStable()
    {
        // Older manifests may lack it; the caller decides what null means.
        var xml = RealManifest.Replace("<channel>win-beta</channel>", "");
        Assert.Equal(new InstallManifest("0.7.2", null), SqVersion.Parse(xml));
    }

    [Theory]
    [InlineData("<package><metadata></metadata></package>")]
    [InlineData("<package><metadata><version> </version></metadata></package>")]
    [InlineData("not xml at all")]
    [InlineData("")]
    public void UnusableManifestsParseToNull(string xml) => Assert.Null(SqVersion.Parse(xml));

    [Fact]
    public void DetectReadsTheManifestFirst()
    {
        File.WriteAllText(InstallPaths.Manifest(_root), RealManifest);
        var info = InstallDetection.Detect(_root, () => throw new Xunit.Sdk.XunitException("registry must not be consulted"));
        Assert.Equal(new InstallInfo("0.7.2", "win-beta"), info);
    }

    [Fact]
    public void DetectFallsBackToTheUninstallKeyWhenTheManifestIsMissing()
    {
        // A wrong "not installed" would run Setup.exe, which silently no-ops on an existing install.
        var info = InstallDetection.Detect(_root, () => "0.7.1");
        Assert.Equal(new InstallInfo("0.7.1", null), info);
    }

    [Fact]
    public void DetectFallsBackWhenTheManifestIsMalformed()
    {
        File.WriteAllText(InstallPaths.Manifest(_root), "<<<");
        Assert.Equal(new InstallInfo("0.7.1", null), InstallDetection.Detect(_root, () => "0.7.1"));
    }

    [Fact]
    public void NothingAnywhereMeansNotInstalled()
        => Assert.Null(InstallDetection.Detect(_root, () => null));

    [Fact]
    public void PathsAreUnderTheRoot()
    {
        Assert.Equal(Path.Combine(_root, "current", "ClaudeUsageTray.exe"), InstallPaths.CurrentExe(_root));
        Assert.Equal(Path.Combine(_root, "current", "sq.version"), InstallPaths.Manifest(_root));
        Assert.Equal(Path.Combine(_root, "Update.exe"), InstallPaths.UpdateExe(_root));
        Assert.EndsWith(@"\WusTechnik.ClaudeUsageTray", InstallPaths.DefaultRoot);
    }

    // ---- current ring: explicit setting wins, otherwise the manifest channel ----

    [Theory]
    [InlineData(null, "win-beta", Ring.Beta)]   // null in the file is a normal state for a beta install
    [InlineData(null, "win", Ring.Stable)]
    [InlineData(null, null, Ring.Stable)]
    [InlineData(false, "win-beta", Ring.Stable)] // explicit opt-out beats the channel
    [InlineData(true, "win", Ring.Beta)]
    [InlineData(true, null, Ring.Beta)]
    public void CurrentRingResolution(bool? useBetaReleases, string? channel, Ring expected)
        => Assert.Equal(expected, CurrentRing.Resolve(useBetaReleases, channel));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~InstallStateTests`
Expected: build FAILS.

- [ ] **Step 3: Implement**

`src/ClaudeUsageTraySetupStub/InstallState.cs`:

```csharp
using System.Security;
using System.Xml;
using System.Xml.Linq;
using ClaudeUsageTray.Core;
using Microsoft.Win32;

namespace ClaudeUsageTraySetupStub;

/// <summary>Where a per-user Velopack install of the app lives. Fixed on purpose: nothing in the app
/// supports a relocated install, which is why the stub does not expose Setup.exe's --installto.</summary>
public static class InstallPaths
{
    public const string PackId = "WusTechnik.ClaudeUsageTray";
    /// <summary>Program.cs acquires this at launch (SingleInstance.cs). The reliable "is the tray
    /// running in this session" probe — and it distinguishes the installed copy from a portable one
    /// only together with the process path.</summary>
    public const string MutexName = @"Local\WusTechnik.ClaudeUsageTray";
    public const string ExeName = "ClaudeUsageTray";

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), PackId);

    public static string CurrentExe(string root) => Path.Combine(root, "current", ExeName + ".exe");
    public static string Manifest(string root) => Path.Combine(root, "current", "sq.version");
    public static string UpdateExe(string root) => Path.Combine(root, "Update.exe");
}

public sealed record InstallManifest(string Version, string? Channel);

/// <summary>`current/sq.version` is the package nuspec — plain XML with a default namespace, so
/// elements are matched by local name. Reading it needs no Velopack dependency.</summary>
public static class SqVersion
{
    public static InstallManifest? Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var metadata = XDocument.Parse(xml).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            var version = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value.Trim();
            if (string.IsNullOrEmpty(version)) return null;
            var channel = metadata!.Elements().FirstOrDefault(e => e.Name.LocalName == "channel")?.Value.Trim();
            return new InstallManifest(version, string.IsNullOrEmpty(channel) ? null : channel);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}

public sealed record InstallInfo(string Version, string? Channel);

public static class InstallDetection
{
    /// <summary>Manifest first; a missing or malformed one falls back to the HKCU uninstall key before
    /// concluding "not installed". Concluding it wrongly would run Setup.exe, which silently no-ops on
    /// an existing install — the stub would then report success having changed nothing.</summary>
    public static InstallInfo? Detect(string root, Func<string?> uninstallKeyVersion)
    {
        var manifestPath = InstallPaths.Manifest(root);
        if (File.Exists(manifestPath))
        {
            try
            {
                if (SqVersion.Parse(File.ReadAllText(manifestPath)) is { } manifest)
                    return new InstallInfo(manifest.Version, manifest.Channel);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var fromRegistry = uninstallKeyVersion();
        return fromRegistry is null ? null : new InstallInfo(fromRegistry, null);
    }

    /// <summary>Velopack registers the per-user uninstall entry under HKCU, keyed by the pack id. The
    /// registry knows the version but not the channel.</summary>
    public static string? ReadUninstallKeyVersion()
    {
        try
        {
            using var uninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) return null;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(name);
                if (key is null) continue;
                var location = (key.GetValue("InstallLocation") as string)?.TrimEnd('\\');
                if (string.Equals(name, InstallPaths.PackId, StringComparison.OrdinalIgnoreCase)
                    || (location is not null && location.EndsWith(InstallPaths.PackId, StringComparison.OrdinalIgnoreCase)))
                    return key.GetValue("DisplayVersion") as string ?? "unknown";
            }
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException) { }
        return null;
    }
}

/// <summary>Which ring an existing install is on. An explicit setting wins; otherwise the manifest
/// channel decides — the same adoption rule Program.cs applies at launch, and the reason a null
/// setting on a win-beta install must read as beta, not stable.</summary>
public static class CurrentRing
{
    public static Ring Resolve(bool? useBetaReleases, string? manifestChannel)
        => (useBetaReleases ?? UpdateRing.IsBetaChannel(manifestChannel)) ? Ring.Beta : Ring.Stable;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~InstallStateTests`
Expected: PASS, 16 tests.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTraySetupStub/InstallState.cs tests/ClaudeUsageTraySetupStub.Tests/InstallStateTests.cs
git commit -m "feat(setup-stub): detect an existing install and its current ring

sq.version first, HKCU uninstall key as fallback; explicit useBetaReleases beats the manifest
channel and a null setting follows the channel.

Refs #21"
```

---

### Task 9: `SettingsEdit` and `SettingsFile`

**Files:**
- Create: `src/ClaudeUsageTraySetupStub/SettingsEdit.cs`
- Test: `tests/ClaudeUsageTraySetupStub.Tests/SettingsEditTests.cs`

**Interfaces:**
- Consumes: `Ring`.
- Produces:
  - `public enum SettingsStatus { Ok, Absent, Malformed, WrongType }`
  - `public sealed record SettingsReadResult(SettingsStatus Status, bool? UseBetaReleases)`
  - `public sealed record SettingsEditResult(SettingsStatus Status, string? Json)`
  - `public static class SettingsEdit` — `const string Key = "useBetaReleases"`, `SettingsReadResult Read(string? json)`, `SettingsEditResult Apply(string? json, bool useBetaReleases)`, `bool NeedsReconcile(bool? existing, Ring chosen)`.
  - `public enum SettingsWriteStatus { Written, Malformed, WrongType, IoError, ReadBackMismatch }`
  - `public static class SettingsFile` — `string DefaultPath` (`%APPDATA%\ClaudeUsageTray\settings.json`), `SettingsReadResult Read(string path)`, `SettingsWriteStatus Write(string path, bool useBetaReleases)`.

- [ ] **Step 1: Write the failing tests**

`tests/ClaudeUsageTraySetupStub.Tests/SettingsEditTests.cs`:

```csharp
using System.Text.Json.Nodes;
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class SettingsEditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stub-settings-" + Guid.NewGuid().ToString("N"));
    public SettingsEditTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Typical = """
        {
          "displayMode": "both",
          "thresholds": { "orange": 50, "red": 85 },
          "runAtStartup": true,
          "useBetaReleases": false,
          "futureKeyThisStubHasNeverHeardOf": { "nested": [1, 2, 3] }
        }
        """;

    // ---- Read ----

    [Fact]
    public void ReadsAnExplicitValue() => Assert.Equal(new SettingsReadResult(SettingsStatus.Ok, false), SettingsEdit.Read(Typical));

    [Fact]
    public void ReadsNullAsNoChoice()
        => Assert.Equal(new SettingsReadResult(SettingsStatus.Ok, null), SettingsEdit.Read("""{ "useBetaReleases": null }"""));

    [Fact]
    public void ReadsAnAbsentKeyAsNoChoice()
        => Assert.Equal(new SettingsReadResult(SettingsStatus.Ok, null), SettingsEdit.Read("""{ "runAtStartup": true }"""));

    [Fact]
    public void ReadsAnyCasing()
        => Assert.Equal(true, SettingsEdit.Read("""{ "UseBetaReleases": true }""").UseBetaReleases);

    [Fact]
    public void MissingOrEmptyFileIsAbsent()
    {
        Assert.Equal(SettingsStatus.Absent, SettingsEdit.Read(null).Status);
        Assert.Equal(SettingsStatus.Absent, SettingsEdit.Read("  ").Status);
    }

    [Theory]
    [InlineData("{ nope")]
    [InlineData("[1, 2]")]
    public void MalformedIsReported(string json) => Assert.Equal(SettingsStatus.Malformed, SettingsEdit.Read(json).Status);

    [Fact]
    public void WrongTypeIsReported()
        => Assert.Equal(SettingsStatus.WrongType, SettingsEdit.Read("""{ "useBetaReleases": "yes" }""").Status);

    // ---- Apply ----

    [Fact]
    public void PreservesEveryOtherKeyIncludingUnknownOnes()
    {
        // The stub is routinely older than the app; a round-trip through Settings would drop keys it predates.
        var result = SettingsEdit.Apply(Typical, useBetaReleases: true);
        Assert.Equal(SettingsStatus.Ok, result.Status);
        var node = JsonNode.Parse(result.Json!)!.AsObject();
        Assert.True((bool)node["useBetaReleases"]!);
        Assert.Equal(3, node["futureKeyThisStubHasNeverHeardOf"]!["nested"]!.AsArray().Count);
        Assert.Equal(85, (int)node["thresholds"]!["red"]!);
        Assert.Equal(5, node.Count);
    }

    [Fact]
    public void RewritesADifferentlyCasedKeyInPlaceWithoutAddingASecond()
    {
        // Settings.Load is case-insensitive; two keys would leave which one wins to chance.
        var result = SettingsEdit.Apply("""{ "UseBetaReleases": false, "runAtStartup": true }""", true);
        var node = JsonNode.Parse(result.Json!)!.AsObject();
        Assert.Equal(2, node.Count);
        Assert.True((bool)node["UseBetaReleases"]!);
        Assert.Equal("UseBetaReleases", node.First().Key);
    }

    [Fact]
    public void AddsTheKeyWhenAbsent()
    {
        var node = JsonNode.Parse(SettingsEdit.Apply("""{ "runAtStartup": true }""", false).Json!)!.AsObject();
        Assert.False((bool)node["useBetaReleases"]!);
    }

    [Fact]
    public void ReplacesANullValue()
    {
        var node = JsonNode.Parse(SettingsEdit.Apply("""{ "useBetaReleases": null }""", true).Json!)!.AsObject();
        Assert.True((bool)node["useBetaReleases"]!);
    }

    [Fact]
    public void MissingFileBecomesAnObjectWithJustThatKey()
    {
        var node = JsonNode.Parse(SettingsEdit.Apply(null, true).Json!)!.AsObject();
        Assert.Single(node);
        Assert.True((bool)node["useBetaReleases"]!);
    }

    [Fact]
    public void RefusesToOverwriteAMalformedFile()
    {
        var result = SettingsEdit.Apply("{ nope", true);
        Assert.Equal(SettingsStatus.Malformed, result.Status);
        Assert.Null(result.Json);
    }

    [Fact]
    public void RefusesAWrongTypedValue()
        => Assert.Equal(SettingsStatus.WrongType, SettingsEdit.Apply("""{ "useBetaReleases": 1 }""", true).Status);

    // ---- reconciliation: the stale settings file ----

    [Theory]
    [InlineData(false, Ring.Beta, true)]   // the "beta installer undoes itself" bug, re-entering via a leftover file
    [InlineData(true, Ring.Stable, true)]  // mirror case: stable install that would stage a beta at once
    [InlineData(null, Ring.Beta, false)]   // absent/null is handled by the app's adoption rule; writing it adds a second source of truth
    [InlineData(null, Ring.Stable, false)]
    [InlineData(true, Ring.Beta, false)]
    [InlineData(false, Ring.Stable, false)]
    public void ReconcileOnlyWhenAnExplicitValueContradictsTheChosenRing(bool? existing, Ring chosen, bool expected)
        => Assert.Equal(expected, SettingsEdit.NeedsReconcile(existing, chosen));

    // ---- SettingsFile ----

    [Fact]
    public void WriteCreatesTheDirectoryAndReadsBack()
    {
        var path = Path.Combine(_dir, "sub", "settings.json");
        Assert.Equal(SettingsWriteStatus.Written, SettingsFile.Write(path, true));
        Assert.Equal(true, SettingsFile.Read(path).UseBetaReleases);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void WriteKeepsExistingContent()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, Typical);
        Assert.Equal(SettingsWriteStatus.Written, SettingsFile.Write(path, true));
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True((bool)node["useBetaReleases"]!);
        Assert.NotNull(node["futureKeyThisStubHasNeverHeardOf"]);
    }

    [Fact]
    public void WriteRefusesAMalformedFileAndLeavesItUntouched()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ nope");
        Assert.Equal(SettingsWriteStatus.Malformed, SettingsFile.Write(path, true));
        Assert.Equal("{ nope", File.ReadAllText(path));
    }

    [Fact]
    public void ReadOfAMissingFileIsAbsent()
        => Assert.Equal(SettingsStatus.Absent, SettingsFile.Read(Path.Combine(_dir, "none.json")).Status);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~SettingsEditTests`
Expected: build FAILS.

- [ ] **Step 3: Implement**

`src/ClaudeUsageTraySetupStub/SettingsEdit.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsageTraySetupStub;

public enum SettingsStatus { Ok, Absent, Malformed, WrongType }

public sealed record SettingsReadResult(SettingsStatus Status, bool? UseBetaReleases);

public sealed record SettingsEditResult(SettingsStatus Status, string? Json);

/// <summary>Reads and rewrites one key of settings.json as a JsonNode DOM. Never through the app's
/// Settings type: that would drop every key this stub's copy predates (the stub cannot auto-update,
/// so it is routinely older than the app) and would run NormalizeFields over values it was never
/// asked to touch. Pure — the file IO is in <see cref="SettingsFile"/>.</summary>
public static class SettingsEdit
{
    public const string Key = "useBetaReleases";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static SettingsReadResult Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(SettingsStatus.Absent, null);
        var root = ParseObject(json);
        if (root is null) return new(SettingsStatus.Malformed, null);
        var property = FindKey(root);
        if (property is null || property.Value.Value is null) return new(SettingsStatus.Ok, null);
        return property.Value.Value is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? new(SettingsStatus.Ok, flag)
            : new(SettingsStatus.WrongType, null);
    }

    /// <summary>The whole document with the key set. A malformed file or a non-bool value is refused,
    /// not replaced: overwriting would destroy the user's other settings. A missing file becomes an
    /// object with just this key.</summary>
    public static SettingsEditResult Apply(string? json, bool useBetaReleases)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(json)) root = new JsonObject();
        else
        {
            var parsed = ParseObject(json);
            if (parsed is null) return new(SettingsStatus.Malformed, null);
            root = parsed;
        }

        var existing = FindKey(root);
        if (existing is { Value: { } current } && !(current is JsonValue v && v.TryGetValue<bool>(out _)))
            return new(SettingsStatus.WrongType, null);

        // Settings.Load matches case-insensitively, so the existing spelling is kept and rewritten in
        // place; adding a second key would leave which one wins to chance.
        root[existing?.Key ?? Key] = JsonValue.Create(useBetaReleases);
        return new(SettingsStatus.Ok, root.ToJsonString(WriteOptions));
    }

    /// <summary>The stale-settings rule. Only an explicit value that contradicts the chosen ring is
    /// corrected; absent or null stays, because Program.cs resolves that from the installed channel
    /// and writing it would add a second source of truth.</summary>
    public static bool NeedsReconcile(bool? existing, Ring chosen)
        => existing is bool value && value != (chosen == Ring.Beta);

    private static JsonObject? ParseObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static KeyValuePair<string, JsonNode?>? FindKey(JsonObject root)
    {
        foreach (var property in root)
            if (string.Equals(property.Key, Key, StringComparison.OrdinalIgnoreCase)) return property;
        return null;
    }
}

public enum SettingsWriteStatus { Written, Malformed, WrongType, IoError, ReadBackMismatch }

public static class SettingsFile
{
    /// <summary>Roaming AppData — the same path as Settings.DefaultPath. Roaming is why a stale file
    /// can follow the user to a machine that never had the app.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray", "settings.json");

    public static SettingsReadResult Read(string path)
    {
        try
        {
            return File.Exists(path) ? SettingsEdit.Read(File.ReadAllText(path)) : new(SettingsStatus.Absent, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(SettingsStatus.Malformed, null);
        }
    }

    /// <summary>Temp file plus atomic replace, matching Settings.Save, then a read-back: a write that
    /// did not persist is exit 3005, not a silent success.</summary>
    public static SettingsWriteStatus Write(string path, bool useBetaReleases)
    {
        string? existing;
        try { existing = File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return SettingsWriteStatus.IoError; }

        var edit = SettingsEdit.Apply(existing, useBetaReleases);
        if (edit.Status == SettingsStatus.Malformed) return SettingsWriteStatus.Malformed;
        if (edit.Status == SettingsStatus.WrongType) return SettingsWriteStatus.WrongType;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, edit.Json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return SettingsWriteStatus.IoError; }

        var back = Read(path);
        return back.Status == SettingsStatus.Ok && back.UseBetaReleases == useBetaReleases
            ? SettingsWriteStatus.Written
            : SettingsWriteStatus.ReadBackMismatch;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~SettingsEditTests`
Expected: PASS, 24 tests.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTraySetupStub/SettingsEdit.cs tests/ClaudeUsageTraySetupStub.Tests/SettingsEditTests.cs
git commit -m "feat(setup-stub): edit useBetaReleases as a JSON DOM with read-back

Unknown keys and the existing key casing survive; malformed files and wrong-typed values are
refused rather than overwritten. Only an explicit value contradicting the chosen ring is reconciled.

Refs #21"
```

---

### Task 10: `Decision`

**Files:**
- Create: `src/ClaudeUsageTraySetupStub/Decision.cs`
- Test: `tests/ClaudeUsageTraySetupStub.Tests/DecisionTests.cs`

**Interfaces:**
- Consumes: `StubOptions`, `InstallInfo`, `Ring`.
- Produces:
  - `public enum Step { AskRing, Install, ChangeRing, Converged, Ambiguous }`
  - `public sealed record Decision(Step Step, Ring Ring)` — for `AskRing`, `Ring` is the radio button to preselect; for `Ambiguous` it is the current ring (for the message).
  - `public static class Flow` — `Decision Decide(StubOptions options, InstallInfo? installed, Ring? currentRing)`.

- [ ] **Step 1: Write the failing tests**

`tests/ClaudeUsageTraySetupStub.Tests/DecisionTests.cs`:

```csharp
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class DecisionTests
{
    private static StubOptions Opts(Ring? ring, bool silent) => new(ring, silent, null, false, false);
    private static readonly InstallInfo Installed = new("0.7.2", "win-beta");

    [Fact]
    public void FreshInteractiveWithNoRingAsks_DefaultingToStable()
        => Assert.Equal(new Decision(Step.AskRing, Ring.Stable), Flow.Decide(Opts(null, false), null, null));

    [Fact]
    public void FreshSilentWithNoRingInstallsStable()
        // The documented default for a normal install.
        => Assert.Equal(new Decision(Step.Install, Ring.Stable), Flow.Decide(Opts(null, true), null, null));

    [Theory]
    [InlineData(Ring.Beta, false)]
    [InlineData(Ring.Beta, true)]
    [InlineData(Ring.Stable, true)]
    public void FreshWithARingInstallsIt(Ring ring, bool silent)
        => Assert.Equal(new Decision(Step.Install, ring), Flow.Decide(Opts(ring, silent), null, null));

    [Fact]
    public void InstalledSilentWithNoRingIsAmbiguous()
    {
        // Defaulting to stable here would silently drag a deliberate beta opt-in back down.
        Assert.Equal(new Decision(Step.Ambiguous, Ring.Beta), Flow.Decide(Opts(null, true), Installed, Ring.Beta));
    }

    [Fact]
    public void InstalledInteractiveWithNoRingAsks_PreselectingTheCurrentRing()
        => Assert.Equal(new Decision(Step.AskRing, Ring.Beta), Flow.Decide(Opts(null, false), Installed, Ring.Beta));

    [Fact]
    public void InstalledOnTheRequestedRingIsConverged()
    {
        // Idempotent: the same --ring twice is a success, not a no-op.
        Assert.Equal(new Decision(Step.Converged, Ring.Beta), Flow.Decide(Opts(Ring.Beta, true), Installed, Ring.Beta));
    }

    [Fact]
    public void InstalledOnTheOtherRingChangesIt()
        => Assert.Equal(new Decision(Step.ChangeRing, Ring.Stable), Flow.Decide(Opts(Ring.Stable, true), Installed, Ring.Beta));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~DecisionTests`
Expected: build FAILS.

- [ ] **Step 3: Implement**

`src/ClaudeUsageTraySetupStub/Decision.cs`:

```csharp
namespace ClaudeUsageTraySetupStub;

public enum Step { AskRing, Install, ChangeRing, Converged, Ambiguous }

/// <summary>For <see cref="Step.AskRing"/>, <c>Ring</c> is the radio button to preselect; the wizard's
/// answer is fed back through <see cref="Flow.Decide"/> with the ring filled in, so the interactive
/// and silent paths share one rule set.</summary>
public sealed record Decision(Step Step, Ring Ring);

public static class Flow
{
    public static Decision Decide(StubOptions options, InstallInfo? installed, Ring? currentRing)
    {
        if (installed is null || currentRing is null)
        {
            // No install: --ring wins; silently the default is stable, matching a normal install.
            if (options.Ring is { } chosen) return new Decision(Step.Install, chosen);
            return options.Silent ? new Decision(Step.Install, Ring.Stable) : new Decision(Step.AskRing, Ring.Stable);
        }

        if (options.Ring is null)
        {
            // Silent + installed + no ring: the operator never said what the desired state is. A default
            // of stable would reverse a deliberate beta opt-in where nobody is watching.
            return options.Silent
                ? new Decision(Step.Ambiguous, currentRing.Value)
                : new Decision(Step.AskRing, currentRing.Value);
        }

        return options.Ring == currentRing
            ? new Decision(Step.Converged, currentRing.Value)
            : new Decision(Step.ChangeRing, options.Ring.Value);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~DecisionTests`
Expected: PASS.

- [ ] **Step 5: Run the whole stub test project**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests`
Expected: PASS, all green. Also `dotnet test` at the solution root must still pass the app's tests.

- [ ] **Step 6: Commit**

```bash
git add src/ClaudeUsageTraySetupStub/Decision.cs tests/ClaudeUsageTraySetupStub.Tests/DecisionTests.cs
git commit -m "feat(setup-stub): the flow decision, including the silent-without-ring safety rule

Refs #21"
```

---

### Task 11: `SetupLog`, `ConsoleOutput`, `NativeProcess`, `ProcessControl`

The shell around the pure functions. Only the two pure helpers on `ProcessControl` are unit-tested; the rest is Win32 and is exercised by the smoke run in Task 13.

**Files:**
- Create: `src/ClaudeUsageTraySetupStub/SetupLog.cs`
- Create: `src/ClaudeUsageTraySetupStub/ConsoleOutput.cs`
- Create: `src/ClaudeUsageTraySetupStub/NativeProcess.cs`
- Create: `src/ClaudeUsageTraySetupStub/ProcessControl.cs`
- Test: `tests/ClaudeUsageTraySetupStub.Tests/ProcessControlTests.cs`

**Interfaces:**
- Consumes: `InstallPaths`.
- Produces:
  - `public sealed class SetupLog(string path)` — `static string DefaultPath`, `void Write(string message)`.
  - `internal static class ConsoleOutput` — `bool TryWriteLine(string text)` (true when a parent console exists).
  - `internal sealed class StartedProcess : IDisposable` — `bool BrokeAwayFromJob`, `int WaitForExit()`.
  - `internal static class NativeProcess` — `StartedProcess? Start(string exe, string arguments, bool tryBreakaway)`.
  - `public static class ProcessControl` — pure: `bool MustRefuseContext(bool isLocalSystem, int sessionId)`, `bool IsInsideRoot(string? path, string root)`; shell: `bool IsRefusedContext()`, `bool IsTrayMutexHeld()`, `(List<Process> Installed, List<Process> Other) FindTray(string root)`, `bool IsUpdateApplying(string root)`, `bool StopTray(IReadOnlyList<Process> processes, TimeSpan timeout)`, `bool RelaunchTray(string root)`.

- [ ] **Step 1: Write the failing tests for the pure helpers**

`tests/ClaudeUsageTraySetupStub.Tests/ProcessControlTests.cs`:

```csharp
using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class ProcessControlTests
{
    [Theory]
    [InlineData(true, 1, true)]   // SYSTEM in an interactive session (psexec -s) is still useless
    [InlineData(false, 0, true)]  // session 0: Intune/SCCM default context
    [InlineData(false, 1, false)]
    [InlineData(false, 2, false)]
    public void RefusesSystemOrSessionZero(bool isLocalSystem, int sessionId, bool expected)
        => Assert.Equal(expected, ProcessControl.MustRefuseContext(isLocalSystem, sessionId));

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\WusTechnik.ClaudeUsageTray\current\ClaudeUsageTray.exe", true)]
    [InlineData(@"C:\Users\x\AppData\Local\WUSTECHNIK.CLAUDEUSAGETRAY\Update.exe", true)]
    [InlineData(@"C:\Users\x\AppData\Local\WusTechnik.ClaudeUsageTray.old\current\ClaudeUsageTray.exe", false)]
    [InlineData(@"D:\portable\ClaudeUsageTray.exe", false)]
    [InlineData(null, false)]
    public void IsInsideRootIsAPathPrefixCheck(string? path, bool expected)
        => Assert.Equal(expected, ProcessControl.IsInsideRoot(path, @"C:\Users\x\AppData\Local\WusTechnik.ClaudeUsageTray"));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~ProcessControlTests`
Expected: build FAILS.

- [ ] **Step 3: Implement the four files**

`src/ClaudeUsageTraySetupStub/SetupLog.cs`:

```csharp
namespace ClaudeUsageTraySetupStub;

/// <summary>A WinExe has no stdout, so a silent run would otherwise show an operator nothing but an
/// exit code. Same rule as the app's fetch.log: outcomes, never credential material. Never throws.</summary>
public sealed class SetupLog(string path)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray", "setup.log");

    public void Write(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss}Z {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // diagnostics must never break the installer
        }
    }
}
```

`src/ClaudeUsageTraySetupStub/ConsoleOutput.cs`:

```csharp
using System.Runtime.InteropServices;

namespace ClaudeUsageTraySetupStub;

/// <summary>Attaches to the parent's console when there is one, so `--help`, `--version` and silent
/// failures are visible to whoever ran the exe from a shell. Double-clicked, there is no console and
/// the caller falls back to a dialog or the log.</summary>
internal static class ConsoleOutput
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private static bool? _attached;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    public static bool TryWriteLine(string text)
    {
        _attached ??= AttachConsole(AttachParentProcess);
        if (_attached != true) return false;
        try
        {
            using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            writer.WriteLine(text);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
```

`src/ClaudeUsageTraySetupStub/NativeProcess.cs`:

```csharp
using System.Runtime.InteropServices;

namespace ClaudeUsageTraySetupStub;

/// <summary>A child that outlives the shell. Process.Start cannot set CREATE_BREAKAWAY_FROM_JOB, and
/// without it a child launched from a deployment agent's or terminal's job object dies with that job —
/// which looks exactly like a startup crash (CLAUDE.md documents the same trap for Update.exe apply).</summary>
internal sealed class StartedProcess(IntPtr handle, bool brokeAwayFromJob) : IDisposable
{
    public bool BrokeAwayFromJob { get; } = brokeAwayFromJob;

    /// <summary>Blocks until the child exits; -1 if the exit code could not be read.</summary>
    public int WaitForExit()
    {
        NativeProcess.WaitForSingleObject(handle, NativeProcess.Infinite);
        return NativeProcess.GetExitCodeProcess(handle, out var code) ? unchecked((int)code) : -1;
    }

    public void Dispose() => NativeProcess.CloseHandle(handle);
}

internal static unsafe class NativeProcess
{
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int ErrorAccessDenied = 5;
    internal const uint Infinite = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateProcessW(char* applicationName, char* commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, char* currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    /// <summary>Breakaway first; when the job forbids it (ERROR_ACCESS_DENIED) the child is started
    /// inside the job so the caller can at least wait for it and read its exit code. Null when even
    /// that fails.</summary>
    public static StartedProcess? Start(string exe, string arguments, bool tryBreakaway)
    {
        var commandLine = string.IsNullOrEmpty(arguments) ? $"\"{exe}\"" : $"\"{exe}\" {arguments}";
        if (tryBreakaway && TryCreate(exe, commandLine, CreateBreakawayFromJob, out var info))
            return new StartedProcess(info.hProcess, brokeAwayFromJob: true);
        if (tryBreakaway && Marshal.GetLastPInvokeError() != ErrorAccessDenied && Marshal.GetLastPInvokeError() != 0)
            return null;
        return TryCreate(exe, commandLine, 0, out info) ? new StartedProcess(info.hProcess, brokeAwayFromJob: false) : null;
    }

    private static bool TryCreate(string exe, string commandLine, uint extraFlags, out ProcessInformation info)
    {
        // CreateProcessW may write into the command-line buffer, so it must be a mutable copy.
        var buffer = (commandLine + '\0').ToCharArray();
        var application = (exe + '\0').ToCharArray();
        var startup = new StartupInfo { cb = (uint)sizeof(StartupInfo) };
        fixed (char* app = application)
        fixed (char* cmd = buffer)
        {
            var ok = CreateProcessW(app, cmd, IntPtr.Zero, IntPtr.Zero, false, extraFlags | CreateUnicodeEnvironment,
                IntPtr.Zero, null, ref startup, out info);
            if (ok) CloseHandle(info.hThread);
            return ok;
        }
    }
}
```

`src/ClaudeUsageTraySetupStub/ProcessControl.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ClaudeUsageTraySetupStub;

public static class ProcessControl
{
    /// <summary>A per-user install run as SYSTEM lands in the SYSTEM profile and exits 0 — silent,
    /// complete, useless. Intune Win32 apps and SCCM programs default to that context.</summary>
    public static bool MustRefuseContext(bool isLocalSystem, int sessionId) => isLocalSystem || sessionId == 0;

    public static bool IsRefusedContext()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var isSystem = identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        return MustRefuseContext(isSystem, Process.GetCurrentProcess().SessionId);
    }

    public static bool IsInsideRoot(string? path, string root)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var prefix = Path.GetFullPath(root).TrimEnd('\\') + '\\';
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The single-instance mutex from SingleInstance.cs: held iff a tray is running in this
    /// session, installed or portable.</summary>
    public static bool IsTrayMutexHeld()
    {
        if (!Mutex.TryOpenExisting(InstallPaths.MutexName, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>Tray processes split by whether they run from the install tree. A process whose path
    /// cannot be read (another user's, or gone already) counts as Other, never as Installed.</summary>
    public static (List<Process> Installed, List<Process> Other) FindTray(string root)
    {
        List<Process> installed = [], other = [];
        foreach (var process in Process.GetProcessesByName(InstallPaths.ExeName))
        {
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            (IsInsideRoot(path, root) ? installed : other).Add(process);
        }
        return (installed, other);
    }

    /// <summary>An Update.exe from the install tree means the user just clicked Restart to update and
    /// current/ is about to be swapped. Killing the app now would race a directory being replaced.</summary>
    public static bool IsUpdateApplying(string root)
    {
        foreach (var process in Process.GetProcessesByName("Update"))
        {
            try
            {
                if (IsInsideRoot(process.MainModule?.FileName, root)) return true;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        return false;
    }

    /// <summary>Terminate and wait. TrayApp is an ApplicationContext with no main window, so there is
    /// nothing to close politely. Waiting for the mutex to clear is not optional: a relaunch that
    /// races the dying process finds it held and exits silently, leaving no tray at all.</summary>
    public static bool StopTray(IReadOnlyList<Process> processes, TimeSpan timeout)
    {
        foreach (var process in processes)
        {
            try { process.Kill(); process.WaitForExit((int)timeout.TotalMilliseconds); }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
        }
        var deadline = DateTime.UtcNow + timeout;
        while (IsTrayMutexHeld())
        {
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(100);
        }
        return true;
    }

    /// <summary>Detached (breakaway from the caller's job). When breakaway is denied, the explorer.exe
    /// indirection CLAUDE.md uses: Explorer starts the child in its own job, so it survives the shell.</summary>
    public static bool RelaunchTray(string root)
    {
        var exe = InstallPaths.CurrentExe(root);
        if (!File.Exists(exe)) return false;
        using var started = NativeProcess.Start(exe, "", tryBreakaway: true);
        if (started is { BrokeAwayFromJob: true }) return true;
        try
        {
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false });
            return explorer is not null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return started is not null; // inside the job is better than nothing
        }
    }
}
```

- [ ] **Step 4: Run the tests and build**

Run: `dotnet test tests/ClaudeUsageTraySetupStub.Tests --filter FullyQualifiedName~ProcessControlTests`
Expected: PASS, 9 tests, and the stub project compiles with no AOT analyzer warnings (`IL3050`/`IL2026`). If `Process.MainModule` produces an `IL` warning, it is informational for this Windows-only path; do not suppress it globally.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTraySetupStub/SetupLog.cs src/ClaudeUsageTraySetupStub/ConsoleOutput.cs src/ClaudeUsageTraySetupStub/NativeProcess.cs src/ClaudeUsageTraySetupStub/ProcessControl.cs tests/ClaudeUsageTraySetupStub.Tests/ProcessControlTests.cs
git commit -m "feat(setup-stub): process control, detached launch, log and console output

Refuses SYSTEM/session 0, probes the single-instance mutex, stops and relaunches the tray with
CREATE_BREAKAWAY_FROM_JOB (explorer.exe fallback), refuses to run while Update.exe is applying.

Refs #21"
```

---

### Task 12: `Wizard` (Win32 task dialogs)

No decisions live here — the strings come from `ResolvedBuild.Describe`, `Wording`, and the caller. Not unit-tested; verified by the smoke run in Task 13.

**Files:**
- Create: `src/ClaudeUsageTraySetupStub/Wizard.cs`

**Interfaces:**
- Consumes: `Ring`, `InstallInfo`, `Rings.ProductName`.
- Produces: `internal static class Wizard` — `const string Title = "Claude Usage Tray Setup"`, `Ring? ChooseRing(Ring preselected, InstallInfo? installed, Ring? currentRing)` (null = cancelled), `bool? RunWithProgress(string instruction, string content, Func<IProgress<double>, CancellationToken, Task<bool>> work)` (null = cancelled), `void Info(string instruction, string content)`, `void Warning(string instruction, string content)`, `void Error(string instruction, string content)`.

- [ ] **Step 1: Implement**

`src/ClaudeUsageTraySetupStub/Wizard.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClaudeUsageTraySetupStub;

/// <summary>Win32 task dialogs via P/Invoke: radio buttons and a progress bar are native features, so
/// no WinForms is needed and the exe stays small. Everything here is layout; every string shown is
/// decided elsewhere.</summary>
internal static unsafe class Wizard
{
    public const string Title = "Claude Usage Tray Setup";

    private const int RadioStable = 100;
    private const int RadioBeta = 101;
    private const int IdOk = 1;
    private const int IdCancel = 2;

    // ---- public pages ----

    public static Ring? ChooseRing(Ring preselected, InstallInfo? installed, Ring? currentRing)
    {
        var content = installed is null
            ? $"{Rings.ProductName} will be installed for the current user only; no administrator rights are needed.\n\nChoose which releases to follow."
            : $"{Rings.ProductName} {installed.Version} is installed on the {(currentRing == Ring.Beta ? "beta" : "stable")} ring.\n\nChoose which releases it should follow.";

        var page = new Page
        {
            Instruction = "Choose a release ring",
            Content = content,
            Icon = Icon.None,
            RadioButtons = [(RadioStable, "Stable (recommended)"), (RadioBeta, "Beta (pre-release builds)")],
            DefaultRadio = preselected == Ring.Beta ? RadioBeta : RadioStable,
            Buttons = [(IdOk, "Continue")],
            CommonButtons = CommonButtons.Cancel,
        };
        var (button, radio) = Show(page, callback: null);
        if (button != IdOk) return null;
        return radio == RadioBeta ? Ring.Beta : Ring.Stable;
    }

    /// <summary>Runs <paramref name="work"/> on the thread pool while a modal progress dialog shows.
    /// Cancel from the dialog cancels the token; the work's result is awaited either way.</summary>
    public static bool? RunWithProgress(string instruction, string content, Func<IProgress<double>, CancellationToken, Task<bool>> work)
    {
        using var cts = new CancellationTokenSource();
        var state = ProgressState.Reset();
        var progress = new Progress<double>(fraction => state.Percent = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100));
        var task = Task.Run(() => work(progress, cts.Token), CancellationToken.None);
        task.ContinueWith(_ => state.Done = true, TaskScheduler.Default);

        var page = new Page
        {
            Instruction = instruction,
            Content = content,
            Icon = Icon.None,
            CommonButtons = CommonButtons.Cancel,
            ShowProgressBar = true,
            CallbackTimer = true,
        };
        var (button, _) = Show(page, callback: &ProgressCallback);

        if (!state.Done && button == IdCancel)
        {
            cts.Cancel();
            try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            return null;
        }
        try { return task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return null; }
    }

    public static void Info(string instruction, string content) => Show(new Page { Instruction = instruction, Content = content, Icon = Icon.Information, CommonButtons = CommonButtons.Ok }, null);
    public static void Warning(string instruction, string content) => Show(new Page { Instruction = instruction, Content = content, Icon = Icon.Warning, CommonButtons = CommonButtons.Ok }, null);
    public static void Error(string instruction, string content) => Show(new Page { Instruction = instruction, Content = content, Icon = Icon.Error, CommonButtons = CommonButtons.Ok }, null);

    // ---- progress callback state (one dialog at a time; the callback is static) ----

    private sealed class ProgressState
    {
        public static readonly ProgressState Current = new();
        public volatile int Percent;
        public volatile bool Done;
        public IntPtr Hwnd;
        public static ProgressState Reset() { Current.Percent = 0; Current.Done = false; Current.Hwnd = IntPtr.Zero; return Current; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int ProgressCallback(IntPtr hwnd, uint notification, nuint wParam, nint lParam, nint refData)
    {
        var state = ProgressState.Current;
        switch (notification)
        {
            case TdnCreated:
                state.Hwnd = hwnd;
                SendMessageW(hwnd, TdmSetProgressBarRange, 0, (nint)(100 << 16)); // MAKELPARAM(0, 100)
                break;
            case TdnTimer:
                SendMessageW(hwnd, TdmSetProgressBarPos, (nuint)state.Percent, 0);
                // Close ourselves when the work finished; the caller tells "done" from "cancelled" by state.Done.
                if (state.Done) SendMessageW(hwnd, TdmClickButton, IdCancel, 0);
                break;
        }
        return 0; // S_OK
    }

    // ---- TaskDialogIndirect plumbing ----

    private enum Icon : ushort { None = 0, Warning = 0xFFFF, Error = 0xFFFE, Information = 0xFFFD }

    [Flags]
    private enum CommonButtons : uint { None = 0, Ok = 0x1, Cancel = 0x8 }

    private sealed class Page
    {
        public string Instruction = "";
        public string Content = "";
        public Icon Icon;
        public CommonButtons CommonButtons;
        public (int Id, string Text)[] Buttons = [];
        public (int Id, string Text)[] RadioButtons = [];
        public int DefaultRadio;
        public bool ShowProgressBar;
        public bool CallbackTimer;
    }

    private const uint TdfAllowDialogCancellation = 0x8;
    private const uint TdfShowProgressBar = 0x200;
    private const uint TdfCallbackTimer = 0x800;
    private const uint TdfPositionRelativeToWindow = 0x1000;
    private const uint TdfSizeToContent = 0x1000000;

    private const uint TdnCreated = 0;
    private const uint TdnTimer = 4;

    private const uint WmUser = 0x0400;
    private const uint TdmClickButton = WmUser + 102;
    private const uint TdmSetProgressBarRange = WmUser + 105;
    private const uint TdmSetProgressBarPos = WmUser + 106;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaskDialogButton
    {
        public int Id;
        public IntPtr Text;
    }

    /// <summary>TASKDIALOGCONFIG is declared with #pragma pack(1); cbSize must be 160 on x64.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TaskDialogConfig
    {
        public uint cbSize;
        public IntPtr hwndParent;
        public IntPtr hInstance;
        public uint dwFlags;
        public uint dwCommonButtons;
        public IntPtr pszWindowTitle;
        public IntPtr hMainIcon;
        public IntPtr pszMainInstruction;
        public IntPtr pszContent;
        public uint cButtons;
        public IntPtr pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public IntPtr pRadioButtons;
        public int nDefaultRadioButton;
        public IntPtr pszVerificationText;
        public IntPtr pszExpandedInformation;
        public IntPtr pszExpandedControlText;
        public IntPtr pszCollapsedControlText;
        public IntPtr hFooterIcon;
        public IntPtr pszFooter;
        public IntPtr pfCallback;
        public IntPtr lpCallbackData;
        public uint cxWidth;
    }

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern int TaskDialogIndirect(ref TaskDialogConfig config, out int button, out int radioButton, out int verificationChecked);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(IntPtr hwnd, uint message, nuint wParam, nint lParam);

    private static (int Button, int Radio) Show(Page page, delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, nint, nint, int> callback)
    {
        var allocations = new List<IntPtr>();
        IntPtr Str(string s) { var p = Marshal.StringToHGlobalUni(s); allocations.Add(p); return p; }
        IntPtr ButtonArray((int Id, string Text)[] buttons)
        {
            if (buttons.Length == 0) return IntPtr.Zero;
            var block = Marshal.AllocHGlobal(buttons.Length * sizeof(TaskDialogButton));
            allocations.Add(block);
            var items = (TaskDialogButton*)block;
            for (var i = 0; i < buttons.Length; i++) items[i] = new TaskDialogButton { Id = buttons[i].Id, Text = Str(buttons[i].Text) };
            return block;
        }

        try
        {
            var flags = TdfAllowDialogCancellation | TdfPositionRelativeToWindow | TdfSizeToContent;
            if (page.ShowProgressBar) flags |= TdfShowProgressBar;
            if (page.CallbackTimer) flags |= TdfCallbackTimer;

            var config = new TaskDialogConfig
            {
                cbSize = (uint)sizeof(TaskDialogConfig),
                dwFlags = flags,
                dwCommonButtons = (uint)page.CommonButtons,
                pszWindowTitle = Str(Title),
                hMainIcon = (IntPtr)(ushort)page.Icon,
                pszMainInstruction = Str(page.Instruction),
                pszContent = Str(page.Content),
                cButtons = (uint)page.Buttons.Length,
                pButtons = ButtonArray(page.Buttons),
                cRadioButtons = (uint)page.RadioButtons.Length,
                pRadioButtons = ButtonArray(page.RadioButtons),
                nDefaultRadioButton = page.DefaultRadio,
                pfCallback = (IntPtr)callback,
            };
            var hr = TaskDialogIndirect(ref config, out var button, out var radio, out _);
            // A failed call (no comctl32 v6, unlikely with the manifest) reads as cancel: nothing was chosen.
            return hr < 0 ? (IdCancel, 0) : (button, radio);
        }
        finally
        {
            foreach (var p in allocations) Marshal.FreeHGlobal(p);
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/ClaudeUsageTraySetupStub`
Expected: succeeds. `sizeof(TaskDialogConfig)` is a compile-time constant in unsafe context; if the compiler rejects `sizeof` on the struct, mark the struct fields blittable-only (they already are: `uint`, `int`, `IntPtr`).

- [ ] **Step 3: Commit**

```bash
git add src/ClaudeUsageTraySetupStub/Wizard.cs
git commit -m "feat(setup-stub): task-dialog wizard pages via P/Invoke

Ring choice with radio buttons, a progress page with a timer callback, and info/warning/error
boxes. Layout only; every string shown is decided elsewhere.

Refs #21"
```

---

### Task 13: `SetupRun` and `Program`

The orchestration. Every branch below calls a function tested in Tasks 1–10; this task adds no new rule.

**Files:**
- Create: `src/ClaudeUsageTraySetupStub/SetupRun.cs`
- Modify: `src/ClaudeUsageTraySetupStub/Program.cs` (replace the Task 1 placeholder)

**Interfaces:**
- Consumes: everything above.
- Produces: `internal sealed class SetupRun(StubOptions options, SetupLog log, HttpClient http)` — `Task<int> RunAsync()`; `Program.Main` returns the exit code.

- [ ] **Step 1: Implement `SetupRun`**

`src/ClaudeUsageTraySetupStub/SetupRun.cs`:

```csharp
namespace ClaudeUsageTraySetupStub;

/// <summary>One run of the stub, start to exit code. Reports go to the log always, to the console in
/// silent mode, and to a dialog interactively.</summary>
internal sealed class SetupRun(StubOptions options, SetupLog log, HttpClient http)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    public async Task<int> RunAsync()
    {
        if (ProcessControl.IsRefusedContext())
            return Fail(ExitCode.BadArguments, "Refusing to run as SYSTEM or in session 0.",
                $"A per-user install from that context lands in the SYSTEM profile and is useless. Run {Wizard.Title} in the user's own session.");

        var root = InstallPaths.DefaultRoot;
        var installed = InstallDetection.Detect(root, InstallDetection.ReadUninstallKeyVersion);
        var settingsPath = SettingsFile.DefaultPath;
        var settings = SettingsFile.Read(settingsPath);
        if (settings.Status is SettingsStatus.Malformed or SettingsStatus.WrongType)
            return Fail(ExitCode.AppControlFailed, "settings.json cannot be edited safely.",
                $"{settingsPath} is {(settings.Status == SettingsStatus.Malformed ? "not valid JSON" : $"carrying a non-boolean {SettingsEdit.Key}")}. " +
                "It was left untouched; fix or remove the file and run again.");

        Ring? currentRing = installed is null ? null : CurrentRing.Resolve(settings.UseBetaReleases, installed.Channel);
        log.Write(installed is null
            ? "state: not installed"
            : $"state: installed {installed.Version} channel={installed.Channel ?? "?"} setting={settings.UseBetaReleases?.ToString() ?? "null"} ring={currentRing}");

        if (installed is not null && ProcessControl.IsUpdateApplying(root))
            return Fail(ExitCode.AppControlFailed, "An update is being applied right now.", "Wait for it to finish and run this again.");

        if (installed is null && ProcessControl.IsTrayMutexHeld())
        {
            log.Write("warning: a tray outside the install tree is running (portable copy?)");
            if (!options.Silent)
                Wizard.Warning("A portable copy is running.",
                    $"A {Rings.ProductName} that was not installed by Setup is running. The installed app will share its settings with it.");
        }

        var effective = options;
        var decision = Flow.Decide(effective, installed, currentRing);
        if (decision.Step == Step.AskRing)
        {
            var chosen = Wizard.ChooseRing(decision.Ring, installed, currentRing);
            if (chosen is null) return Fail(ExitCode.Cancelled, "Cancelled.", null);
            effective = options with { Ring = chosen };
            decision = Flow.Decide(effective, installed, currentRing);
        }
        log.Write($"decision: {decision.Step} ring={decision.Ring}");

        return decision.Step switch
        {
            Step.Ambiguous => Fail(ExitCode.AmbiguousRequest,
                $"{Rings.ProductName} {installed!.Version} is installed on the {Name(currentRing!.Value)} ring.",
                "Pass --ring stable or --ring beta to say which ring it should be on; --silent alone changes nothing."),
            Step.Converged => Succeed($"{Rings.ProductName} {installed!.Version} is already on the {Name(decision.Ring)} ring.", null),
            Step.ChangeRing => ChangeRing(root, settingsPath, decision.Ring),
            _ => await InstallAsync(decision.Ring, settings, settingsPath, effective).ConfigureAwait(false),
        };
    }

    // ---- existing install: stop → write → relaunch ----

    private int ChangeRing(string root, string settingsPath, Ring target)
    {
        var (installedProcesses, _) = ProcessControl.FindTray(root);
        var wasRunning = installedProcesses.Count > 0;
        if (wasRunning && !ProcessControl.StopTray(installedProcesses, StopTimeout))
            return Fail(ExitCode.AppControlFailed, $"Could not stop {Rings.ProductName}.", "Nothing was changed. Quit it from the tray menu and run this again.");
        log.Write(wasRunning ? "tray: stopped" : "tray: was not running");

        var status = SettingsFile.Write(settingsPath, target == Ring.Beta);
        log.Write($"settings: write {SettingsEdit.Key}={target == Ring.Beta} -> {status}");
        if (status != SettingsWriteStatus.Written)
        {
            // The failure mode has to be "nothing changed" — never "installer ran, tray gone".
            if (wasRunning) ProcessControl.RelaunchTray(root);
            return Fail(ExitCode.AppControlFailed, "The setting could not be written.", $"{settingsPath}: {status}. Nothing was changed.");
        }

        if (wasRunning && !ProcessControl.RelaunchTray(root))
            return Fail(ExitCode.AppControlFailed, $"{Rings.ProductName} could not be restarted.",
                "The ring was changed, but the app is not running. Start it from the Start menu.");
        log.Write(wasRunning ? "tray: relaunched" : "tray: left stopped");
        return Succeed(Wording.SwitchStaged(target), null);
    }

    // ---- fresh install: resolve → download → verify → reconcile → Setup.exe ----

    private async Task<int> InstallAsync(Ring ring, SettingsReadResult settings, string settingsPath, StubOptions effective)
    {
        var resolve = await ReleaseResolver.ResolveAsync(http, ring, effective.Token, effective.Silent, HttpRetry.DefaultDelays, CancellationToken.None).ConfigureAwait(false);
        log.Write($"resolve: ring={ring} {resolve.Detail}");
        if (resolve.Build is null)
            return Fail(ExitCode.ResolutionFailed, "No installer could be found for the " + Name(ring) + " ring.", resolve.Detail);
        var build = resolve.Build;
        log.Write($"resolve: version={build.Version?.ToString() ?? "latest"} via={build.Via} url={build.Url}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"ClaudeUsageTraySetup-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var setupPath = Path.Combine(tempDir, Rings.SetupAssetName(build.Channel));

            bool downloaded;
            if (effective.Silent)
            {
                downloaded = await Downloader.DownloadAsync(http, build.Url, setupPath, HttpRetry.DefaultDelays, null, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var result = Wizard.RunWithProgress("Downloading", build.Describe(),
                    (progress, ct) => Downloader.DownloadAsync(http, build.Url, setupPath, HttpRetry.DefaultDelays, progress, ct));
                if (result is null) return Fail(ExitCode.Cancelled, "Cancelled.", null);
                downloaded = result.Value;
            }
            log.Write($"download: {(downloaded ? "ok" : "failed")}");
            if (!downloaded) return Fail(ExitCode.DownloadFailed, "The download failed.", build.Url.ToString());

            var verify = DownloadVerification.Verify(setupPath, build.Digest);
            log.Write($"verify: {verify} digest={(build.Digest is null ? "none" : "checked")}");
            if (verify != VerifyOutcome.Ok)
                return Fail(ExitCode.DownloadFailed, "The downloaded installer was rejected.", verify switch
                {
                    VerifyOutcome.Empty => "The file is empty.",
                    VerifyOutcome.NotExecutable => "The file is not a Windows executable.",
                    _ => "Its SHA-256 digest does not match what GitHub reports for the asset.",
                });

            // The stale-settings rule: a leftover explicit value contradicting the chosen ring would make
            // the very first update check undo this install.
            if (SettingsEdit.NeedsReconcile(settings.UseBetaReleases, ring))
            {
                var status = SettingsFile.Write(settingsPath, ring == Ring.Beta);
                log.Write($"settings: reconcile {SettingsEdit.Key}={ring == Ring.Beta} -> {status}");
                if (status != SettingsWriteStatus.Written)
                    return Fail(ExitCode.AppControlFailed, "A stale settings file could not be corrected.", $"{settingsPath}: {status}. Nothing was installed.");
            }

            using var setup = NativeProcess.Start(setupPath, effective.Silent ? "--silent" : "", tryBreakaway: true);
            if (setup is null) return Fail(ExitCode.DownloadFailed, "Setup.exe could not be started.", setupPath);
            if (!setup.BrokeAwayFromJob) log.Write("setup: breakaway from job denied; Setup.exe runs inside the caller's job");
            var code = setup.WaitForExit();
            log.Write($"setup: exit {code}");
            if (code != 0)
                return Report(code, $"Setup.exe exited with code {code}.", "See %LOCALAPPDATA%\\WusTechnik.ClaudeUsageTray\\Velopack.log if it exists.", isError: true);

            return Succeed(Wording.Installed(build), build.Via == ResolvedVia.LatestRedirect && ring == Ring.Beta ? build.Describe() : null);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---- reporting ----

    private static string Name(Ring ring) => ring == Ring.Beta ? "beta" : "stable";

    private int Succeed(string headline, string? detail) => Report(ExitCode.Converged, headline, detail, isError: false);

    private int Fail(int code, string headline, string? detail) => Report(code, headline, detail, isError: true);

    private int Report(int code, string headline, string? detail, bool isError)
    {
        log.Write($"exit {code}: {headline}{(detail is null ? "" : " " + detail)}");
        if (options.Silent)
        {
            ConsoleOutput.TryWriteLine($"{headline}{(detail is null ? "" : " " + detail)} (exit {code})");
        }
        else if (isError)
        {
            Wizard.Error(headline, detail ?? $"Exit code {code}. Details: {SetupLog.DefaultPath}");
        }
        else
        {
            Wizard.Info(headline, detail ?? "");
        }
        return code;
    }
}
```

- [ ] **Step 2: Replace `Program.cs`**

`src/ClaudeUsageTraySetupStub/Program.cs`:

```csharp
using System.Net;
using System.Reflection;

namespace ClaudeUsageTraySetupStub;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var parsed = CliArgs.Parse(args, Environment.GetEnvironmentVariable("GH_TOKEN"));
        var log = new SetupLog(SetupLog.DefaultPath);
        if (parsed.Error is { } error)
        {
            log.Write($"exit {ExitCode.BadArguments}: bad arguments");
            if (!ConsoleOutput.TryWriteLine(error)) Wizard.Error("Bad arguments", error);
            return ExitCode.BadArguments;
        }

        var options = parsed.Options!;
        if (options.ShowHelp) return Print(CliArgs.Usage);
        if (options.ShowVersion) return Print($"ClaudeUsageTraySetup {InformationalVersion()}");

        log.Write($"start: version={InformationalVersion()} ring={options.Ring?.ToString() ?? "unset"} silent={options.Silent} token={(options.Token is null ? "no" : "yes")}");

        // Default system proxy so corporate proxies and TLS inspection work; a long total timeout because
        // the payload is a 58 MB installer on whatever link the user has.
        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = true,
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        try
        {
            return new SetupRun(options, log, http).RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // Last resort: an unexpected exception must still produce a readable exit, not a WER dialog.
            log.Write($"exit {ExitCode.AppControlFailed}: unhandled {e.GetType().Name}: {e.Message}");
            if (!options.Silent) Wizard.Error("Setup failed unexpectedly.", $"{e.GetType().Name}: {e.Message}\n\nDetails: {SetupLog.DefaultPath}");
            else ConsoleOutput.TryWriteLine($"Setup failed unexpectedly: {e.Message} (exit {ExitCode.AppControlFailed})");
            return ExitCode.AppControlFailed;
        }
    }

    private static int Print(string text)
    {
        if (!ConsoleOutput.TryWriteLine(text)) Wizard.Info(Wizard.Title, text);
        return ExitCode.Converged;
    }

    /// <summary>`1.0.0+&lt;sha&gt;` when CI passed -p:SourceRevisionId, plain `1.0.0` locally. The stub
    /// cannot auto-update, so this is what support asks a user for.</summary>
    private static string InformationalVersion()
        => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
```

- [ ] **Step 3: Build and smoke-test from source**

```powershell
dotnet build ClaudeUsageTray.sln
dotnet test
dotnet run --project src/ClaudeUsageTraySetupStub -- --help;    $LASTEXITCODE   # expect usage text, 0
dotnet run --project src/ClaudeUsageTraySetupStub -- --version; $LASTEXITCODE   # expect "ClaudeUsageTraySetup 1.0.0", 0
dotnet run --project src/ClaudeUsageTraySetupStub -- --ring nightly; $LASTEXITCODE   # expect 3001
```

Expected: all three exit codes as noted. `--help` output may appear after the shell prompt (console attach quirk); that is fine.

Then the one flow that touches nothing remote and is safe on the dev machine, which has the app installed on `win`:

```powershell
dotnet run --project src/ClaudeUsageTraySetupStub -- --silent; $LASTEXITCODE
```

Expected: `3004`, and a `state: installed 0.7.2 channel=win ...` line followed by `decision: Ambiguous` in `%APPDATA%\ClaudeUsageTray\setup.log`. Do **not** run `--ring beta` against the dev install unless you intend to switch it (it will stop and relaunch the tray and stage a ring change); `--ring stable` is safe and must exit `0` with a "already on the stable ring" line.

Interactive check: `dotnet run --project src/ClaudeUsageTraySetupStub` must show the ring page with **Stable** preselected and the installed version in the text; Cancel must exit `3006`.

- [ ] **Step 4: Commit**

```bash
git add src/ClaudeUsageTraySetupStub/SetupRun.cs src/ClaudeUsageTraySetupStub/Program.cs
git commit -m "feat(setup-stub): wire the run: detect, decide, install or switch ring, report

Refs #21"
```

---

### Task 14: Publish pipeline — `setup-stub.yml` and `release.yml`

**Files:**
- Create: `.github/workflows/setup-stub.yml`
- Modify: `.github/workflows/release.yml`

**Interfaces:**
- Produces the canonical URL `https://github.com/wus-technik/win_systray-claude-usage/releases/download/setup-stub/ClaudeUsageTraySetup.exe` and a copy of the same bytes on every `v*` release.

- [ ] **Step 1: Create the stub workflow**

`.github/workflows/setup-stub.yml`:

```yaml
name: Setup stub

# Publishes ClaudeUsageTraySetup.exe to the permanent `setup-stub` release. main only: a push on any
# feature branch would otherwise --clobber the canonical production asset. The paths filter includes
# the linked shared source, which would otherwise change without the stub being rebuilt.
on:
  workflow_dispatch:
  push:
    branches:
      - main
    paths:
      - "src/ClaudeUsageTraySetupStub/**"
      - "src/ClaudeUsageTray/Core/UpdateRing.cs"
      - ".github/workflows/setup-stub.yml"

permissions:
  contents: write

jobs:
  publish:
    runs-on: windows-latest

    steps:
      - name: Checkout
        uses: actions/checkout@v7

      - name: Setup .NET
        uses: actions/setup-dotnet@v6
        with:
          dotnet-version: "10.0.x"

      - name: Test
        run: dotnet test tests/ClaudeUsageTraySetupStub.Tests --configuration Release

      - name: Publish (NativeAOT)
        shell: pwsh
        # Needs the MSVC linker, which is why the stub is published here and not locally. SourceRevisionId
        # lands in the informational version that `--version` prints.
        run: dotnet publish src/ClaudeUsageTraySetupStub --configuration Release --runtime win-x64 -p:SourceRevisionId=${{ github.sha }} -o artifacts/stub

      - name: Smoke test the binary
        shell: pwsh
        run: |
          $exe = "artifacts/stub/ClaudeUsageTraySetup.exe"
          if (-not (Test-Path $exe)) { throw "publish produced no exe" }
          $size = (Get-Item $exe).Length
          Write-Host ("ClaudeUsageTraySetup.exe is {0:N1} MB" -f ($size / 1MB))
          if ($size -gt 8MB) { throw "stub is unexpectedly large ($size bytes): did AOT trimming fail?" }
          & $exe --version
          if ($LASTEXITCODE -ne 0) { throw "--version exited $LASTEXITCODE" }
          & $exe --ring nightly
          if ($LASTEXITCODE -ne 3001) { throw "--ring nightly exited $LASTEXITCODE, expected 3001" }

      - name: Ensure the setup-stub release exists and is never 'latest'
        shell: pwsh
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        # GitHub picks the newest non-draft, non-prerelease release as /releases/latest. A plain release
        # here would take that title and break both the README's install link and the stub's own stable
        # resolution — only when the stub is republished, the worst time to find out. --latest=false is
        # sticky on the release object; it is asserted below anyway.
        run: |
          gh release view setup-stub --repo $env:GITHUB_REPOSITORY *> $null
          if ($LASTEXITCODE -ne 0) {
            gh release create setup-stub --repo $env:GITHUB_REPOSITORY --latest=false --target main `
              --title "Claude Usage Tray Setup (permanent link)" `
              --notes "Version-independent installer. Download ClaudeUsageTraySetup.exe and run it; it installs the newest release for the ring you pick (stable or beta) or switches the ring of an existing install. Rebuilt automatically when its source changes."
            if ($LASTEXITCODE -ne 0) { throw "could not create the setup-stub release" }
          }

      - name: Upload the exe
        shell: pwsh
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          gh release upload setup-stub artifacts/stub/ClaudeUsageTraySetup.exe --repo $env:GITHUB_REPOSITORY --clobber
          if ($LASTEXITCODE -ne 0) { throw "upload failed" }

      - name: Assert /releases/latest still resolves to a v* tag
        shell: pwsh
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          $latest = gh api "repos/$env:GITHUB_REPOSITORY/releases/latest" --jq .tag_name 2>$null
          if ($LASTEXITCODE -ne 0) { Write-Host "No latest release yet (no stable release published so far)."; exit 0 }
          if ($latest -notmatch '^v\d') {
            throw "/releases/latest resolves to '$latest'. The README link and the stub's stable resolution depend on it being a v* tag; unset 'latest' on that release."
          }
          Write-Host "/releases/latest -> $latest"
```

- [ ] **Step 2: Modify `release.yml`**

Add this step directly after the existing `Test` step:

```yaml
      - name: Assert /releases/latest resolves to a v* tag
        shell: pwsh
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        # The setup stub's stable resolution and the README's install link are both built on
        # /releases/latest/download. If some other release (the permanent setup-stub one) has taken the
        # 'latest' title, fail here rather than ship an update onto a broken install path.
        run: |
          $latest = gh api "repos/$env:GITHUB_REPOSITORY/releases/latest" --jq .tag_name 2>$null
          if ($LASTEXITCODE -ne 0) { Write-Host "No latest release yet."; exit 0 }
          if ($latest -notmatch '^v\d') { throw "/releases/latest resolves to '$latest', not a v* tag. Unset 'latest' on that release before releasing." }
          Write-Host "/releases/latest -> $latest"
```

Add this step at the very end of the job, after `Upload and publish release`:

```yaml
      - name: Attach the setup stub
        shell: pwsh
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        # Copied, not rebuilt: identical bytes on every release, no AOT cost here, and the stub only
        # changes when its own inputs do (setup-stub.yml's paths filter). Missing stub release = warn,
        # not fail: the first release after this workflow lands may run before the stub has ever built.
        run: |
          gh release view setup-stub --repo $env:GITHUB_REPOSITORY *> $null
          if ($LASTEXITCODE -ne 0) { Write-Host "::warning::No setup-stub release yet; ClaudeUsageTraySetup.exe not attached."; exit 0 }
          gh release download setup-stub --repo $env:GITHUB_REPOSITORY --pattern ClaudeUsageTraySetup.exe --dir artifacts/stub
          if ($LASTEXITCODE -ne 0) { throw "could not download ClaudeUsageTraySetup.exe from the setup-stub release" }
          gh release upload "v$env:VERSION" artifacts/stub/ClaudeUsageTraySetup.exe --repo $env:GITHUB_REPOSITORY --clobber
          if ($LASTEXITCODE -ne 0) { throw "could not attach ClaudeUsageTraySetup.exe to v$env:VERSION" }
```

- [ ] **Step 3: Validate the YAML locally and check the action majors**

```powershell
# Both workflows use the same action majors the repo already pins (checkout@v7, setup-dotnet@v6);
# confirm they still declare node24 rather than assuming:
gh api repos/actions/checkout/contents/action.yml?ref=v7 --jq .content | % { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_)) } | Select-String using:
gh api repos/actions/setup-dotnet/contents/action.yml?ref=v6 --jq .content | % { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_)) } | Select-String using:
```

Expected: both print `using: 'node24'`. If either does not, pin the major that does.

- [ ] **Step 4: Commit and push to a branch; run the workflow once by hand**

```bash
git add .github/workflows/setup-stub.yml .github/workflows/release.yml
git commit -m "ci(setup-stub): publish the stub to the permanent setup-stub release

The release is created with --latest=false and both workflows assert that /releases/latest
still resolves to a v* tag, since the README link and the stub's own stable resolution depend on
it. release.yml copies the stub onto each release instead of rebuilding it.

Refs #21"
```

After the feature branch is merged to `main`, the push triggers `setup-stub.yml`. Verify:

```powershell
gh run list --workflow setup-stub.yml --limit 1
gh release view setup-stub --json isLatest,assets --jq '{isLatest, assets: [.assets[].name]}'
gh api repos/wus-technik/win_systray-claude-usage/releases/latest --jq .tag_name   # must still be v0.7.2 (or newer v*)
```

Expected: `isLatest: false`, one asset `ClaudeUsageTraySetup.exe`, and `/releases/latest` unchanged. Then download the published exe and run `--version` — it must print `1.0.0+<sha>`.

---

### Task 15: Documentation — `README.md`, `CLAUDE.md`

**Files:**
- Modify: `README.md` (Install section, design-doc table)
- Modify: `CLAUDE.md` (new section on the stub)

- [ ] **Step 1: Rewrite the README install section**

Replace the current `## Install` block (from `## Install` up to but excluding `## ` of the next section) with:

````markdown
## Install

Download **[`ClaudeUsageTraySetup.exe`](https://github.com/wus-technik/win_systray-claude-usage/releases/download/setup-stub/ClaudeUsageTraySetup.exe)**
and run it. The link never changes: the small launcher fetches the newest release for the ring you
pick — **Stable** or **Beta** — and hands off to that release's installer. Run it again later to
move an existing install between the two rings.

> [!NOTE]
> Per-user install — no admin rights, nothing written outside your profile. Updates apply
> themselves in the background; the tray menu offers **Restart to update** when one is staged.
> Prefer no installer? A portable `.zip` ships with every release, as does the classic
> `WusTechnik.ClaudeUsageTray-win-Setup.exe`.

<details>
<summary><b>Unattended deployment</b></summary>

<br>

```
ClaudeUsageTraySetup.exe --ring stable|beta --silent [--token <t>]
```

Always pass `--ring`: with `--silent` and no ring on a machine that already has the app, the
launcher changes nothing and exits `3004`, because a default would silently drag a deliberate beta
opt-in back to stable. Running it repeatedly with the same `--ring` is idempotent and exits `0`.

It must run **in the user's session**, not as SYSTEM (Intune Win32 apps and SCCM programs default
to SYSTEM). From that context a per-user install lands in the SYSTEM profile and is useless, so the
launcher refuses with `3001`. For detection rules, check for
`%LOCALAPPDATA%\WusTechnik.ClaudeUsageTray\current\ClaudeUsageTray.exe` or the Velopack uninstall key
under `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall`.

`--ring beta` looks up GitHub's release list, which is rate-limited per source IP (60/h
unauthenticated, shared behind a NAT). A fleet rollout should pass `--token` (or set `GH_TOKEN`) or
roll out stable and let users tick **Use beta releases** in the app. Without the API the beta path
fails closed in silent mode rather than installing stable content behind an operator's back.

Switching the ring of an installed app stops and restarts it and writes `useBetaReleases`; the move
itself completes when the user accepts **Restart to update** — the launcher never applies packages.

| Exit code | Meaning |
|---|---|
| `0` | Installed, ring changed, or already correct |
| *other* | `Setup.exe` ran and returned this code |
| `3001` | Bad arguments, or SYSTEM / session-0 context |
| `3002` | No release found for the ring (or API unavailable in silent mode) |
| `3003` | Download failed, or the file was empty, not an executable, or failed its digest check |
| `3004` | `--silent` without `--ring` on an existing install |
| `3005` | Could not stop or restart the app, or the settings write did not persist |
| `3006` | Cancelled |

Diagnostics land in `%APPDATA%\ClaudeUsageTray\setup.log`. `ClaudeUsageTraySetup.exe --version`
prints the launcher's own version and build commit.

</details>
````

Also add a row to the design-docs table at the end of the README:

```markdown
| [`specs/2026-09-04-beta-release-ring-design.md`](docs/superpowers/specs/2026-09-04-beta-release-ring-design.md) | The stable/beta release rings and the return-to-stable downgrade |
| [`specs/2026-09-04-setup-stub-design.md`](docs/superpowers/specs/2026-09-04-setup-stub-design.md) | The version-independent setup launcher and ring switching |
```

- [ ] **Step 2: Add the stub section to `CLAUDE.md`**

Insert after the `## Releasing` section (before `## Design docs`):

````markdown
## The setup stub

`src/ClaudeUsageTraySetupStub/` is a second executable, `ClaudeUsageTraySetup.exe`: a ~2 MB NativeAOT
launcher published to the permanent `setup-stub` release. It resolves the newest release for a ring,
downloads that release's channel `Setup.exe` and runs it; against an existing install it only writes
`useBetaReleases` and restarts the tray. Design: `docs/superpowers/specs/2026-09-04-setup-stub-design.md`.

- **No WinForms, no Velopack.** `Core/UpdateRing.cs` is linked in as shared source
  (`<Compile Include=… Link=…/>`) for the channel names only. The stub never calls `UpdateRing.For`
  and never decides a downgrade — that is the app's job.
- **Its tests live in `tests/ClaudeUsageTraySetupStub.Tests`**, which references the stub only. That
  project and `tests/ClaudeUsageTray.Tests` both export `ClaudeUsageTray.Core.UpdateRing`, so one test
  project referencing both is CS0433. Same rule as the app: decisions are pure functions there.
- **Published in CI only** (`.github/workflows/setup-stub.yml`, on push to `main` when its paths
  change). `dotnet publish` needs the MSVC linker for ILC; `dotnet build`, `dotnet test` and
  `dotnet run --project src/ClaudeUsageTraySetupStub -- --help` work locally without it.
- **The `setup-stub` release must never become `/releases/latest`.** It is created with
  `--latest=false`, and both workflows assert that `latest` still resolves to a `v*` tag. If that
  assertion ever fails, unset "latest" on the offending release; do not remove the assertion.
- `release.yml` copies `ClaudeUsageTraySetup.exe` from the `setup-stub` release onto each release. The
  `win-beta` mirror on stable releases is also what the stub's beta fallback downloads — a third
  reason that mirror is mandatory.
- Testing a ring switch by hand stops and relaunches the installed tray and stages a cross-ring
  package. `--ring stable` against a stable install is the safe idempotence check (exit `0`);
  `--silent` alone against any install must exit `3004`.
````

- [ ] **Step 3: Check rendering and links**

```powershell
git diff --stat
Select-String -Path README.md -Pattern 'setup-stub/ClaudeUsageTraySetup.exe'
```

Expected: one canonical link in the README, the table row present, and the CLAUDE.md section between "Releasing" and "Design docs".

- [ ] **Step 4: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: document the setup stub for users and for working on it

Refs #21"
```

---

## Self-review against the spec

**Spec coverage** — each spec section and the task that implements it:

| Spec section | Task |
|---|---|
| Decision: thin launcher, Velopack keeps installing | 13 (`InstallAsync` runs the downloaded `Setup.exe`) |
| What the stub is not allowed to decide (no `For`, three actions against an install) | 1 (`Rings` uses only the constants), 13 (`ChangeRing`) |
| What a ring switch does — staging wording | 5 (`Wording.SwitchStaged`), 13 |
| The stale settings file — reconcile on every run | 9 (`NeedsReconcile`), 13 (`InstallAsync` reconciles; `ChangeRing` writes) |
| Release resolution: stable redirect, beta API, SemVer by tag, skip non-SemVer, one page, two failure kinds, fallback, silent fails closed, `--token`/`GH_TOKEN` | 3, 4, 6, 2 |
| Integrity: digest when from API, PE and zero-length checks, no digest on the stable path | 7 |
| CLI surface incl. `--version` with build commit, no `--installto` | 2, 13, 14 (`SourceRevisionId`) |
| User context only (SYSTEM / session 0 → `3001`) | 11, 13 |
| Exit codes and the `3004` rule | 1, 10, 13 |
| Flows: fresh interactive (radio page, progress page), already installed (sq.version → HKCU fallback, current ring settings-first, refuse mid-apply, stop → write → relaunch, relaunch on failure, leave stopped if not running) | 8, 10, 11, 12, 13 |
| Launch children detached (`CREATE_BREAKAWAY_FROM_JOB`, explorer fallback) | 11 |
| Editing settings.json as a DOM, case-insensitive in place, atomic, refuse malformed | 9 |
| Portable-zip warning | 13 |
| Build and publish: NativeAOT, manifest, source-generated JSON, CI-only publish, proxy/timeouts/retries, temp dir cleanup, `setup.log` | 1, 4, 6, 11, 13, 14 |
| Canonical URL, `--latest=false`, `release.yml` assertion and copy, paths filter incl. `UpdateRing.cs` | 14 |
| Testing: separate project; every bullet in the spec's list | 1–11 (argument parsing 2; SemVer 3; selection 4; failure kinds 6; `Describe`/switch messages 5; asset names 1; `sq.version` 8; current ring 8; DOM edit and stale reconciliation 9; digest/PE 7; `3004` rule 10) |

**Gaps accepted knowingly:** the wizard (Task 12) and the process/dialog shell (Task 11) are not unit-tested, per the spec's last line. Task 13's smoke run is their check.

**Type consistency check:** `StubOptions` (Task 2) is consumed by `Flow.Decide` (10) and `SetupRun` (13) with the same five fields; `ResolvedBuild` (4) fields `Ring, Channel, Version, Url, Digest, Via` are used identically in 5, 6, 7, 13; `SettingsReadResult.UseBetaReleases` (9) is what `CurrentRing.Resolve` (8) and `NeedsReconcile` (9) take; `InstallInfo(Version, Channel)` (8) is what `Flow.Decide` (10) and `Wizard.ChooseRing` (12) receive; `HttpRetry.DefaultDelays` (6) is passed in 13 to both the resolver and the downloader.
