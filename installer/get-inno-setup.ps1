<#
.SYNOPSIS
Installs the Inno Setup that builds PowerLedger's installer, for the current user, from its official release.

.DESCRIPTION
Downloads Inno Setup 7.1.0 (x64) from github.com/jrsoftware/issrc, refuses it unless its SHA-256 matches and its
Authenticode signature is valid and Pyrsys B.V.'s (Inno Setup's publisher), and installs it for this user only, so no
administrator is needed. Prints the ISCC.exe path, which installer\build.ps1 finds on its own. Does nothing when it is
already there. Inno Setup 6 won't do: its 32-bit compiler can't use the compression dictionary the script sets.

.EXAMPLE
pwsh installer\get-inno-setup.ps1
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$file = 'innosetup-7.1.0-x64.exe'
$url = "https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/$file"
$sha256 = '0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F'
$folder = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7'
$iscc = Join-Path $folder 'ISCC.exe'
if (Test-Path $iscc) { return $iscc }

$download = Join-Path ([IO.Path]::GetTempPath()) $file
try {
    Invoke-WebRequest $url -OutFile $download
    $hash = (Get-FileHash $download -Algorithm SHA256).Hash
    if ($hash -ne $sha256) { throw "$file has SHA-256 $hash, not the pinned $sha256." }
    $signature = Get-AuthenticodeSignature $download
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys') {
        throw "$file is not validly signed by Pyrsys B.V. ($($signature.Status))."
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
