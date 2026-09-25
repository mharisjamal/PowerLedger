#Requires -Version 7
<#
.SYNOPSIS
Publishes the version in Directory.Build.props as a GitHub release with its installer, which installed copies then offer
as an update.

.DESCRIPTION
Run it from main, clean and pushed: the release is tagged at that commit. It refuses a version already released, builds
the universal installer and one per architecture with installer\build.ps1 -For both,x64,arm64,x86 (or uses the ones already
in installer\output with -SkipBuild), since an update downloads whichever matches the PC, creates the release vX.Y.Z with
the notes in -Notes and all four installers attached, and checks that the SHA-256 GitHub lists for each is the local
file's, since every installed copy checks its download against that digest (spec §13). -Draft makes a draft, which
nobody is offered until it is published on GitHub.

Each installer is also signed, with its version, file name and SHA-256, by the owner's key,
%USERPROFILE%\.powerledger\release-signing.pem (made once by
scripts\new-release-key.ps1), and the signatures, checked before anything is uploaded, go up as
PowerLedger-X.Y.Z-signatures.json; the service installs an update itself only when its signature verifies against the
public key in src\PowerLedger.Service\Updates\ReleaseKey.cs (Plan Q §4). Without the key file nothing is released.

gh must be signed in to an account that may publish to the repository, or GH_TOKEN must hold a token for one.

.EXAMPLE
pwsh scripts/release.ps1 -Notes notes-0.2.0.md
#>
param(
    [Parameter(Mandatory)][string]$Notes,
    [string]$Repo = 'mharisjamal/PowerLedger',
    [switch]$SkipBuild,
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # git's and gh's exit codes are read, not thrown
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-signing.ps1')

# Runs a native command, and throws when it fails.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    $output = & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
    $output
}

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }
$tag = "v$version"
$Notes = (Resolve-Path $Notes).Path
$installers = @(
    Join-Path $root "installer\output\PowerLedger-$version-setup.exe"
    Join-Path $root "installer\output\PowerLedger-$version-setup-x64.exe"
    Join-Path $root "installer\output\PowerLedger-$version-setup-arm64.exe"
    Join-Path $root "installer\output\PowerLedger-$version-setup-x86.exe"
)

# The release is tagged at the commit that was built, which must be main as GitHub has it.
$branch = Invoke-Native 'git branch' { git -C $root branch --show-current }
if ($branch -ne 'main') { throw "Release from main, not $branch." }
if (Invoke-Native 'git status' { git -C $root status --porcelain }) { throw 'The working tree has changes; commit or stash them first.' }
Invoke-Native 'git fetch' { git -C $root fetch --quiet origin main } | Out-Null
$head = Invoke-Native 'git rev-parse' { git -C $root rev-parse HEAD }
$pushed = Invoke-Native 'git rev-parse' { git -C $root rev-parse origin/main }
if ($head -ne $pushed) { throw "main ($head) isn't what GitHub has ($pushed); push or pull first." }
gh release view $tag --repo $Repo --json tagName *> $null
if ($LASTEXITCODE -eq 0) { throw "$tag is already released; raise <Version> in Directory.Build.props for a new one." }

# The signing key, checked before the long build: missing, or not the one installed services trust, stops the release.
$publicKey = Get-ReleasePublicKey
$compiled = Get-CompiledReleaseKey $root
if ($compiled -and $compiled -ne $publicKey) { throw "The key in $ReleaseKeyPath isn't the one in ReleaseKey.cs, so installed services would refuse this release." }
if (-not $compiled) { Write-Warning 'ReleaseKey.cs holds no public key yet, so services built from this commit never install an update themselves.' }

if (-not $SkipBuild) { & (Join-Path $root 'installer\build.ps1') -For both,x64,arm64,x86 }
foreach ($file in $installers) { if (-not (Test-Path $file)) { throw "There is no installer at $file; build it with installer\build.ps1 -For both,x64,arm64,x86." } }

$signaturesFile = Join-Path $root "installer\output\PowerLedger-$version-signatures.json"
New-ReleaseSignatures $installers $version | ConvertTo-Json | Set-Content -LiteralPath $signaturesFile -Encoding utf8NoBOM
Test-ReleaseSignatures $signaturesFile $installers $version $publicKey
$assets = $installers + @($signaturesFile)
$shas = @{}
foreach ($file in $assets) { $shas[(Split-Path $file -Leaf)] = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() }

$create = @('release', 'create', $tag) + $assets + @('--repo', $Repo, '--target', $head, '--title', "PowerLedger $version", '--notes-file', $Notes)
if ($Draft) { $create += '--draft' }
Invoke-Native 'gh release create' { gh @create } | Out-Host

# Installed copies check their download against this digest, so it has to be the file built here. GitHub works it out
# after the upload, so it can take a few seconds to appear.
foreach ($attempt in 1..20) {
    $listed = (Invoke-Native 'gh release view' { gh release view $tag --repo $Repo --json assets } | ConvertFrom-Json).assets
    if (@($shas.Keys | Where-Object { -not ($listed | Where-Object name -eq $_).digest }).Count -eq 0) { break }
    Start-Sleep -Seconds 3
}
foreach ($name in $shas.Keys) {
    $digest = ($listed | Where-Object name -eq $name).digest
    if ($digest -ne "sha256:$($shas[$name])") { throw "GitHub lists $($digest ?? 'no SHA-256') for $name, not sha256:$($shas[$name])." }
}
if (-not $Draft) { Invoke-Native 'git fetch' { git -C $root fetch --quiet --tags origin } | Out-Null }
"Released ${tag}: https://github.com/$Repo/releases/tag/$tag"
foreach ($name in $shas.Keys) { "${name}: $($shas[$name])" }
