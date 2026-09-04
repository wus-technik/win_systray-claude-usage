# Local package build. GitHub Actions handles production releases and downloads
# previous release assets first so it can create delta packages.
# Usage: .\build\build-release.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

[xml]$csproj = Get-Content src/ClaudeUsageTray/ClaudeUsageTray.csproj
$version = $csproj.Project.PropertyGroup.Version
if (-not $version) { throw 'No <Version> in ClaudeUsageTray.csproj' }

# A prerelease version packs to the beta channel; a stable one packs twice, because beta users only
# ever read releases.win-beta.json and would stall without the mirror.
$ring = & (Join-Path $PSScriptRoot 'release-ring.ps1') -Version $version
$channels = @($ring.Channel) + @($ring.MirrorChannel | Where-Object { $_ })

# The version's changelog section becomes the release notes the update dialog shows. Missing section
# = hard stop: shipping a release nobody can read the changes for is the thing this prevents.
$notesFile = Join-Path $PWD 'artifacts/release-notes.md'
& (Join-Path $PSScriptRoot 'changelog-section.ps1') -Version $version -OutFile $notesFile | Out-Null

$publish = Join-Path $PWD 'artifacts\publish'
if (Test-Path $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
dotnet publish src/ClaudeUsageTray -c Release -r win-x64 --self-contained -o $publish
# vpk writes --packAuthors into the package's nuspec verbatim, so the ampersand has to arrive
# already XML-escaped: a bare "W&S Technik GmbH" makes vpk fail with an XmlException. The escape is
# undone when the manifest is read, so Windows still shows "W&S Technik GmbH".
# --icon is what puts the app icon on Setup.exe, the desktop shortcut and the Start menu entry.
# <ApplicationIcon> only covers the exe itself; vpk builds the shortcuts and needs its own copy.
foreach ($channel in $channels) {
    dnx vpk --version 1.2.0 pack --packId WusTechnik.ClaudeUsageTray --packTitle "Claude Usage Tray" --packAuthors "W&amp;S Technik GmbH" --packVersion $version --packDir $publish --mainExe ClaudeUsageTray.exe --releaseNotes $notesFile --icon src/ClaudeUsageTray/app.ico --channel $channel
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed for channel '$channel'" }
}

Write-Host "Release $version built in .\Releases for channel(s): $($channels -join ', ')"
