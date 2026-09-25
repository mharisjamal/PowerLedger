#Requires -Version 7
<#
.SYNOPSIS
Makes the release signing key once: an ECDSA P-256 key the owner keeps, which release.ps1 signs every installer with and
installed services check before they install an update themselves (Plan Q §4).

.DESCRIPTION
Writes the private key as PEM to %USERPROFILE%\.powerledger\release-signing.pem, a file only the current user can read,
created with that ACL from the start, and refuses to replace a key that is already there: a new key would leave every
installed copy refusing the next update. It prints only the public key, as base64 SubjectPublicKeyInfo, for
src\PowerLedger.Service\Updates\ReleaseKey.cs. The private key never leaves the file; back the file up somewhere only
the owner can reach.

.EXAMPLE
pwsh scripts/new-release-key.ps1
#>
param(
    # Where the key goes; the default is the one release.ps1 reads.
    [string]$Path = (Join-Path $HOME '.powerledger\release-signing.pem')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (Test-Path -LiteralPath $Path) { throw "$Path already exists, and is not replaced: installed copies trust the key in it. Delete it yourself only if you mean to start a new key." }
$folder = Split-Path -Parent $Path
if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder | Out-Null }

# The current user alone, nothing inherited, set as the file is created so the key is never readable by anyone else.
$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$security = [System.Security.AccessControl.FileSecurity]::new()
$security.SetAccessRuleProtection($true, $false)
$security.SetOwner($user)
$security.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
    $user, [System.Security.AccessControl.FileSystemRights]::FullControl, [System.Security.AccessControl.AccessControlType]::Allow))

$key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $pem = [System.Text.Encoding]::ASCII.GetBytes($key.ExportPkcs8PrivateKeyPem())
    $file = [System.IO.FileSystemAclExtensions]::Create(
        [System.IO.FileInfo]::new($Path), [System.IO.FileMode]::CreateNew, [System.Security.AccessControl.FileSystemRights]::FullControl,
        [System.IO.FileShare]::None, 4096, [System.IO.FileOptions]::None, $security)
    try { $file.Write($pem, 0, $pem.Length) } finally { $file.Dispose() }
    [Array]::Clear($pem)
    [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
}
finally {
    $key.Dispose()
}
