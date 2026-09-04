# Which release ring a version ships to. One definition, used by build-release.ps1 and by
# .github/workflows/release.yml, so a local build and CI can never disagree about it.
#
# A SemVer prerelease suffix makes it a beta: 0.7.2-beta.1 -> channel win-beta, published as a GitHub
# pre-release. Anything else is stable -> channel win, and is *also* mirrored into win-beta, because a
# win-beta client never reads releases.win.json and would otherwise stall after every stable release.
# See docs/superpowers/specs/2026-09-04-beta-release-ring-design.md.
#
# Usage:
#   $ring = .\build\release-ring.ps1 -Version 0.7.2-beta.1
#   $ring.Channel        # win-beta
#   $ring.IsBeta         # True
#   $ring.MirrorChannel  # $null (stable releases return 'win-beta')
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'

# Rejected rather than guessed at: a version vpk and Velopack would read differently is exactly the
# thing that sends a beta to every user.
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' is not a SemVer 2 version. Betas look like 0.7.2-beta.1 (dot-numbered, so beta.10 sorts above beta.9)."
}

$isBeta = $Version -match '^[0-9]+\.[0-9]+\.[0-9]+-'

[pscustomobject]@{
    Version       = $Version
    IsBeta        = $isBeta
    Channel       = if ($isBeta) { 'win-beta' } else { 'win' }
    MirrorChannel = if ($isBeta) { $null } else { 'win-beta' }
}
