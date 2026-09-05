# Download Stats Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Snapshot GitHub's public per-asset release download counters into a CSV on an orphan `stats` branch once a day, so install and update fetch rates per release and ring become visible without any telemetry.

**Architecture:** A PowerShell 7 script under `build/` reads the releases API through `gh`, classifies each asset by filename into `kind` and `ring`, and merges one day's rows into `downloads.csv` idempotently. A scheduled GitHub Actions workflow runs the script against a `stats` worktree and pushes only when the file changed. A docs page records what the numbers mean and a README note states the privacy position. Nothing under `src/` changes.

**Tech Stack:** PowerShell 7 (`pwsh`), GitHub CLI (`gh api --paginate --slurp`, needs gh ≥ 2.40), GitHub Actions on `ubuntu-latest`, `actions/checkout@v7` (node24).

**Spec:** `docs/superpowers/specs/2026-09-05-download-stats-design.md`

## Global Constraints

- The app sends nothing. No file under `src/` is touched by any task.
- Only data source: GitHub's public `download_count`. Skip `setup-stub` and draft releases.
- CSV columns, exactly and in this order: `date,tag,prerelease,ring,kind,asset,asset_id,download_count`.
- `kind` values: `setup`, `portable`, `full`, `delta`, `feed`, `releases-legacy`, `stub`, `other`.
- `ring` values: `win`, `win-beta`, `none`. `none` for `releases-legacy`, `stub`, `other`.
- One snapshot per UTC day; rerunning for the same `-Date` replaces that day's rows.
- Actions follow major tags whose `action.yml` declares `using: node24` (`actions/checkout@v7`, already verified).
- Commit messages end with `Refs #22` and the `Claude-Session:` trailer used on this branch; no AI co-author trailer.
- Everything produced is English.

---

### Task 1: The collector script

**Files:**
- Create: `build/download-stats.ps1`

**Interfaces:**
- Consumes: `gh` on PATH, authenticated (locally via `gh auth login`, in CI via `GH_TOKEN`).
- Produces: `build/download-stats.ps1 [-Repo <owner/name>] [-OutFile <path>] [-Date yyyy-MM-dd]`. Exit 0 and a merged CSV at `-OutFile` on success, non-zero with no partial file on failure. Task 2 calls it with `-Repo $env:GITHUB_REPOSITORY -OutFile stats/downloads.csv` and optionally `-Date`.

- [ ] **Step 1: Write the script**

```powershell
# Snapshots GitHub's public per-asset release download counters into a CSV, one row per asset
# for one UTC day. Rerunning for the same day replaces that day's rows; other days are untouched.
#
# Usage:
#   .\build\download-stats.ps1                                   # today, .\downloads.csv
#   .\build\download-stats.ps1 -OutFile stats\downloads.csv       # CI layout
#   .\build\download-stats.ps1 -Date 2026-09-04                   # backfill a missed day
#
# What the numbers mean (and do not mean) is documented in docs/download-stats.md.
# Skips the permanent setup-stub release and draft releases, so the file only ever holds what an
# anonymous visitor to the releases page can see.
[CmdletBinding()]
param(
    [string]$Repo = 'wus-technik/win_systray-claude-usage',
    [string]$OutFile = 'downloads.csv',
    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')]
    [string]$Date = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
)
$ErrorActionPreference = 'Stop'

# Filename-based classification. The stub is checked first: it ends in "Setup.exe" but has no
# channel prefix, so it must not fall into the setup bucket.
function Get-AssetKind([string]$Name) {
    switch -Regex ($Name) {
        '^ClaudeUsageTraySetup\.exe$' { return 'stub' }
        '-Setup\.exe$'                { return 'setup' }
        '-Portable\.zip$'             { return 'portable' }
        '-full\.nupkg$'               { return 'full' }
        '-delta\.nupkg$'              { return 'delta' }
        '^releases\.[^.]+\.json$'     { return 'feed' }
        '^RELEASES$'                  { return 'releases-legacy' }
        default                       { return 'other' }
    }
}

# Ring comes from the asset name, never from the release's prerelease flag: stable releases carry
# a win-beta mirror. Ring-neutral assets get 'none' so they never inflate the stable ring.
function Get-AssetRing([string]$Name, [string]$Kind) {
    if ($Kind -in @('releases-legacy', 'stub', 'other')) { return 'none' }
    if ($Name -like '*-win-beta-*' -or $Name -eq 'releases.win-beta.json') { return 'win-beta' }
    return 'win'
}

$json = gh api "repos/$Repo/releases" --paginate --slurp
if ($LASTEXITCODE -ne 0) { throw "gh api failed for repos/$Repo/releases (exit $LASTEXITCODE)" }

# --slurp returns one array per page; flatten one level.
$releases = foreach ($page in ($json | ConvertFrom-Json)) { $page }

$rows = foreach ($release in $releases) {
    if ($release.draft -or $release.tag_name -eq 'setup-stub') { continue }
    foreach ($asset in $release.assets) {
        $kind = Get-AssetKind $asset.name
        [pscustomobject]@{
            date           = $Date
            tag            = $release.tag_name
            prerelease     = [bool]$release.prerelease
            ring           = Get-AssetRing $asset.name $kind
            kind           = $kind
            asset          = $asset.name
            asset_id       = $asset.id
            download_count = $asset.download_count
        }
    }
}
if (-not $rows) { throw "No release assets found for $Repo; refusing to write an empty snapshot." }

$existing = @()
if (Test-Path -LiteralPath $OutFile) {
    $existing = @(Import-Csv -LiteralPath $OutFile | Where-Object { $_.date -ne $Date })
}

$all = @($existing) + @($rows) | Sort-Object date, tag, asset

# Write with LF endings regardless of host OS so the stats branch diff stays stable, and via a
# temp file so a failure cannot leave a truncated CSV behind.
$text = (($all | ConvertTo-Csv -NoTypeInformation -UseQuotes AsNeeded) -join "`n") + "`n"
$tmp = "$OutFile.tmp"
[System.IO.File]::WriteAllText($tmp, $text, [System.Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $tmp -Destination $OutFile -Force

Write-Host "Snapshot $Date for $Repo: $(@($rows).Count) asset rows across $(@($rows | Select-Object -ExpandProperty tag -Unique).Count) releases -> $OutFile ($(@($all).Count) rows total)"
```

