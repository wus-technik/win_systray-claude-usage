# Download and update statistics

The app sends no telemetry and carries no identifiers. The only usage metric this project keeps is
GitHub's public per-asset `download_count` on releases, which anyone can read from the releases
API. A scheduled workflow (`.github/workflows/download-stats.yml`) snapshots those counters once a
day into `downloads.csv` on the orphan `stats` branch. Design:
`docs/superpowers/specs/2026-09-05-download-stats-design.md`.

## Reading the data

```powershell
git fetch origin stats
git show origin/stats:downloads.csv | ConvertFrom-Csv | Where-Object tag -eq v0.7.2 | Format-Table
```

Columns: `date,tag,prerelease,ring,kind,asset,asset_id,download_count`.

- `date` is the UTC day of the snapshot. One snapshot per day; a rerun for the same day replaces
  that day's rows. A missed day can be backfilled by dispatching the workflow with its `date`
  input, which labels the rows with the day they stand in for.
- `download_count` is cumulative. The difference between two days is that day's fetches.
- `asset_id` changes when an asset is deleted and re-uploaded, which also resets its counter.

## What each row approximates

Treat every number as **fetches**. GitHub documents neither what counts as a download nor CDN
effects, so none of this is installs, users, or successful updates.

| `kind`            | Matches                                       | `ring`    | Fetches approximate                                                            |
|-------------------|-----------------------------------------------|-----------|--------------------------------------------------------------------------------|
| `setup`           | `*-Setup.exe`                                 | from name | First installs via the channel installer                                       |
| `portable`        | `*-Portable.zip`                              | from name | Portable first installs                                                        |
| `full`            | `*-full.nupkg`                                | from name | Update attempts where no delta fit, plus the release CI baseline download      |
| `delta`           | `*-delta.nupkg`                               | from name | Update attempts via delta, plus the release CI baseline download               |
| `feed`            | `releases.win.json`, `releases.win-beta.json` | from name | Feed fetches. One client check fetches the feed of every release in its window |
| `releases-legacy` | `RELEASES`                                    | `none`    | Legacy Velopack feed, ignore                                                   |
| `stub`            | `ClaudeUsageTraySetup.exe`                    | `none`    | Setup stub downloads for that release                                          |
| `other`           | anything else                                 | `none`    | Unknown; kept so a new asset type is visible                                   |

**Ring.** `win-beta` when the asset name contains `-win-beta-` or is `releases.win-beta.json`,
`win` for every other channel asset, `none` for ring-neutral assets. Every stable release since
0.7.x also carries a `win-beta` mirror, so the ring is read from the asset, never from the
release's `prerelease` flag. `prerelease` is recorded so betas can be separated from stable
releases with a mirror.

**Feeds are not update checks.** Velopack lists the newest page of releases and merges the
`releases.<channel>.json` of each one on every check, so one check increments the feed counter on
several releases. The feed count of the newest release a ring can see is the closest proxy for
checks since that release. Summing feed counts across releases overcounts.

**Packages are not updates applied.** `full + delta` for a version counts download attempts,
including retries, staged-but-never-applied updates, manual downloads, and the one baseline
download `release.yml` makes of the previous release when it builds a delta. Expect it to sit a
little above the true number of installs that moved.

**Skipped.** The permanent `setup-stub` release (its stub is copied onto every version release and
counted there) and draft releases.
