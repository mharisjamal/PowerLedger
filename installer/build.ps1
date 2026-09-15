<#
.SYNOPSIS
Publishes PowerLedger and compiles its installer into installer\output.

.DESCRIPTION
Needs Inno Setup 6.3 or later (https://jrsoftware.org/isinfo.php), found on PATH or in its default folder.
The version comes from Directory.Build.props, so the installer and the programs always agree.
#>
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

& (Join-Path $root 'scripts\publish.ps1') -Configuration $Configuration

$command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$iscc = if ($command) { $command.Source } else { Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
if (-not (Test-Path $iscc)) { throw 'Inno Setup 6 is not installed. Get it from https://jrsoftware.org/isinfo.php and run this again.' }

& $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot 'PowerLedger.iss')
if ($LASTEXITCODE -ne 0) { throw 'The installer did not compile.' }
Get-ChildItem (Join-Path $PSScriptRoot 'output') -Filter '*.exe' | ForEach-Object { '{0}: {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
