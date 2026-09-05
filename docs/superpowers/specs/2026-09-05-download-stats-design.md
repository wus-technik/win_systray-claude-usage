# Download and update statistics without telemetry

Refs #22.

## Goal

Know roughly how many installs the tray app has and how many of them pick up each release,
without collecting anything that identifies a user or a machine.

## Constraints

- **The app sends nothing.** No beacon, ping, install id, machine id, or crash report. The tray's
  outbound traffic stays exactly what it is today: the usage API, status.claude.com, and the
  Velopack update feed on GitHub Releases.
- **No third-party analytics.** The only data source is GitHub's public per-asset `download_count`,
  which anyone can read from the releases API. We add no information GitHub does not already
  publish; we only keep a history of it.
- **Nothing under `src/` changes.**

## Why GitHub asset counts are enough

Every file Velopack touches is a release asset, and GitHub counts each download:

| Asset name pattern                            | `kind`            | Approximates                                                                 |
|-----------------------------------------------|-------------------|------------------------------------------------------------------------------|
| `*-Setup.exe`                                 | `setup`           | First installs via the channel installer                                     |
| `*-Portable.zip`                              | `portable`        | Portable first installs                                                      |
| `*-full.nupkg`                                | `full`            | Updates applied where no delta fit                                           |
| `*-delta.nupkg`                               | `delta`           | Updates applied via delta                                                    |
| `releases.win.json`, `releases.win-beta.json` | `feed`            | Update checks (the client fetches the feed of the newest release it can see) |
| `RELEASES`                                    | `releases-legacy` | Legacy Velopack feed, ignore                                                 |
| `ClaudeUsageTraySetup.exe`                    | `stub`            | Setup stub downloads for that release                                        |

`ring` is `win-beta` when the asset name contains `-win-beta-` or is `releases.win-beta.json`,
otherwise `win`. Since 0.7.x every stable release also carries a `win-beta` mirror
(`docs/superpowers/specs/2026-09-04-beta-release-ring-design.md`), so ring must be read from the
asset, never from the release's `prerelease` flag. `prerelease` is still recorded so beta
releases can be separated from stable releases with a mirror.

`full + delta` per version is the update count for that version. The counts are cumulative, so
the difference between two daily rows is that day's rate. GitHub keeps no time series, which is
the whole reason for storing snapshots.

The permanent `setup-stub` release is skipped. Its `ClaudeUsageTraySetup.exe` is copied onto each
version release, and only those copies are attributed to a release.

## Components

### `build/download-stats.ps1`

PowerShell 7 script, run locally or in CI. Requires `gh` to be authenticated.

- Parameters: `-Repo` (default `wus-technik/win_systray-claude-usage`), `-OutFile` (default
  `downloads.csv` in the current directory), `-Date` (default today, UTC, `yyyy-MM-dd`).
- Pages through `gh api repos/<repo>/releases --paginate`, skips `setup-stub`, and produces one
  row per asset with columns `date,tag,prerelease,ring,kind,asset,download_count`.
- Idempotent per day: existing rows for `-Date` are dropped before the new rows are appended, so a
  rerun replaces rather than duplicates. Rows for other dates are preserved untouched.
- Writes the header when the file does not exist. Rows are sorted by `date, tag, asset` so the
  file diff is stable.
- Fails loudly (non-zero exit) if `gh` fails; never writes a partial file.

### `.github/workflows/download-stats.yml`

- Triggers: `schedule` daily at 03:17 UTC and `workflow_dispatch`.
- `permissions: contents: write`. Uses the default `GITHUB_TOKEN`; `gh` reads it from `GH_TOKEN`.
- Steps: check out `main` (for the script), check out the `stats` branch into `stats/`
  (creating it as an orphan with an empty commit when it does not exist), run the script with
  `-OutFile stats/downloads.csv`, commit as `github-actions[bot]` and push only when the file
  changed.
- Every action pinned to a major whose `action.yml` declares `using: node24`, verified before
  pinning.
- Concurrency group `download-stats` so a manual run cannot race the schedule.

### `docs/download-stats.md`

The table above, the ring rule, the cumulative-snapshot note, and how to read the branch:

```powershell
git fetch origin stats && git show origin/stats:downloads.csv
```

### `README.md`

A short "Privacy" note: the app sends no telemetry and has no identifiers. The project's only
usage metric is GitHub's public download counts of release assets, snapshotted daily into the
`stats` branch.

## Out of scope

- Any in-app counter or crash reporting.
- Rendering charts or summaries. The CSV is the deliverable; anything on top is a later issue.
- Deduplicating repeated downloads by the same machine. Not possible without identifiers, and not
  wanted.

## Verification

- Run the script locally against the real API, inspect the CSV, run it a second time with the same
  date and confirm the row count is unchanged.
- After merge, trigger the workflow by hand and confirm one commit lands on `stats` with the file.
- No unit tests; the other `build/*.ps1` scripts have none either, and the script has no logic
  worth isolating beyond the filename classification, which the doc table makes reviewable.
