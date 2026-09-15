<#
.SYNOPSIS
Publishes the App and the service for win-x64, framework-dependent (spec §13), into artifacts\publish.

.DESCRIPTION
A win-x64 publish ships only Windows x64's native libraries, where a platform-neutral build carries eight
platforms' worth of QuestPDF and SQLite. The installer (installer\PowerLedger.iss) packs both folders.
#>
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$programs = @(
    @{ Project = 'src\PowerLedger.App'; Folder = 'App' },
    @{ Project = 'src\PowerLedger.Service'; Folder = 'Service' }
)
foreach ($program in $programs) {
    dotnet publish (Join-Path $root $program.Project) -c $Configuration -r win-x64 --self-contained false `
        -o (Join-Path $out $program.Folder) --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($program.Project)." }
}

foreach ($folder in Get-ChildItem $out -Directory) {
    $bytes = (Get-ChildItem $folder.FullName -Recurse -File | Measure-Object Length -Sum).Sum
    '{0}: {1:N1} MB' -f $folder.Name, ($bytes / 1MB)
}
