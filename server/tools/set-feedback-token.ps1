#requires -Version 5.1
<#
.SYNOPSIS
Sets the Worker's FEEDBACK_GITHUB_TOKEN secret: the token in-app feedback files issues with.
.DESCRIPTION
Run this yourself, on the machine you use to deploy, in server\. It asks for the token without
showing it, and pipes it to wrangler; nothing keeps or prints it. The token is a fine-grained
personal access token for the PRIVATE feedback repo alone (wrangler.toml's FEEDBACK_REPO), with
Contents: read and write, and Issues: read and write, on that one repo, and nothing else.
#>

$ErrorActionPreference = 'Stop'

$secure = Read-Host -Prompt 'GitHub token for the feedback repo (not shown)' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
}
if ([string]::IsNullOrWhiteSpace($token)) { throw 'No token was given.' }
$token = $token.Trim()
if ($token -notmatch '^(github_pat_|ghp_)[A-Za-z0-9_]+$') {
    Write-Warning "That doesn't look like a GitHub personal access token (github_pat_... or ghp_...); setting it anyway."
}

# The token goes in on standard input and wrangler's own output is kept back; neither is ever shown.
$token | npx wrangler secret put FEEDBACK_GITHUB_TOKEN 2>&1 | Out-Null
$token = $null
if ($LASTEXITCODE -ne 0) {
    throw "wrangler couldn't set the feedback token (exit $LASTEXITCODE). Run 'npx wrangler login' in server\ first, then run this again."
}

Write-Output 'Feedback token set.'
