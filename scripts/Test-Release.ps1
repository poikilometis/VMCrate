#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$SignerSha256)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseSignature.ps1')
$zip = (Resolve-Path -LiteralPath $Archive).Path
$manifest = "$zip.sha256"
Test-CrateManifestSignature -Manifest $manifest -Signature "$manifest.p7s" -SignerSha256 $SignerSha256
$line = (Get-Content -LiteralPath $manifest -Raw).TrimEnd()
if ($line -notmatch '^([0-9a-fA-F]{64})  ([A-Za-z0-9.-]+\.zip)$') { throw 'Invalid release checksum manifest.' }
if ($Matches[2] -cne [IO.Path]::GetFileName($zip) -or (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $Matches[1]) {
    throw 'The ZIP does not match its signed release manifest.'
}
Write-Output 'PASS publisher signature and ZIP SHA-256'
