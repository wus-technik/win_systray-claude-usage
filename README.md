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
  refresh, apply a staged update with **Restart to update**, or quit.

## Settings

`%APPDATA%\ClaudeUsageTray\settings.json` (edit by hand; no UI in v1):

| Key | Meaning | Default |
|---|---|---|
| `displayMode` | `"fiveHour"` \| `"sevenDay"` \| `"both"` | `"both"` |
| `thresholds` | `{ "orange": 50, "red": 85 }` severity boundaries (%) | as shown |
| `stalenessMinutes` | minutes before cached data is flagged stale | `15` |
| `runAtStartup` | applied to the HKCU Run key at every installed launch | `true` |
| `configPathOverride` | explicit path to `.claude.json` (mainly tests) | unset |

## Development

    dotnet test                              # unit tests (core logic)
    dotnet run --project src/ClaudeUsageTray # run the tray app
    .\build\build-release.ps1                # publish + vpk pack → .\Releases

Production releases are published by the tag-triggered GitHub Actions workflow.
