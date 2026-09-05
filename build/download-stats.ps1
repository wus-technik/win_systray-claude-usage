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

Write-Host "Snapshot $Date for ${Repo}: $(@($rows).Count) asset rows across $(@($rows | Select-Object -ExpandProperty tag -Unique).Count) releases -> $OutFile ($(@($all).Count) rows total)"
