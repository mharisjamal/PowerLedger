#Requires -Version 7
<#
.SYNOPSIS
Makes every installed copy update to a version: sets MIN_APP_VERSION in server\wrangler.toml to it and commits that.

.DESCRIPTION
The owner's rule (2026-10-15): every published release is forced, so release.ps1 runs this after a release that isn't a
draft, and it is run by hand when a draft is published on GitHub. The version defaults to the one in
Directory.Build.props. Nothing is pushed or deployed: push main, then deploy the Worker
(cd server; npx wrangler deploy) for older copies to start updating.

.EXAMPLE
pwsh scripts/force-update.ps1 -Version 0.11.0
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "'$Version' isn't a version like 0.11.0." }

$toml = Join-Path $root 'server\wrangler.toml'
$text = [IO.File]::ReadAllText($toml)
$pattern = '(?m)^MIN_APP_VERSION = "([^"]*)"'
$match = [regex]::Match($text, $pattern)
if (-not $match.Success) { throw "No MIN_APP_VERSION line in $toml." }
if ($match.Groups[1].Value -eq $Version) { "MIN_APP_VERSION is already $Version."; return }
if ([version]$match.Groups[1].Value -gt [version]$Version) { throw "MIN_APP_VERSION is $($match.Groups[1].Value), newer than $Version." }

[IO.File]::WriteAllText($toml, [regex]::Replace($text, $pattern, "MIN_APP_VERSION = `"$Version`""))
git -C $root add -- server/wrangler.toml
git -C $root commit --quiet -m "Every copy updates to ${Version}: MIN_APP_VERSION raised" -- server/wrangler.toml
if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
"MIN_APP_VERSION is now $Version and committed. Push main, then deploy: cd server; npx wrangler deploy"
