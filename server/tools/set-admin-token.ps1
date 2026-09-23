#requires -Version 5.1
<#
.SYNOPSIS
Sets the Worker's ADMIN_TOKEN secret, from (or by creating) the owner's local admin.json.
.DESCRIPTION
Run this yourself, on the machine you use to deploy. Nobody else should see the token it holds or
pipes to wrangler. It keeps an existing admin.json as is, salt included, so pseudonyms in an
export stay the same from one run to the next.
#>

$ErrorActionPreference = 'Stop'

function New-RandomBase64Url {
    $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$configDir = Join-Path $env:USERPROFILE '.powerledger'
$configPath = Join-Path $configDir 'admin.json'

if (Test-Path $configPath) {
    $config = Get-Content -Path $configPath -Raw | ConvertFrom-Json
    if (-not $config.token -or -not $config.salt) {
        throw "$configPath exists but is missing a token or a salt."
    }
} else {
    New-Item -ItemType Directory -Force -Path $configDir | Out-Null
    $config = [pscustomobject]@{
        token = New-RandomBase64Url
        salt  = New-RandomBase64Url
    }
    $config | ConvertTo-Json | Set-Content -Path $configPath -NoNewline -Encoding utf8
}

# Only the owner can read or write it: reset inheritance, then grant just this account.
icacls $configPath /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null

$config.token | npx wrangler secret put ADMIN_TOKEN | Out-Null

Write-Output 'Admin token set.'
