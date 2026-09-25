#Requires -Version 7
# Release signing (Plan Q §4), dot-sourced by release.ps1: each installer signed with the owner's ECDSA P-256 key from
# scripts\new-release-key.ps1, as the service checks it before it installs an update itself. What is signed is the UTF-8
# message "PowerLedger|<version X.Y.Z>|<file name>|<SHA-256, lowercase hex>", so a signature vouches for one installer
# of one release and can't be carried over to another version or file name.

# The message signed for $File as an installer of $Version.
function Get-ReleaseSignedMessage([string]$Version, [string]$File) {
    $sha = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.Text.Encoding]::UTF8.GetBytes("PowerLedger|$Version|$(Split-Path $File -Leaf)|$sha")
}

# Where new-release-key.ps1 keeps the key.
$ReleaseKeyPath = Join-Path $HOME '.powerledger\release-signing.pem'

# The public key compiled into the service, from ReleaseKey.cs; empty when none is set yet.
function Get-CompiledReleaseKey([string]$Root) {
    $source = Get-Content -Raw (Join-Path $Root 'src\PowerLedger.Service\Updates\ReleaseKey.cs')
    if ($source -notmatch 'PublicKey\s*=\s*"([^"]*)"') { throw 'ReleaseKey.cs has no PublicKey constant.' }
    $Matches[1]
}

# The public key, base64 SubjectPublicKeyInfo, of the private key at $KeyPath; throws clearly when there is none.
function Get-ReleasePublicKey([string]$KeyPath = $ReleaseKeyPath) {
    if (-not (Test-Path -LiteralPath $KeyPath)) { throw "There is no release signing key at $KeyPath. Make one once with scripts\new-release-key.ps1, and put the public key it prints in src\PowerLedger.Service\Updates\ReleaseKey.cs." }
    $key = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $key.ImportFromPem((Get-Content -Raw -LiteralPath $KeyPath))
        [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
    }
    finally { $key.Dispose() }
}

# { "<file name>": "<base64 DER signature of its message>" } for each file of release $Version, each signature checked
# against the public key before it is returned, so a release never carries one the service would refuse.
function New-ReleaseSignatures([string[]]$Files, [string]$Version, [string]$KeyPath = $ReleaseKeyPath) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "The version to sign for must be X.Y.Z, not '$Version'." }
    $public = Get-ReleasePublicKey $KeyPath
    $der = [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence
    $signer = [System.Security.Cryptography.ECDsa]::Create()
    $checker = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $signer.ImportFromPem((Get-Content -Raw -LiteralPath $KeyPath))
        $bytesRead = 0
        $checker.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($public), [ref]$bytesRead)
        $signatures = [ordered]@{}
        $sha256 = [System.Security.Cryptography.HashAlgorithmName]::SHA256
        foreach ($file in $Files) {
            $message = Get-ReleaseSignedMessage $Version $file
            $signature = $signer.SignData($message, $sha256, $der)
            if (-not $checker.VerifyData($message, $signature, $sha256, $der)) { throw "The signature made for $file doesn't verify." }
            $signatures[(Split-Path $file -Leaf)] = [Convert]::ToBase64String($signature)
        }
        $signatures
    }
    finally {
        $signer.Dispose()
        $checker.Dispose()
    }
}

# Checks a signatures file against the files of release $Version it names and a public key; throws on the first that
# doesn't verify.
function Test-ReleaseSignatures([string]$SignaturesFile, [string[]]$Files, [string]$Version, [string]$PublicKey) {
    $signatures = Get-Content -Raw -LiteralPath $SignaturesFile | ConvertFrom-Json -AsHashtable
    $der = [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence
    $sha256 = [System.Security.Cryptography.HashAlgorithmName]::SHA256
    $checker = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $bytesRead = 0
        $checker.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($PublicKey), [ref]$bytesRead)
        foreach ($file in $Files) {
            $name = Split-Path $file -Leaf
            if (-not $signatures.ContainsKey($name)) { throw "$SignaturesFile has no signature for $name." }
            $message = Get-ReleaseSignedMessage $Version $file
            if (-not $checker.VerifyData($message, [Convert]::FromBase64String($signatures[$name]), $sha256, $der)) { throw "The signature for $name doesn't verify." }
        }
    }
    finally { $checker.Dispose() }
}
