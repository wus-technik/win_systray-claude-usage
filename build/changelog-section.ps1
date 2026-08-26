# Extracts one version's section from CHANGELOG.md, for `vpk pack --releaseNotes`.
#
# The heading may carry a date and link brackets, so "## [0.7.0] - 2026-08-26", "## 0.7.0" and
# "## [0.7.0]" all match version 0.7.0. Everything up to the next "## " heading is the section.
#
# Usage:
#   .\build\changelog-section.ps1 -Version 0.7.1                  # writes the section to stdout
#   .\build\changelog-section.ps1 -Version 0.7.1 -OutFile notes.md # and to a file
#
# Exits non-zero when the version has no section, so a release cannot silently ship without notes.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutFile,
    [string]$ChangelogPath = (Join-Path $PSScriptRoot '..\CHANGELOG.md')
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ChangelogPath)) {
    throw "No changelog at $ChangelogPath"
}

$lines = Get-Content -LiteralPath $ChangelogPath
$escaped = [regex]::Escape($Version)
# Anchored on the version itself so 0.7.1 cannot match 0.7.10.
$headingPattern = "^##\s+\[?$escaped\]?(\s|$|\s*[-–—])"

$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $headingPattern) { $start = $i; break }
}
if ($start -lt 0) {
    throw "CHANGELOG.md has no '## $Version' section. Add one before releasing $Version."
}

$body = New-Object System.Collections.Generic.List[string]
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s') { break }
    $body.Add($lines[$i])
}

$section = ($body -join "`n").Trim()
if (-not $section) {
    throw "The '## $Version' section in CHANGELOG.md is empty."
}

if ($OutFile) {
    $directory = Split-Path -Parent $OutFile
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Set-Content -LiteralPath $OutFile -Value $section -Encoding utf8NoBOM
}
$section
