# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows system-tray app (.NET 10, WinForms, `win-x64` self-contained) that shows Claude usage
limits as badge icons. Shipped as a per-user Velopack install with auto-update. See `README.md` for
the user-facing behaviour (icon semantics, pace colours, settings keys) — it is the reference for
*what* the app does; this file covers *how to work on it*.

## Commands

```powershell
dotnet test                                   # all tests (Debug)
dotnet test --configuration Release           # what CI runs
dotnet test --filter FullyQualifiedName~SeverityRulesTests             # one class
dotnet test --filter "FullyQualifiedName~SeverityRulesTests.Method"    # one test
dotnet run --project src/ClaudeUsageTray      # run the tray app from source
.\build\build-release.ps1                     # publish + vpk pack -> .\Releases
```

CI (`.github/workflows/ci.yml`) runs restore + `dotnet test -c Release` on Windows for every PR and
push to `main`. There is no linter or formatter step — match surrounding style.

## Architecture

Two-layer split, and it is load-bearing:

- **`src/ClaudeUsageTray/Core/`** — pure, WinForms-free logic: parsing, formatting, severity,
  scheduling, row selection. No clocks and no threads: every time-dependent function takes a
  caller-supplied `DateTimeOffset now`. This is what the tests exercise.
- **`src/ClaudeUsageTray/Tray/`** — WinForms only: `TrayApp` (the `ApplicationContext` that owns all
  timers and state), `IconRenderer`, `UsagePopup`, `UsageBar`, `SettingsDialog`.

**Put new logic in `Core/` as a pure function and unit-test it.** Adding a decision to `TrayApp` or a
paint method is how this codebase becomes untestable — the reason `FetchScheduler`, `StatusScheduler`,
`SeverityRules`, `PopupRows`, `TimeMarker`, and `SnapshotPrecedence` exist as separate state machines
is that each was pulled out of the UI to be testable.

### Data flow

Three independent sources feed one `UsageSnapshot` (`Core/UsageSnapshot.cs`), newest wins via
`SnapshotPrecedence.IsNewer` — which is what stops the 30 s cache re-read from clobbering a fresher
API result:

1. **Live API** — `UsageApiClient` GETs `https://api.anthropic.com/api/oauth/usage` every 5 min,
   authenticated with the token `CredentialsReader` reads from `~/.claude/.credentials.json`.
2. **Offline cache** — `UsageCacheReader` parses `cachedUsageUtilization` from `.claude.json`
   (`ConfigPath.Resolve`, overridable via the `configPathOverride` setting). Watched with a
   `FileSystemWatcher` + 500 ms debounce, plus a 30 s tick as the recovery path for missed events.
3. **Platform status** — `PlatformStatusApi` polls status.claude.com every 60 s, no auth. Kept fully
   independent of the usage path: a status failure must never null, clobber, or delay usage data.

Both network paths are gated by a scheduler (`FetchScheduler` for usage, `StatusScheduler` for
status) that owns the floors, backoff, and budget. `TrayApp` enforces single-flight with a bool on
the UI thread. Fetch outcomes land in `%APPDATA%\ClaudeUsageTray\fetch.log` — percentages and
outcomes only, **never** money amounts, currency, or account-specific model names.

### Non-negotiable invariants

- **The token is read-only.** Never write, refresh, or log credential material anywhere, `fetch.log`
  included.
- **Nothing in the read paths throws.** `UsageCacheReader`, `CredentialsReader`, `UsageApiClient`,
  and `Settings.Load` swallow IO/JSON errors and return null / defaults. A malformed file must
  degrade the display, not kill the tray.
- **Labels come from the payload**, not from a hardcoded model list — a renamed or new model has to
  show up with no app update.
- **Absent data means no row.** Never render a placeholder `0 %` or `—` for a limit the account
  does not have.

## Working with the live usage endpoint