- [ ] **Step 2: Run it against the real API into the scratchpad**

Run (from the worktree root, replacing `<scratch>` with the session scratchpad directory):

```powershell
pwsh -NoProfile -File .\build\download-stats.ps1 -OutFile <scratch>\downloads.csv
Get-Content <scratch>\downloads.csv | Select-Object -First 8
```

Expected: exit 0, a summary line, and a header `"date","tag","prerelease","ring","kind","asset","asset_id","download_count"` or the unquoted equivalent, followed by rows like `2026-09-05,v0.7.2,False,win-beta,delta,WusTechnik.ClaudeUsageTray-0.7.2-win-beta-delta.nupkg,<id>,1`. No row with tag `setup-stub`. `ClaudeUsageTraySetup.exe` rows have `kind=stub` and `ring=none`. `RELEASES` has `kind=releases-legacy`, `ring=none`.

- [ ] **Step 3: Verify idempotence and backfill**

```powershell
$n1 = (Import-Csv <scratch>\downloads.csv).Count
pwsh -NoProfile -File .\build\download-stats.ps1 -OutFile <scratch>\downloads.csv
$n2 = (Import-Csv <scratch>\downloads.csv).Count
pwsh -NoProfile -File .\build\download-stats.ps1 -OutFile <scratch>\downloads.csv -Date 2026-09-04
$n3 = (Import-Csv <scratch>\downloads.csv).Count
"$n1 $n2 $n3"; (Import-Csv <scratch>\downloads.csv | Group-Object date).Name
```

Expected: `n1 == n2` and `n3 == 2 * n1`; two dates listed. Also confirm LF endings: `(Get-Content -Raw <scratch>\downloads.csv) -match "`r"` prints `False`.

- [ ] **Step 4: Verify the failure path leaves no partial file**

```powershell
pwsh -NoProfile -File .\build\download-stats.ps1 -Repo wus-technik/does-not-exist -OutFile <scratch>\nope.csv; $LASTEXITCODE
Test-Path <scratch>\nope.csv
```

Expected: a thrown error, non-zero exit code, `False`.

- [ ] **Step 5: Commit**

```bash
git add build/download-stats.ps1
git commit -m "feat: add the download-stats collector script" -m "Reads public release asset download counters via gh, classifies each asset into kind and ring by filename, and merges one UTC day's rows into a CSV idempotently. Skips setup-stub and drafts." -m "Refs #22" -m "Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 2: The scheduled workflow

**Files:**
- Create: `.github/workflows/download-stats.yml`

**Interfaces:**
- Consumes: `build/download-stats.ps1` from Task 1 with `-Repo`, `-OutFile`, `-Date`.
- Produces: branch `stats` on origin containing `downloads.csv`, one commit per day the file changed.

- [ ] **Step 1: Write the workflow**

```yaml
name: Download stats

# Daily snapshot of GitHub's public per-asset release download counters into downloads.csv on the
# orphan `stats` branch. No app involvement, no telemetry: see docs/download-stats.md.
#
# Cron on GitHub is best-effort and a run can be delayed or dropped under load. A missed day can be
# backfilled by dispatching the workflow with the `date` input.

on:
  schedule:
    - cron: "17 3 * * *"
  workflow_dispatch:
    inputs:
      date:
        description: "Snapshot date, yyyy-MM-dd UTC. Empty means today. Use to backfill a missed day."
        required: false
        default: ""

permissions:
  contents: write

