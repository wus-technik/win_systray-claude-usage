# Changelog

All notable changes to Claude Usage Tray are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The section for the version being released is what the in-app update dialog shows, so each entry is
written for the person deciding whether to install it — not for the person who wrote the commit.

## [0.7.2-beta.1] - 2026-09-04

First release on the beta channel, and the release that introduces it.

### Added

- **Use beta releases**, a checkbox in the Settings dialog. With it on, the updater also offers
  pre-release builds (`0.7.2-beta.1`, `0.7.2-beta.2`, …); with it off — the default — nothing changes
  and only stable releases are ever offered. The switch takes effect on the next update check, with
  no restart, and unchecking it moves you back to the latest stable build, even when that means
  stepping down from a newer beta. Stable releases keep reaching beta users too, so opting in never
  means falling behind.

- The app has an icon of its own: the exe in Explorer, the installer, the desktop shortcut, the
  Start menu entry and the Settings and update windows all show a speedometer instead of the generic
  default. The tray badge is unchanged.

- The creator line in the Settings dialog's About section links to the project page.

## [0.7.1] - 2026-08-26

### Changed

- The Settings dialog's update row is now two controls instead of one. The refresh button (⟳) checks
  the feed and only checks it; **Update now** stays disabled until a check has actually found
  something. Previously a single "Update now" button did the checking and then offered to restart,
  so there was no way to see which version was waiting before committing to it.
- Choosing **Update now** shows what is changing — the release notes packed with that version — in a
  scrollable window with *Update and restart* and *Later*. Updates published before this release
  carry no notes and fall back to the previous plain confirmation.

### Added

- `CHANGELOG.md`, and a release pipeline that passes the version's section to `vpk pack` so the app
  can show it. A release whose version has no changelog section now fails the workflow.

## [0.7.0] - 2026-08-26

### Added

- Claude platform status. The tray polls the public status page once a minute and shows a warning
  badge on every icon when claude.ai or the API is degraded, so an outage no longer looks like a
  stalled fetch.
- The popup names the disruption and lists up to three open incidents, each with a link to the
  incident on status.claude.com.

### Fixed

- Long platform-status text wraps instead of stretching the popup off the screen edge.

## [0.6.2] - 2026-08-21

### Changed

- W&S Technik GmbH is named as the creator, and the app has one display name everywhere — the
  update UI no longer shows the raw package id.

## [0.6.1] - 2026-08-20

### Added

- The Settings dialog shows the installed version and can check for updates.

## [0.6.0] - 2026-08-20

### Added

- A Settings dialog for the colour thresholds.
- Pace colouring: bars and badges take their colour from usage against time elapsed rather than the
  absolute percentage, so being 40 % through a limit an hour into a 5-hour window reads differently
  from 40 % with ten minutes left. Switch it off with the `paceColors` setting.

## Earlier releases

0.5.1 and earlier predate this file. See the
[releases page](https://github.com/wus-technik/win_systray-claude-usage/releases) for what shipped in
them.

[Unreleased]: https://github.com/wus-technik/win_systray-claude-usage/compare/v0.7.2-beta.1...HEAD
[0.7.2-beta.1]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.7.2-beta.1
[0.7.1]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.7.1
[0.7.0]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.7.0
[0.6.2]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.6.2
[0.6.1]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.6.1
[0.6.0]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.6.0