`GET /api/oauth/usage` enforces a **tight per-token rolling-hour budget shared across every consumer
of that token** — this tray *and* every running `claude.exe` on the machine. Measured behaviour:
~28–30 requests/h (hence `FetchScheduler`'s default 20/h margin); under contention it returns
`429 rate_limit_error` with `Retry-After: 0` and recovers within ~90 s.

**Do not hammer it while debugging.** Even ~15 quick probes exhaust the budget and cause minutes of
persistent 429s — for the user's real Claude Code sessions too. Test against `UsageApiClientTests`
with canned responses instead of live calls. For the same reason the `.claude.json` cache can lag
many hours: Claude Code hits the same limit.

## Verifying UI drawing without a human

`UsageBar` and `IconRenderer` are painted to a bitmap and sampled in `UsageBarTests` /
`IconRendererTests` — extend those rather than eyeballing the app.

For whole-popup checks, construct a real `UsagePopup` with a synthetic `UsageSnapshot` from a
throwaway test in `tests/ClaudeUsageTray.Tests/` (that project has `UseWindowsForms=true`) and
capture it with `Control.DrawToBitmap`. Scale the capture ~6x with `InterpolationMode.NearestNeighbor`
— the bars are 240x12, too small to judge otherwise.

**Pitfall:** call `CreateControl()`, never `Show()`. `UsagePopup.OnDeactivate` calls `Close()`, and
with no message loop `Show()` triggers an immediate activate/deactivate cycle that disposes the form
— you get a blank render with zero controls and no error. Delete the probe file afterwards; it is a
verification artifact, not a test.

## Testing a build in the real installed app

To install a local build over the existing Velopack install (preserving the auto-update chain):

1. Build a package. `.\build\build-release.ps1` takes its version from the csproj `<Version>`, and
   `Update.exe apply` requires a version *higher* than what is installed. To avoid bumping the csproj
   on a feature branch, run the two steps by hand and override only the pack version with a
   `-local.N` suffix:
   ```powershell
   dotnet publish src/ClaudeUsageTray -c Release -r win-x64 --self-contained -o artifacts\publish
   dnx vpk --version 1.2.0 pack --packId WusTechnik.ClaudeUsageTray `
     --packVersion <ver>-local.N --packDir artifacts\publish --mainExe ClaudeUsageTray.exe
   ```
   The exe's own ProductVersion still reads the csproj version — harmless, since Velopack (and
   `UpdateManager`) compares the *package* version.

   Add `--channel win-beta` to test the beta ring; the assets are then named `-win-beta-` and the
   installed manifest records that channel, which is what makes the app read the beta feed (and what
   `UpdateRing` keys the return-to-stable downgrade off). Without the flag, `vpk pack` defaults to
   `win`, matching a stable install.
2. `Stop-Process -Name ClaudeUsageTray -Force`
3. `& "$env:LOCALAPPDATA\WusTechnik.ClaudeUsageTray\Update.exe" apply --package <path-to-nupkg>`

**Do not use `WusTechnik.ClaudeUsageTray-win-Setup.exe` to update.** Setup.exe is first-install only,
silently no-ops when the app is already installed, and is non-silent so it stalls headless.

**The relaunched app dies when the tool shell ends.** `Update.exe apply` launches the new version as
a child of the shell that ran it; from an agent's PowerShell/Bash call that shell's job object kills
the tray app seconds later — which looks exactly like a startup crash (no WER report, no event log
entry, `fetch.log` just stops after the first poll). Relaunch it detached instead:

```powershell
& explorer.exe "$env:LOCALAPPDATA\WusTechnik.ClaudeUsageTray\current\ClaudeUsageTray.exe"
```

Check the parent with `Get-CimInstance Win32_Process -Filter "Name='ClaudeUsageTray.exe'"` before
diagnosing a real crash.

Paths: installed app `%LOCALAPPDATA%\WusTechnik.ClaudeUsageTray\current\`, settings and log
`%APPDATA%\ClaudeUsageTray\`.

## Releasing

Production releases are tag-triggered (`.github/workflows/release.yml`): bump `<Version>` in
`src/ClaudeUsageTray/ClaudeUsageTray.csproj`, then push a matching `v<Version>` tag. The workflow
hard-fails if tag and csproj version disagree, then tests, downloads the previous release as a delta
baseline, packs, and publishes to GitHub Releases.

### Release rings

`build/release-ring.ps1` derives the ring from `<Version>` and is the only place that decides it —
both `release.yml` and `build-release.ps1` call it, so a local build and CI cannot disagree.

- A prerelease version (`0.7.2-beta.1`, dot-numbered so `beta.10` sorts above `beta.9`) packs to
  channel **`win-beta`** and is uploaded with `--pre`, i.e. as a GitHub pre-release. Only users with
  `useBetaReleases` on are offered it.
- A stable version packs **twice**: channel `win`, then the same build again as the `win-beta`
  mirror, merged into the same GitHub release. **The mirror is mandatory, not cosmetic**: a
  `win-beta` client never reads `releases.win.json`, so without it beta users stall after every
  stable release, and the beta index would eventually fall out of the 10-release window
  `GithubSource` looks at.

Beta versions need their own `## 0.7.2-beta.N` section in `CHANGELOG.md` — `changelog-section.ps1`
hard-fails otherwise, betas included. The client side of this lives in `Core/UpdateRing.cs`; the
design doc (`docs/superpowers/specs/2026-09-04-beta-release-ring-design.md`) records the verified
Velopack behaviour it depends on, including why `AllowVersionDowngrade` is enabled *only* for the
switch back to stable.

Note for both the workflow and `build-release.ps1`: `--packAuthors` is written into the nuspec
verbatim, so the company ampersand must arrive already XML-escaped (`W&amp;S Technik GmbH`) or `vpk`
fails with an `XmlException`. Same in the csproj, where MSBuild rejects a bare `&`.

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

## Design docs

`docs/superpowers/spec*/` holds the design doc per feature and `docs/superpowers/plans/` the
implementation plans. Read the relevant design doc before changing severity/pace rules, the icon
renderer, scoped limits and credits, or platform status — each records why a rule looks odd (e.g. why
`is_active: false` is never filtered on, why pace has a dead zone and a floor).
