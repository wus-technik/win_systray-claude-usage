<div align="center">

# Claude Usage Tray

**Your Claude limits, always visible — right next to the clock.**

A tiny Windows tray app that shows your **5-hour** and **7-day** usage as filled badge
icons, plus per-model weekly caps and paid credit spend at a glance.

[![Latest release](https://img.shields.io/github/v/release/wus-technik/win_systray-claude-usage?style=for-the-badge&logo=github&color=6C4BF6)](https://github.com/wus-technik/win_systray-claude-usage/releases/latest)
[![CI](https://img.shields.io/github/actions/workflow/status/wus-technik/win_systray-claude-usage/ci.yml?branch=main&style=for-the-badge&label=CI)](https://github.com/wus-technik/win_systray-claude-usage/actions/workflows/ci.yml)
[![Downloads](https://img.shields.io/github/downloads/wus-technik/win_systray-claude-usage/total?style=for-the-badge&color=2EA043)](https://github.com/wus-technik/win_systray-claude-usage/releases)

[![License: MIT](https://img.shields.io/github/license/wus-technik/win_systray-claude-usage?style=flat-square&color=blue)](LICENSE)
![Windows](https://img.shields.io/badge/Windows_10%2F11-x64-0078D6?style=flat-square&logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![Install](https://img.shields.io/badge/install-per--user,_no_admin-lightgrey?style=flat-square)
![Auto-update](https://img.shields.io/badge/updates-Velopack-informational?style=flat-square)

<br>

<img src="docs/screenshot-popup.png" alt="The Claude Usage Tray popup showing the 5-hour window at 4%, the 7-day window at 1%, a Fable weekly row at 0%, and credits at 40.01 of 60.00 EUR" width="360">

<sub>Left-click the tray icon for the full picture. Every bar is colour-coded by severity.</sub>

</div>

---

## Why

Claude Code tells you that you hit a limit *after* you hit it. This sits in your tray and
tells you it's coming — the rolling windows, the per-model weekly cap that throttles first,
and how much paid credit you've burned this month.

## What you get

| | |
|---|---|
| 📊 **Two badge icons** | Fill = usage, colour = severity, centre digit = window (`5` or `7`) |
| 🎯 **Per-model caps** | Weekly limits scoped to a model (e.g. Fable) or surface — the cap that usually bites first |
| 💳 **Credit spend** | Paid extra-usage against your limit, in **your** account's currency |
| ⏱️ **Reset countdowns** | "resets in 3h 58m", on hover and in the popup |
| 🔄 **Live + offline** | Polls Anthropic's usage API every 5 min; falls back to Claude Code's local cache |
| 🚀 **Zero-friction** | Per-user install, no admin, auto-updates, starts at login |

## Install

Grab **`WusTechnik.ClaudeUsageTray-win-Setup.exe`** from the
[latest release](https://github.com/wus-technik/win_systray-claude-usage/releases/latest)
and run it.

> [!NOTE]
> Per-user install — no admin rights, nothing written outside your profile. Updates apply
> themselves in the background; the tray menu offers **Restart to update** when one is staged.
> Prefer no installer? A portable `.zip` ships with every release.

## Using it

### The icons

| What you see | What it means |
|---|---|
| 🟢 Green badge | Under 50 % |
| 🟠 Orange badge | 50 – 85 % |
| 🔴 Red badge | Over 85 % |
| Dimmed | Stale data (cache older than 15 min) |
| Grey `—` | No data yet — run Claude Code once |

### Interactions

- **Hover** → exact percentage and reset countdown
- **Left-click** → the popup above: both windows, any scoped weekly caps, credit spend
- **Right-click** → `Show 5h` / `Show 7d` / `Show both`, run-at-startup, `Refresh now`,
  `Restart to update`, `Quit`

<details>
<summary><b>How the popup decides what to show</b></summary>

<br>

Rows appear only when the data behind them exists. Nothing is ever rendered as a
placeholder `0 %` or `—` for a limit you don't have:

- **Scoped weekly rows** are read from the payload's `limits[]` array and **labelled from the
  payload itself**, so a renamed or newly added model shows up with no app update. On a plan
  without per-model caps, there's simply no entry — and therefore no row.
- **Currently-binding limits sort first** and are never hidden, even when several caps exist.
  Background limits are capped at four rows with a `+N more` line, so the popup can't grow
  off-screen.
- **Credits** are shown with your account's own ISO currency code and decimal precision —
  never an assumed `$`. Their bar follows the server's own severity, because "spend cap
  reached" is state a percentage can't express. `disabled` and `limit reached` get their own
  line rather than being hidden.

</details>

<details>
<summary><b>Where the data comes from</b></summary>

<br>

Two sources, newest wins:

1. **Live** — read-only `GET` against Anthropic's OAuth usage endpoint every 5 minutes,
   the same source claude.ai uses, authenticated with **Claude Code's existing token**.
   The app never stores, refreshes, or logs that token, and respects `Retry-After` on 429s.
2. **Offline fallback** — the `cachedUsageUtilization` block Claude Code writes to
   `%USERPROFILE%\.claude.json`, used whenever no valid token is available.

A rolling diagnostic log lands in `%APPDATA%\ClaudeUsageTray\fetch.log` so a
"stale, never refreshes" report is debuggable. It records fetch outcomes and the two window
percentages only — **never** money amounts, currency, or account-specific model names.

</details>

## Settings

Hand-edit `%APPDATA%\ClaudeUsageTray\settings.json` — there's no settings UI yet.

| Key | Meaning | Default |
|---|---|---|
| `displayMode` | `"fiveHour"` \| `"sevenDay"` \| `"both"` | `"both"` |
| `thresholds` | `{ "orange": 50, "red": 85 }` severity boundaries (%) | as shown |
| `stalenessMinutes` | minutes before data is flagged stale | `15` |
| `runAtStartup` | applied to the HKCU `Run` key at every installed launch | `true` |
| `configPathOverride` | explicit path to `.claude.json` (mainly for tests) | unset |

## Development

```powershell
dotnet test                              # unit tests (all core logic)
dotnet run --project src/ClaudeUsageTray # run the tray app
.\build\build-release.ps1                # publish + vpk pack → .\Releases
```

Parsing, formatting, severity, and row-selection logic all live in `Core/` as pure functions
so they're testable without WinForms; `Tray/` only draws. Production releases are published
by the tag-triggered GitHub Actions workflow — push a `v*` tag whose version matches
`<Version>` in the csproj and it validates, tests, packs, and publishes.

<details>
<summary><b>Design docs</b></summary>

<br>

| Document | Covers |
|---|---|
| [`spec/claude-usage-tray.md`](docs/superpowers/spec/claude-usage-tray.md) | The original app specification |
| [`specs/2026-07-24-live-usage-fetch-design.md`](docs/superpowers/specs/2026-07-24-live-usage-fetch-design.md) | Live API polling and rate-limit handling |
| [`specs/2026-07-24-icon-readability-design.md`](docs/superpowers/specs/2026-07-24-icon-readability-design.md) | Badge icon legibility at tray size |
| [`specs/2026-07-27-fable-and-credits-design.md`](docs/superpowers/specs/2026-07-27-fable-and-credits-design.md) | Scoped weekly limits and credit usage |

</details>

---

## License

[MIT](LICENSE) © 2026 W&S Technik GmbH

<div align="center">
<sub>Not affiliated with Anthropic. Reads only your own local Claude Code credentials and usage data.</sub>
</div>
