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

<img src="docs/screenshot-popup.png" alt="The Claude Usage Tray popup showing the 5-hour window at 39% resetting in 1h 3m, the 7-day window at 58% resetting in 4d 16h, a Fable weekly row at 32%, and credits at 40.01 of 60.00 EUR. A dark vertical marker on each usage bar shows how far the clock has moved through that limit's period." width="360">

<sub>Left-click the tray icon for the full picture. Every bar is colour-coded by severity, and the
marker line shows where the clock is: fill short of it means you're burning slower than the clock.</sub>

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
| 📈 **Pace marker** | A line on each bar marking where the *clock* is in the period — fill past it means you're on track to hit the cap early |
| 🚦 **Pace colours** | Colour follows usage *against the clock*, not raw percent: 60 % with 5½ of 7 days gone stays green, 40 % in the first hour of a 5-hour window goes red |
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
| 🟢 Green badge | On or under the pace the clock sets for the period |
| 🟠 Orange badge | Burning ≥ 1.1× the clock — the cap arrives before the reset |
| 🔴 Red badge | Burning ≥ 1.75× the clock, **or** over 85 % used whatever the pace |
| Dimmed | Stale data (cache older than 15 min) |
| Grey `—` | No data yet — run Claude Code once |

The pace ratio is `percent used ÷ percent of the period elapsed` — the same comparison the marker
line makes visually. When it is what decided the colour, the popup caption and the hover tooltip name
it (`· 1.4× pace`). Two guards override it: below 20 % used, or in the first 10 % of a period, a huge
ratio over a trivial number means nothing and the plain `thresholds` percentages decide instead; and
past `thresholds.red` the badge is red however calm the pace, because running out is running out.
Without a trustworthy reset time the plain percentages decide too. Set `paceColors` to `false` for the
old percent-only behaviour.

### Interactions

- **Hover** → exact percentage and reset countdown
- **Left-click** → the popup above: both windows, any scoped weekly caps, credit spend
- **Right-click** → `Settings…`, `Refresh now`, `Restart to update`, `Quit`

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

**Right-click → `Settings…`** covers everything except `configPathOverride`: which icons to show,
run-at-startup, the two colour thresholds, pace colouring, and the staleness cutoff. Saving applies
at once — the badges and the popup repaint, no restart. A preview bar shows where the thresholds land
before you commit them, and the two spinners constrain each other so `orange` can never reach `red`.

Its **About** section names the running version and, on `Update now`, checks GitHub Releases straight
away rather than waiting for the six-hourly background check. If something newer is there it is
downloaded and you are asked before the restart, and any edits still open in the dialog are saved
first. Outside the installed app there is nothing to update, so the button is disabled.

Everything is also readable and editable in `%APPDATA%\ClaudeUsageTray\settings.json`. An invalid
value there falls back to its default on load — the pair `orange`/`red` resets together, since the
file gives no way to tell which of the two was meant.

| Key | Meaning | Default |
|---|---|---|
| `displayMode` | `"fiveHour"` \| `"sevenDay"` \| `"both"` | `"both"` |
| `thresholds` | `{ "orange": 50, "red": 85 }` severity boundaries (%) — with `paceColors` on, the red value is the absolute ceiling and both are the fallback | as shown |
| `paceColors` | colour by usage against elapsed time instead of raw percent | `true` |
| `stalenessMinutes` | minutes before data is flagged stale | `15` |
| `runAtStartup` | applied to the HKCU `Run` key at every installed launch | `true` |
| `configPathOverride` | explicit path to `.claude.json` (mainly for tests); file-only, and re-read at launch | unset |

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
