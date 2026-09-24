<#
.SYNOPSIS
Publishes the App and the service, self-contained, for x64, Arm64 and 32-bit x86 Windows (spec §13), into
artifacts\publish\<runtime>.

.DESCRIPTION
Self-contained, so each program carries the .NET 10 runtime: the installer downloads nothing, the PC needs no .NET
of its own, and Arm64 Windows runs a native Arm64 build instead of emulating x64. A publish for one runtime also ships
only that platform's native libraries, where a platform-neutral build carries eight platforms' worth of QuestPDF and
SQLite. The installer (installer\PowerLedger.iss) packs every folder and installs the build that matches the PC.
artifacts\publish is emptied first, so an installer is never built from two different publishes.

.PARAMETER Configuration
The configuration to publish; Release by default.

.PARAMETER Runtime
The runtimes to publish for; win-x64, win-arm64 and win-x86 by default. The installer needs all three.

.PARAMETER GoogleClientSecret
Google's Desktop OAuth client secret (households design §7), built into the App's own assembly metadata
(PowerLedgerGoogleClientSecret) so it never sits in source, since the repo is public. Empty by default: Google sign-in
is then unavailable in the build, Microsoft sign-in is not affected. installer\build.ps1 is what reads it from the
machine building; this script never prints it.
#>
param(
    [string]$Configuration = 'Release',
    [string[]]$Runtime = @('win-x64', 'win-arm64', 'win-x86'),
    [string]$GoogleClientSecret = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$programs = @(
    @{ Project = 'src\PowerLedger.App'; Folder = 'App' },
    @{ Project = 'src\PowerLedger.Service'; Folder = 'Service' }
)
foreach ($rid in $Runtime) {
    foreach ($program in $programs) {
        dotnet publish (Join-Path $root $program.Project) -c $Configuration -r $rid --self-contained true `
            -o (Join-Path $out "$rid\$($program.Folder)") --nologo "-p:PowerLedgerGoogleClientSecret=$GoogleClientSecret"
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($program.Project) ($rid)." }
    }
}

foreach ($rid in $Runtime) {
    foreach ($program in $programs) {
        $bytes = (Get-ChildItem (Join-Path $out "$rid\$($program.Folder)") -Recurse -File | Measure-Object Length -Sum).Sum
        '{0}\{1}: {2:N1} MB' -f $rid, $program.Folder, ($bytes / 1MB)
    }
}
