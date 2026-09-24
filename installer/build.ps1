<#
.SYNOPSIS
Publishes PowerLedger and compiles its installer into installer\output.

.DESCRIPTION
Needs Inno Setup 7.1 or later (https://jrsoftware.org/isinfo.php); installer\get-inno-setup.ps1 installs the pinned one.
Inno Setup 6's 32-bit compiler can't use the 256 MB compression dictionary the script sets. Uses -Iscc when given, else
Inno Setup 7 installed for this user or for everyone, else an ISCC.exe on PATH that is Inno Setup 7; it refuses older ones.
The version comes from Directory.Build.props, so the installer and the programs always agree.
The installer holds the x64, the Arm64 and the 32-bit x86 build, each with its own .NET runtime, and downloads nothing. Its strong
compression takes a few minutes; -Fast is quicker and makes a bigger installer.
Google's Desktop OAuth client secret comes from POWERLEDGER_GOOGLE_CLIENT_SECRET, else
%USERPROFILE%\.powerledger\google-client-secret.txt, and is never printed; with neither, the build still succeeds, with
Google sign-in unavailable.

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

.PARAMETER For
Which installers to compile: `both` holds every build, `x64`, `arm64` and `x86` only their own and are much smaller,
which is what an update downloads.
#>
param(
    [string]$Configuration = 'Release',
    [string]$Iscc,
    [switch]$SkipPublish,
    [switch]$Fast,
    [switch]$TestVariants,
    [ValidateSet('both', 'x64', 'arm64', 'x86')]
    [string[]]$For = @('both')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $PSScriptRoot 'PowerLedger.iss'

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

# Google's Desktop OAuth client secret (households design §7): the repo is public, so it never sits in source. Read
# from the environment, else a file in the profile, and never printed; a build with neither still succeeds, just
# without Google sign-in.
function Get-GoogleClientSecret {
    if ($env:POWERLEDGER_GOOGLE_CLIENT_SECRET) { return $env:POWERLEDGER_GOOGLE_CLIENT_SECRET }
    $path = Join-Path $env:USERPROFILE '.powerledger\google-client-secret.txt'
    if (Test-Path $path -PathType Leaf) {
        $value = (Get-Content $path -Raw).Trim()
        if ($value) { return $value }
    }
    Write-Warning 'Google sign-in is off in this build.'
    ''
}

if (-not $SkipPublish) {
    $googleClientSecret = Get-GoogleClientSecret
    & (Join-Path $root 'scripts\publish.ps1') -Configuration $Configuration -GoogleClientSecret $googleClientSecret
}

# The major version of an ISCC.exe, from the banner it prints, since the file itself carries no version.
function Get-InnoMajor([string]$Path) {
    $help = & $Path '/?' 2>&1 | Out-String
    if ($help -match 'Inno Setup (\d+) Command-Line Compiler') { [int]$Matches[1] } else { 0 }
}

# Inno Setup 7's own folders first: a machine can also have Inno Setup 6 on PATH, as GitHub's Windows runners do.
function Find-Iscc {
    foreach ($base in "$env:LOCALAPPDATA\Programs", $env:ProgramFiles, ${env:ProgramFiles(x86)} | Where-Object { $_ }) {
        $candidate = Join-Path $base 'Inno Setup 7\ISCC.exe'
        if (Test-Path $candidate) { return $candidate }
    }
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command -and (Get-InnoMajor $command.Source) -ge 7) { return $command.Source }
    throw 'Inno Setup 7 is not installed. Run installer\get-inno-setup.ps1, or get it from https://jrsoftware.org/isinfo.php, and run this again.'
}

if (-not $Iscc) { $Iscc = Find-Iscc }
elseif (-not (Test-Path $Iscc -PathType Leaf)) { throw "There is no ISCC.exe at $Iscc." }
$major = Get-InnoMajor $Iscc
if ($major -lt 7) {
    $found = if ($major) { "Inno Setup $major's compiler" } else { "not Inno Setup's compiler" }
    throw "$Iscc is $found. The installer needs Inno Setup 7, whose 64-bit compiler can use its 256 MB dictionary; run installer\get-inno-setup.ps1."
}
"Compiling with $Iscc (Inno Setup $major)"

function Invoke-Iscc([string[]]$Options) {
    & $Iscc @Options $script
    if ($LASTEXITCODE -ne 0) { throw "The installer did not compile (ISCC $Options)." }
}

$fastCompression = '/DCompression=lzma2/fast'

# Inno Setup doesn't count files it picks per architecture toward the disk space setup asks for, so it is told what the
# bigger build takes.
$sizes = @{}
foreach ($folder in Get-ChildItem (Join-Path $root 'artifacts\publish') -Directory) {
    $sizes[$folder.Name] = (Get-ChildItem $folder.FullName -Recurse -File | Measure-Object Length -Sum).Sum
}
if ($sizes.Count -eq 0) { throw 'artifacts\publish is empty; run this without -SkipPublish.' }
function Payload([string]$Architecture) {
    $bytes = switch ($Architecture) {
        'x64' { $sizes['win-x64'] }
        'arm64' { $sizes['win-arm64'] }
        'x86' { $sizes['win-x86'] }
        default { ($sizes.Values | Measure-Object -Maximum).Maximum }
    }
    "/DPayloadBytes=$([long]$bytes)"
}

$made = @()
foreach ($architecture in $For) {
    $options = @("/DAppVersion=$version", (Payload $architecture), "/DArchitecture=$architecture")
    if ($Fast) { $options += $fastCompression }
    Invoke-Iscc $options
    $made += Join-Path $PSScriptRoot ("output\PowerLedger-$version-setup{0}.exe" -f $(if ($architecture -eq 'both') { '' } else { "-$architecture" }))
}

if ($TestVariants) {
    $test = Join-Path $PSScriptRoot 'output\test'
    $current = [version]$version
    $next = '{0}.{1}.{2}' -f $current.Major, $current.Minor, ([Math]::Max($current.Build, 0) + 1)
    $upgrade = "PowerLedger-$next-setup"
    Invoke-Iscc @("/DAppVersion=$next", (Payload 'both'), $fastCompression, "/O$test", "/F$upgrade")
    $made += Join-Path $test "$upgrade.exe"
}

foreach ($file in $made) { '{0}: {1:N1} MB' -f [IO.Path]::GetRelativePath($root, $file), ((Get-Item $file).Length / 1MB) }
