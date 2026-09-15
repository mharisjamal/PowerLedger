<#
.SYNOPSIS
Publishes PowerLedger and compiles its installer into installer\output.

.DESCRIPTION
Needs Inno Setup 7.1 or later (https://jrsoftware.org/isinfo.php); installer\get-inno-setup.ps1 installs the pinned one.
Inno Setup 6's 32-bit compiler can't use the 256 MB compression dictionary the script sets. Uses -Iscc when given, else
ISCC.exe on PATH, else Inno Setup 7's, installed for this user or for everyone.
The version comes from Directory.Build.props, so the installer and the programs always agree.
The installer holds the x64 and the Arm64 build, each with its own .NET runtime, and downloads nothing. Its strong
compression takes a few minutes; -Fast is quicker and makes a bigger installer.

.PARAMETER Configuration
The configuration to publish; Release by default.

.PARAMETER Iscc
The ISCC.exe to compile with.

.PARAMETER SkipPublish
Compiles what artifacts\publish already holds instead of publishing again.

.PARAMETER Fast
Compresses with lzma2/fast instead of lzma2/ultra64, for a quick local build.

.PARAMETER TestVariants
Also compiles the build the installer test needs into installer\output\test: the next patch version, for the upgrade.
It is compressed with lzma2/fast, since the test doesn't care about its size.
#>
param(
    [string]$Configuration = 'Release',
    [string]$Iscc,
    [switch]$SkipPublish,
    [switch]$Fast,
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
    foreach ($base in "$env:LOCALAPPDATA\Programs", $env:ProgramFiles, ${env:ProgramFiles(x86)} | Where-Object { $_ }) {
        $candidate = Join-Path $base 'Inno Setup 7\ISCC.exe'
        if (Test-Path $candidate) { return $candidate }
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

$fastCompression = '/DCompression=lzma2/fast'
$options = @("/DAppVersion=$version")
if ($Fast) { $options += $fastCompression }
Invoke-Iscc $options
$made = @(Join-Path $PSScriptRoot "output\PowerLedger-$version-setup.exe")

if ($TestVariants) {
    $test = Join-Path $PSScriptRoot 'output\test'
    $current = [version]$version
    $next = '{0}.{1}.{2}' -f $current.Major, $current.Minor, ([Math]::Max($current.Build, 0) + 1)
    $upgrade = "PowerLedger-$next-setup"
    Invoke-Iscc @("/DAppVersion=$next", $fastCompression, "/O$test", "/F$upgrade")
    $made += Join-Path $test "$upgrade.exe"
}

foreach ($file in $made) { '{0}: {1:N1} MB' -f [IO.Path]::GetRelativePath($root, $file), ((Get-Item $file).Length / 1MB) }
