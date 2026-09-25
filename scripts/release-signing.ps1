#Requires -Version 7
# Release signing (Plan Q §4), dot-sourced by release.ps1: each installer's SHA-256 signed with the owner's ECDSA P-256 key
# from scripts\new-release-key.ps1, as the service checks it before it installs an update itself.

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

# { "<file name>": "<base64 DER signature of its SHA-256>" } for each file, each signature checked against the public key
# before it is returned, so a release never carries one the service would refuse.
function New-ReleaseSignatures([string[]]$Files, [string]$KeyPath = $ReleaseKeyPath) {
    $public = Get-ReleasePublicKey $KeyPath
    $der = [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence
    $signer = [System.Security.Cryptography.ECDsa]::Create()
    $checker = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $signer.ImportFromPem((Get-Content -Raw -LiteralPath $KeyPath))
        $bytesRead = 0
        $checker.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($public), [ref]$bytesRead)
        $signatures = [ordered]@{}
        foreach ($file in $Files) {
            $digest = [Convert]::FromHexString((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash)
            $signature = $signer.SignHash($digest, $der)
            if (-not $checker.VerifyHash($digest, $signature, $der)) { throw "The signature made for $file doesn't verify." }
            $signatures[(Split-Path $file -Leaf)] = [Convert]::ToBase64String($signature)
        }
        $signatures
    }
    finally {
        $signer.Dispose()
        $checker.Dispose()
    }
}

# Checks a signatures file against the files it names and a public key; throws on the first that doesn't verify.
function Test-ReleaseSignatures([string]$SignaturesFile, [string[]]$Files, [string]$PublicKey) {
    $signatures = Get-Content -Raw -LiteralPath $SignaturesFile | ConvertFrom-Json -AsHashtable
    $der = [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence
    $checker = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $bytesRead = 0
        $checker.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($PublicKey), [ref]$bytesRead)
        foreach ($file in $Files) {
            $name = Split-Path $file -Leaf
            if (-not $signatures.ContainsKey($name)) { throw "$SignaturesFile has no signature for $name." }
            $digest = [Convert]::FromHexString((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash)
            if (-not $checker.VerifyHash($digest, [Convert]::FromBase64String($signatures[$name]), $der)) { throw "The signature for $name doesn't verify." }
        }
    }
    finally { $checker.Dispose() }
}
