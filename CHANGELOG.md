# Changelog

All notable changes to Claude Usage Tray are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The section for the version being released is what the in-app update dialog shows, so each entry is
written for the person deciding whether to install it — not for the person who wrote the commit.

## [Unreleased]

## [0.7.3] - 2026-09-11

Everything below since 0.7.2, gathered for anyone updating from it. The four `0.7.3-beta` sections
that follow are the same changes as they reached the beta ring.

### Added

- **Usage for Claude Desktop users.** The tray reads the Claude Desktop app's own usage history when
  Claude Code's data is missing or stale, so a machine that only uses the desktop app shows its
  5-hour and 7-day percentages instead of a permanent `—`. The 5-hour reset is inferred from that
  history, so pace colours, the elapsed marker and `resets in …` work there too; inferred times are
  marked with a tilde and never raise a notification. State your weekly reset under **Settings →
  General → Claude Desktop** (e.g. `Thu 03:00`) and the 7-day window paces against it.

- **Desktop notifications.** A Windows toast when a usage limit turns red — whichever rule coloured
  it — and one each way when a watched status page goes down or recovers, in the page's own words.
  Exactly one toast per red period, none at launch into an already-red state, and none for a settings
  edit. Toasts land in Action Center and follow Focus Assist; clicking one opens the popup. All of it
  is on by default and switchable under **Settings → Status → Notifications**.

- **OpenAI status as an optional second source.** For anyone running Codex next to Claude Code, the
  popup can show status.openai.com's banner under Claude's, in the page's own words. Off by default;
  tick **Watch OpenAI status**. An OpenAI disruption appears in the popup and the tooltip but never
  marks the tray icon — the badge still means "Claude is degraded, which is why your numbers may
  have stopped moving." Each page is polled on its own schedule, so one timing out cannot delay or
  blank the other.

- **Watched components**, for both pages. A comma-separated filter per source decides which of a
  page's components you care about (OpenAI lists 25, most irrelevant to a Codex user). A disruption
  affecting only unwatched components shows greyed and marked *outside your watched components*, and
  no longer marks the tray icon; one the page cannot attribute to any component is always shown in
  full. An empty filter — the Claude default — watches everything, so nothing changes unless you
  narrow the list yourself.

- **A tabbed Settings dialog.** The sections are grouped into **General**, **Appearance**, **Status**
  and **About**, so the window is as tall as its largest group rather than the sum of all of them.
  Nothing moved out of the dialog and nothing changed what it does.

- **Clickable page lists.** Under each components filter, every name the page currently reports is a
  link that adds itself to the box — no more retyping a component name to exclude the rest.

- New settings keys: `desktopStalenessHours` (default 3, an hours-scale cutoff because the desktop
  app records usage only while you work in it), `weeklyResetAnchor`, `usageNotifications`, and
  `statusSources` with `enabled`, `components` and `notify` per page. File-only:
  `desktopHistoryPathOverride`. An existing settings file keeps today's behaviour and gains the new
  keys on its next save.

### Changed

- When there is no usage data at all, the popup and tooltip say what is missing — `.claude.json`
  absent, present without a usage block, no credentials file for the live fetch, or an empty desktop
  history — instead of always suggesting you run Claude Code.

- Popup incident and component rows, the status headers and the tooltip's status suffix are built by
  the same rules for both pages. When the tooltip runs long, the usage text is what gets shortened,
  so the `· Claude: …` suffix that explains a warning badge always survives.

- The installed size grows by about 24 MB (the Windows SDK projection that toasts need). Velopack
  deltas mean you download it once. The supported Windows floor is now Windows 10 2004 (build 19041).

### Fixed

- A window with no known reset says `no reset time` on a desktop snapshot, instead of leaving a
  silent blank where the reset would be.

## [0.7.3-beta.4] - 2026-09-10

### Added
- Claude Desktop history: the 5-hour reset is now inferred from the sample series, so pace colours,
  the elapsed marker and `resets in …` work on a desktop-only machine. Estimates are marked with a
  tilde and never raise a notification.
- `weeklyResetAnchor` setting and a **Claude Desktop** settings group: state your weekly reset
  (e.g. `Thu 03:00`) and the 7-day window paces against it.

### Fixed
- A window with no known reset now says `no reset time` on a desktop snapshot, instead of leaving a
  silent blank where the reset would be.

## [0.7.3-beta.3] - 2026-09-06

### Added

