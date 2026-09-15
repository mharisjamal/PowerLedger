<#
.SYNOPSIS
Installs the Inno Setup that builds PowerLedger's installer, for the current user, from its official release.

.DESCRIPTION
Downloads the pinned release from github.com/jrsoftware/issrc, refuses it unless its SHA-256 matches and its Authenticode
signature is valid and Pyrsys B.V.'s (Inno Setup's publisher), and installs it for this user only, so no administrator is
needed. Prints the ISCC.exe path, which installer\build.ps1 finds on its own. Does nothing when that version is there.
7.1.0 is what the installer is built with; 6.7.3 is kept for checking the script still compiles with Inno Setup 6.

.EXAMPLE
pwsh installer\get-inno-setup.ps1
#>
param([ValidateSet('7.1.0', '6.7.3')][string]$Version = '7.1.0')

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$releases = @{
    '7.1.0' = @{ File = 'innosetup-7.1.0-x64.exe'; Tag = 'is-7_1_0'; Folder = 'Inno Setup 7'
        Sha256 = '0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F' }
    '6.7.3' = @{ File = 'innosetup-6.7.3.exe'; Tag = 'is-6_7_3'; Folder = 'Inno Setup 6'
        Sha256 = '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732' }
}
$release = $releases[$Version]
$folder = Join-Path $env:LOCALAPPDATA "Programs\$($release.Folder)"
$iscc = Join-Path $folder 'ISCC.exe'
if (Test-Path $iscc) { return $iscc }

$download = Join-Path ([IO.Path]::GetTempPath()) $release.File
try {
    Invoke-WebRequest "https://github.com/jrsoftware/issrc/releases/download/$($release.Tag)/$($release.File)" -OutFile $download
    $hash = (Get-FileHash $download -Algorithm SHA256).Hash
    if ($hash -ne $release.Sha256) { throw "$($release.File) has SHA-256 $hash, not the pinned $($release.Sha256)." }
    $signature = Get-AuthenticodeSignature $download
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys') {
        throw "$($release.File) is not validly signed by Pyrsys B.V. ($($signature.Status))."
    }

    # Start-Process -Wait would also wait for anything setup leaves running; waiting on the process itself doesn't.
    $setup = Start-Process $download -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', "/DIR=`"$folder`"")
    $null = $setup.Handle   # keeps the exit code readable after the process ends
    $setup.WaitForExit()
    if ($setup.ExitCode -ne 0) { throw "Inno Setup's installer failed with exit code $($setup.ExitCode)." }
}
finally {
    Remove-Item $download -ErrorAction SilentlyContinue
}

if (-not (Test-Path $iscc)) { throw "Inno Setup installed, but $iscc is missing." }
$iscc
