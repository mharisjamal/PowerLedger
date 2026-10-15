<#
.SYNOPSIS
Plan AE's busy-loop check: on a PC with nothing else running, prints what the energy by app gives a busy loop against the
extra watts it makes the processor draw, run after run.

.DESCRIPTION
Builds the Service tests (Release) and runs AppWatchHardwareTests.A_busy_loop_is_given_about_the_extra_watts_it_makes_the_processor_draw
as many times as asked. Each run takes about five and a half minutes: 75 seconds with nothing added, for the resting draw
to be learned, then eight rounds of 15 seconds with a copy of cmd.exe spinning one thread and 15 seconds without. A run
passes when the loop is given its extra watts within 15%. It reads the processor's own energy meter and needs no
administrator; it touches no installed PowerLedger and writes only to a temporary database.

.EXAMPLE
./scripts/busy-loop-check.ps1
./scripts/busy-loop-check.ps1 -Runs 5
#>
param(
    [int]$Runs = 3
)

$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'tests\PowerLedger.Service.Tests'
dotnet build $project -c Release -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The Service tests did not build." }

$passed = 0
for ($run = 1; $run -le $Runs; $run++) {
    $lines = dotnet test $project -c Release --no-build --nologo -v q `
        --filter 'FullyQualifiedName~AppWatchHardwareTests.A_busy_loop_is_given_about_the_extra_watts' `
        --logger 'console;verbosity=detailed' 2>&1
    $verdict = if ($LASTEXITCODE -eq 0) { 'within 15%' } else { 'NOT within 15%' }
    if ($LASTEXITCODE -eq 0) { $passed++ }
    $result = $lines | Select-String -Pattern 'W extra; the loop was given' | Select-Object -First 1
    $text = if ($result) { $result.Line.Trim() } else { 'no result printed: ' + (($lines | Select-String 'Error Message' -Context 0, 3 | Select-Object -First 1) -join ' ') }
    Write-Output "Run ${run}: $text [$verdict]"
}
Write-Output "$passed of $Runs runs within 15%."
