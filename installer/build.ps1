<#
.SYNOPSIS
Publishes PowerLedger and compiles its installer into installer\output.

.DESCRIPTION
Needs Inno Setup 7.1, or 6.3 and later (https://jrsoftware.org/isinfo.php); installer\get-inno-setup.ps1 installs the
pinned one. Uses -Iscc when given, else ISCC.exe on PATH, else Inno Setup 7's, then 6's, installed for this user or for
everyone.
The version comes from Directory.Build.props, so the installer and the programs always agree.

.PARAMETER Configuration
The configuration to publish; Release by default.

.PARAMETER Iscc
The ISCC.exe to compile with.

.PARAMETER SkipPublish
Compiles what artifacts\publish already holds instead of publishing again.

.PARAMETER TestVariants
Also compiles the builds the installer test needs into installer\output\test: the next patch version (an upgrade), one
that acts as if the .NET runtime were missing, and one whose runtime download fails.
#>
param(
    [string]$Configuration = 'Release',
    [string]$Iscc,
    [switch]$SkipPublish,
    [switch]$TestVariants
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $PSScriptRoot 'PowerLedger.iss'

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

if (-not $SkipPublish) { & (Join-Path $root 'scripts\publish.ps1') -Configuration $Configuration }

function Find-Iscc {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $bases = "$env:LOCALAPPDATA\Programs", $env:ProgramFiles, ${env:ProgramFiles(x86)} | Where-Object { $_ }
    foreach ($folder in 'Inno Setup 7', 'Inno Setup 6') {
        foreach ($base in $bases) {
            $candidate = Join-Path $base "$folder\ISCC.exe"
            if (Test-Path $candidate) { return $candidate }
        }
    }
    throw 'Inno Setup is not installed. Run installer\get-inno-setup.ps1, or get it from https://jrsoftware.org/isinfo.php, and run this again.'
}

if (-not $Iscc) { $Iscc = Find-Iscc }
elseif (-not (Test-Path $Iscc -PathType Leaf)) { throw "There is no ISCC.exe at $Iscc." }
"Compiling with $Iscc"

function Invoke-Iscc([string[]]$Options) {
    & $Iscc @Options $script
    if ($LASTEXITCODE -ne 0) { throw "The installer did not compile (ISCC $Options)." }
}

Invoke-Iscc "/DAppVersion=$version"
$made = @(Join-Path $PSScriptRoot "output\PowerLedger-$version-setup.exe")

if ($TestVariants) {
    $test = Join-Path $PSScriptRoot 'output\test'
    $current = [version]$version
    $next = '{0}.{1}.{2}' -f $current.Major, $current.Minor, ([Math]::Max($current.Build, 0) + 1)
    $missing = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/powerledger-test-missing.exe'   # a 404 on Microsoft's host
    $variants = [ordered]@{
        "PowerLedger-$next-setup"              = @("/DAppVersion=$next")   # the upgrade
        "PowerLedger-$version-noruntime-setup" = @("/DAppVersion=$version", '/DForceRuntimeDownload')
        "PowerLedger-$version-badurl-setup"    = @("/DAppVersion=$version", '/DForceRuntimeDownload', "/DRuntimeUrl=$missing")
    }
    foreach ($name in $variants.Keys) {
        Invoke-Iscc ($variants[$name] + "/O$test" + "/F$name")
        $made += Join-Path $test "$name.exe"
    }
}

foreach ($file in $made) { '{0}: {1:N1} MB' -f [IO.Path]::GetRelativePath($root, $file), ((Get-Item $file).Length / 1MB) }