concurrency:
  group: download-stats
  cancel-in-progress: false

jobs:
  snapshot:
    runs-on: ubuntu-latest

    steps:
      - name: Checkout
        uses: actions/checkout@v7

      - name: Check out or create the stats branch
        shell: bash
        run: |
          set -euo pipefail
          if git ls-remote --exit-code --heads origin stats >/dev/null; then
            git fetch origin stats
            git worktree add stats origin/stats
          else
            # Orphan worktree: an empty tree, so the first commit holds only downloads.csv.
            git worktree add --orphan stats
          fi

      - name: Snapshot download counters
        shell: pwsh
        env:
          GH_TOKEN: ${{ github.token }}
          SNAPSHOT_DATE: ${{ inputs.date }}
        run: |
          $params = @{ Repo = $env:GITHUB_REPOSITORY; OutFile = 'stats/downloads.csv' }
          if ($env:SNAPSHOT_DATE) { $params.Date = $env:SNAPSHOT_DATE }
          ./build/download-stats.ps1 @params

      - name: Commit and push when changed
        shell: bash
        run: |
          set -euo pipefail
          cd stats
          git add downloads.csv
          if git diff --cached --quiet; then
            echo "No change in downloads.csv."
            exit 0
          fi
          git -c user.name='github-actions[bot]' \
              -c user.email='41898282+github-actions[bot]@users.noreply.github.com' \
              commit -m "stats: snapshot $(date -u +%F)"
          git push origin HEAD:refs/heads/stats
```

- [ ] **Step 2: Check the workflow parses and the action is on node24**

```bash
gh api "repos/actions/checkout/contents/action.yml?ref=v7" --jq .content | base64 -d | grep "using:"
pwsh -NoProfile -Command "Get-Content .github/workflows/download-stats.yml | Select-String -Pattern '^\s*uses:'"
```

Expected: `using: node24`, and the only `uses:` line is `actions/checkout@v7`. YAML validity is confirmed by GitHub when the branch is pushed: `gh workflow list` on the PR branch must list "Download stats" without a parse error (workflows with `workflow_dispatch` register from non-default branches once pushed).

- [ ] **Step 3: Dry-run the branch bootstrap locally**

The orphan-worktree step is the one piece that cannot be tested by reading. Reproduce it in a throwaway clone in the scratchpad:

```bash
cd <scratch> && rm -rf wt-test && git init -q wt-test && cd wt-test
git commit -q --allow-empty -m init
git worktree add --orphan stats
cd stats && git status --short --branch && ls -A
```

Expected: `## No commits yet on stats` and an empty listing (only `.git` file). Delete `wt-test` afterwards.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/download-stats.yml
git commit -m "ci: snapshot release download counters daily into the stats branch" -m "Runs build/download-stats.ps1 on a schedule and on dispatch (with a date input for backfills), against a worktree of the orphan stats branch, and pushes only when downloads.csv changed." -m "Refs #22" -m "Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 3: Documentation and the README privacy note

**Files:**
- Create: `docs/download-stats.md`
- Modify: `README.md` (add a `## Privacy` section between `## Settings` and `## Development`, i.e. before the line `## Development` at roughly line 242)

**Interfaces:**
- Consumes: column and value names from Task 1, the branch name and dispatch input from Task 2.
- Produces: nothing programmatic.

- [ ] **Step 1: Write `docs/download-stats.md`**

```markdown
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
```

- [ ] **Step 2: Add the README section**

Insert before `## Development`:

```markdown
## Privacy

The app sends no telemetry and has no install or machine identifier. Its only network traffic is
the Claude usage API, the Claude status page, and the Velopack update feed on GitHub Releases.

The project's only usage metric is GitHub's public download counter on release assets. A
scheduled workflow snapshots those counters once a day into the `stats` branch, which adds a
history of aggregate public numbers and nothing about any user or machine. See
`docs/download-stats.md` for what the numbers mean.
```

- [ ] **Step 3: Check both files render sanely**

```bash
grep -n "^## " README.md
grep -c "|" docs/download-stats.md
```

Expected: `## Privacy` listed directly before `## Development`; the table lines are present.

- [ ] **Step 4: Commit**

```bash
git add docs/download-stats.md README.md
git commit -m "docs: explain the download statistics and state the privacy position" -m "Refs #22" -m "Claude-Session: https://claude.ai/code/session_01GoQxf4Dj7bvrrejE9weHGL"
```

---

### Task 4: Finish the branch

- [ ] **Step 1: Run the test suite to prove nothing under `src/` moved**

```bash
git diff --stat origin/main -- src
dotnet test --nologo -v q
```

Expected: empty diff for `src/`; all tests pass.

- [ ] **Step 2: Hand over**

Use superpowers:finishing-a-development-branch. The PR body must mention that after merge the workflow needs one manual `gh workflow run download-stats.yml` to create the `stats` branch, and end with `Closes #22` plus the required generated-with footer.
