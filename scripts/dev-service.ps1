#Requires -RunAsAdministrator
<#
.SYNOPSIS
Installs, removes, starts or stops the PowerLedger service on this machine for development.

.DESCRIPTION
Run from an elevated PowerShell. "install" publishes the service to Program Files, registers it to start with
Windows as LocalSystem, and sets the recovery actions from spec §10: restart after 5 s, up to three times a day,
including after a failure exit. The installer (Plan E) replaces this script for real installs.
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet('install', 'uninstall', 'start', 'stop', 'status')]
    [string]$Action,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$name = 'PowerLedger'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:ProgramFiles 'PowerLedger\Service'

switch ($Action) {
    'install' {
        dotnet publish (Join-Path $root 'src\PowerLedger.Service') -c $Configuration -o $target --nologo
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
        $exe = Join-Path $target 'PowerLedger.Service.exe'
        sc.exe create $name binPath= "`"$exe`"" start= auto obj= LocalSystem DisplayName= 'PowerLedger'
        sc.exe description $name 'Records how much power this PC uses.'
        sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/5000
        sc.exe failureflag $name 1
        sc.exe start $name
    }
    'uninstall' {
        sc.exe stop $name
        Start-Sleep -Seconds 3
        sc.exe delete $name
    }
    'start' { sc.exe start $name }
    'stop' { sc.exe stop $name }
    'status' { sc.exe query $name }
}