- **Desktop notifications.** A Windows toast when a usage limit turns red — whichever rule coloured
  it — and one each way when a watched status page goes down or recovers, in the page's own words.
  Exactly one toast per red period, none at launch into an already-red state, none for a settings
  edit, and a recovery toast that never claims a page is healthy while it still reports a disruption
  outside your watched components. Toasts land in Action Center and follow Focus Assist; clicking one
  opens the popup. New **Notifications** group in Settings, plus `usageNotifications` and a per-source
  `notify` key in `settings.json`, all on by default.

### Changed

- The installed size grows by about 24 MB (the Windows SDK projection that toasts need). Velopack
  deltas mean you download it once. The supported Windows floor is now Windows 10 2004 (build 19041).

## [0.7.3-beta.2] - 2026-09-05

### Added

- **OpenAI status as an optional second source.** For anyone running Codex next to Claude Code, the
  popup can now show status.openai.com's banner under Claude's, in the page's own words. It is off
  by default; tick **Watch OpenAI status** in Settings to turn it on. An OpenAI disruption appears in
  the popup and the tooltip but never marks the tray icon — the badge still means "Claude is
  degraded, which is why your numbers may have stopped moving." Each page is polled on its own
  schedule, so one page timing out cannot delay or blank the other.
- **Watched components.** OpenAI's page lists 25 components, most of them irrelevant to a Codex user,
  so the new setting comes with a comma-separated component filter (default `codex, responses,
  login, vs code extension`, matched as case-insensitive substrings). A disruption that only affects
  unwatched components shows a grey banner marked *outside your watched components* and adds nothing
  to the tooltip; one the page cannot attribute to any component is always shown in full. The
  filter never hides the badge.
- **`statusSources`** in `settings.json`, one entry per page with `enabled` and `components`. The
  `claude` entry accepts a filter too, as an advanced file-only setting that narrows the popup rows
  and the tooltip but not the badge. An existing settings file keeps today's behaviour and gains
  the key on its next save.

### Changed

- Popup incident and component rows, the status headers, and the tooltip's status suffix are now
  built by the same rules for both pages. When the tooltip runs long, the usage text is what gets
  shortened, so the `· Claude: …` suffix that explains a warning badge always survives.

## [0.7.3-beta.1] - 2026-09-05

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
- **`desktopHistoryPathOverride`** in `settings.json` (file-only), for pointing the tray at a
  `plan-usage-history.json` in a location it does not know about.

### Changed

- When there is no usage data at all, the popup and tooltip now say what is missing — `.claude.json`
  absent, present without a usage block, no credentials file for the live fetch, or an empty desktop
  history — instead of always suggesting you run Claude Code. The popup's *Fetch* line also explains
  when the live fetch is off for lack of credentials.

## [0.7.2] - 2026-09-04

Everything below since 0.7.1, gathered for anyone updating from it. The two `0.7.2-beta` sections
that follow are the same changes as they reached the beta ring.

### Added

- **Use beta releases**, a checkbox in the Settings dialog. With it on, the updater also offers
  pre-release builds (`0.7.3-beta.1` and the like); with it off — the default — nothing changes and
  only stable releases are ever offered. The switch takes effect on the next update check, with no
  restart, and unticking it moves you back to the latest stable build, even when that means stepping
  down from a newer beta. Stable releases keep reaching beta users too, so opting in never means
  falling behind.

- The app has an icon of its own: the exe in Explorer, the installer, the desktop shortcut, the
  Start menu entry and the Settings and update windows all show a speedometer instead of the generic
  default. The tray badge is unchanged.

- The creator line in the Settings dialog's About section links to the project page.

## [0.7.2-beta.2] - 2026-09-04

### Fixed

- A build installed from the beta installer no longer talks itself back onto stable. Because it had
  no saved answer to **Use beta releases** yet, its first update check treated that as "stable
  please" and offered the older stable release — undoing the install. Until you tick or untick the
  box yourself, the app now follows the channel it was installed from; ticking or unticking it still
  decides everything from then on.

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

[Unreleased]: https://github.com/wus-technik/win_systray-claude-usage/compare/v0.7.3...HEAD
[0.7.3]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.7.3
[0.7.3-beta.1]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.7.3-beta.1
[0.7.3-beta.2]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.7.3-beta.2
[0.7.2]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.7.2
[0.7.2-beta.2]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.7.2-beta.2
[0.7.2-beta.1]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.7.2-beta.1
[0.7.1]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.7.1
[0.7.0]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.7.0
[0.6.2]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.6.2
[0.6.1]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.6.1
[0.6.0]: https://github.com/wus-technik/win_systray-claude-usage/releases/tag/v0.6.0
