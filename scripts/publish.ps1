<#
.SYNOPSIS
Publishes the App and the service, self-contained, for x64 and Arm64 Windows (spec §13), into
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
The runtimes to publish for; win-x64 and win-arm64 by default. The installer needs both.
#>
param(
    [string]$Configuration = 'Release',
    [string[]]$Runtime = @('win-x64', 'win-arm64')
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
            -o (Join-Path $out "$rid\$($program.Folder)") --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($program.Project) ($rid)." }
    }
}

foreach ($rid in $Runtime) {
    foreach ($program in $programs) {
        $bytes = (Get-ChildItem (Join-Path $out "$rid\$($program.Folder)") -Recurse -File | Measure-Object Length -Sum).Sum
        '{0}\{1}: {2:N1} MB' -f $rid, $program.Folder, ($bytes / 1MB)
    }
}
