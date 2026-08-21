# Local package build. GitHub Actions handles production releases and downloads
# previous release assets first so it can create delta packages.
# Usage: .\build\build-release.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

[xml]$csproj = Get-Content src/ClaudeUsageTray/ClaudeUsageTray.csproj
$version = $csproj.Project.PropertyGroup.Version
if (-not $version) { throw 'No <Version> in ClaudeUsageTray.csproj' }

$publish = Join-Path $PWD 'artifacts\publish'
if (Test-Path $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
dotnet publish src/ClaudeUsageTray -c Release -r win-x64 --self-contained -o $publish
# vpk writes --packAuthors into the package's nuspec verbatim, so the ampersand has to arrive
# already XML-escaped: a bare "W&S Technik GmbH" makes vpk fail with an XmlException. The escape is
# undone when the manifest is read, so Windows still shows "W&S Technik GmbH".
dnx vpk --version 1.2.0 pack --packId WusTechnik.ClaudeUsageTray --packTitle "Claude Usage Tray" --packAuthors "W&amp;S Technik GmbH" --packVersion $version --packDir $publish --mainExe ClaudeUsageTray.exe

Write-Host "Release $version built in .\Releases"
