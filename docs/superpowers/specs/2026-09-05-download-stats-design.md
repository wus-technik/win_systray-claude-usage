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
  which anyone can read from the releases API. The only thing this project adds is a daily
  history of those public counters, which yields aggregate rates. It adds no identifiers.
- **Nothing under `src/` changes.**

## What GitHub asset counts can and cannot tell us

Every file Velopack touches is a release asset, and GitHub keeps a per-asset `download_count`.
That counter is opaque: GitHub documents neither what a "download" is (full transfer, range
request, redirect follow) nor CDN effects. Treat every number as **fetches**, never as installs,
users, or successful updates.

| Asset name pattern                            | `kind`            | `ring`    | Fetches approximate                                                                |
|-----------------------------------------------|-------------------|-----------|------------------------------------------------------------------------------------|
| `*-Setup.exe`                                 | `setup`           | from name | First installs via the channel installer                                           |
| `*-Portable.zip`                              | `portable`        | from name | Portable first installs (vpk always produces one; v0.7.2 ships both rings' zips)   |
| `*-full.nupkg`                                | `full`            | from name | Update attempts where no delta fit, plus release CI baseline downloads             |
| `*-delta.nupkg`                               | `delta`           | from name | Update attempts via delta, plus release CI baseline downloads                      |
| `releases.win.json`, `releases.win-beta.json` | `feed`            | from name | Feed fetches. One client check fetches the feed of **every** release in its window |
| `RELEASES`                                    | `releases-legacy` | `none`    | Legacy Velopack feed, ignore                                                       |
| `ClaudeUsageTraySetup.exe`                    | `stub`            | `none`    | Setup stub downloads for that release                                              |
| anything else                                 | `other`           | `none`    | Unknown, keep the row so a new asset type is visible                               |

**Ring rule.** `ring` is `win-beta` when the asset name contains `-win-beta-` or is
`releases.win-beta.json`, `win` for every other channel asset, and `none` for ring-neutral
assets (stub, legacy `RELEASES`, unknown). Since 0.7.x every stable release also carries a
`win-beta` mirror (`docs/superpowers/specs/2026-09-04-beta-release-ring-design.md`), so ring must
be read from the asset, never from the release's `prerelease` flag. `prerelease` is still recorded
so beta releases can be separated from stable releases with a mirror.

**Feeds are not "update checks".** Per the ring design doc, Velopack's `GithubSource` lists the
newest page of releases and then merges `releases.<channel>.json` from each one, so a single
check increments the feed counter on several releases. The feed count of the *newest* release a
ring can see is the closest proxy for checks since that release; summing feed counts across
releases overcounts.

**Packages are not "updates applied".** `full + delta` per version counts download attempts:
retries, staged-but-never-applied updates, manual downloads, and the one baseline download
`release.yml` makes of the previous release when it builds a delta. Read the sum as
"update fetches" and expect it to be a little above the true number of installs that moved.

**Cumulative.** Counts only grow (unless an asset is deleted and re-uploaded, which resets it).
The difference between two daily rows is that day's rate. GitHub keeps no time series, which is
the whole reason for storing snapshots. `asset_id` is recorded so a re-upload shows up as a new id
instead of a silent reset.

**Skipped releases.** The permanent `setup-stub` release is skipped: its `ClaudeUsageTraySetup.exe`
is copied onto each version release, and only those copies are attributed to a release. Draft
releases are skipped too, so the CSV only ever contains what anonymous users can also see.

## Components

### `build/download-stats.ps1`

PowerShell 7 script, run locally or in CI. Requires `gh` to be authenticated.

- Parameters: `-Repo` (default `wus-technik/win_systray-claude-usage`), `-OutFile` (default
  `downloads.csv` in the current directory), `-Date` (default today, UTC, `yyyy-MM-dd`; passing a
  past date lets a missed scheduled run be backfilled by hand, labelled with the day it stands in
  for).
- Pages through `gh api repos/<repo>/releases --paginate`, skips `setup-stub` and any release with
  `draft == true`, and produces one row per asset with columns
  `date,tag,prerelease,ring,kind,asset,asset_id,download_count`.
- Idempotent per day: existing rows for `-Date` are dropped before the new rows are appended, so a
  rerun replaces rather than duplicates. One snapshot per UTC day is the model; a later rerun on
  the same day overwrites that day's rows, and the docs say so. Rows for other dates are preserved
  untouched.
- Writes the header when the file does not exist. Rows are sorted by `date, tag, asset` so the
  file diff is stable.
- Fails loudly (non-zero exit) if `gh` fails; never writes a partial file.

### `.github/workflows/download-stats.yml`

- Triggers: `schedule` daily at 03:17 UTC and `workflow_dispatch` with an optional `date` input
  passed through to `-Date`. GitHub runs cron best-effort and may delay or drop a run under load,
  so the CSV is a best-effort daily series; the `date` input is the manual backfill.
- `permissions: contents: write`. Uses the default `GITHUB_TOKEN`; `gh` reads it from `GH_TOKEN`.
  The token is only needed for the push; the releases read is of public data and the script drops
  drafts so the token's visibility never leaks into the CSV.
- Steps: check out `main` (for the script). Then `git ls-remote --heads origin stats`; if the
  branch exists, fetch it and `git worktree add stats/ origin/stats`; if not, create it in
  `stats/` with `git switch --orphan stats` on a fresh empty directory (no inherited files) so the
  first commit contains only `downloads.csv`. Run the script with `-OutFile stats/downloads.csv`,
  commit as `github-actions[bot]` and push `refs/heads/stats` only when the file changed.
- Actions follow the major tag whose `action.yml` declares `using: node24` (`actions/checkout@v7`,
  matching `ci.yml`), verified before pinning. Major tags are mutable by design; that is the
  project convention, not a SHA pin.
- Concurrency group `download-stats` so a manual run cannot race the schedule. A snapshot taken
  while `release.yml` is still uploading may see a release with some assets missing; since the
  counters are cumulative the missing assets simply appear in the next day's rows, so no guard is
  needed.

### `docs/download-stats.md`

The table above, the ring rule, the feed and package caveats, the one-snapshot-per-day overwrite
policy, and how to read the branch:

```powershell
git fetch origin stats && git show origin/stats:downloads.csv
```

### `README.md`

A short "Privacy" note: the app sends no telemetry and has no identifiers. The project's only
usage metric is GitHub's public download counters of release assets, snapshotted daily into the
`stats` branch. Say that the snapshots add historical aggregates on top of public counters, and
nothing about any user or machine.

## Out of scope

- Any in-app counter or crash reporting.
- Rendering charts or summaries. The CSV is the deliverable; anything on top is a later issue.
- Deduplicating repeated downloads by the same machine. Not possible without identifiers, and not
  wanted.

## Verification

- Run the script locally against the real API, inspect the CSV, run it a second time with the same
  date and confirm the row count is unchanged. Run it once more with `-Date` set to yesterday and
  confirm both days are present.
- After merge, trigger the workflow by hand and confirm one commit lands on `stats` with the file.
- No unit tests; the other `build/*.ps1` scripts have none either, and the script has no logic
  worth isolating beyond the filename classification, which the doc table makes reviewable.
