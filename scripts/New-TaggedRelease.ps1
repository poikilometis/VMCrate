#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Tag, [string]$OutputDirectory = 'artifacts/releases')

$ErrorActionPreference = 'Stop'
$props = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw)
if ($Tag -cne ('v' + $props.Project.PropertyGroup.Version)) {
    throw 'The Git tag must match the version in Directory.Build.props.'
}

# The signing store belongs to the ephemeral GitHub runner. Child processes do not inherit the secrets.
$certificateBytes = $env:VMCRATE_SIGNING_PFX
$password = $env:VMCRATE_SIGNING_PASSWORD
$env:VMCRATE_SIGNING_PFX = $null
$env:VMCRATE_SIGNING_PASSWORD = $null
$build = Join-Path $PSScriptRoot 'New-Release.ps1'
if (-not $certificateBytes) {
    & $build -OutputDirectory $OutputDirectory
    return
}
if (-not $password -or -not $env:VMCRATE_TIMESTAMP_SERVER) {
    throw 'Configure the signing password and timestamp server for signed releases.'
}

$pfxPath = Join-Path ([IO.Path]::GetTempPath()) ('vmcrate-' + [guid]::NewGuid().ToString('N') + '.pfx')
try {
    [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($certificateBytes))
    $secret = ConvertTo-SecureString -String $password -AsPlainText -Force
    $certificates = @(Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation 'Cert:\CurrentUser\My' -Password $secret |
        Where-Object HasPrivateKey)
    if ($certificates.Count -ne 1) { throw 'Expected one code-signing key.' }
    & $build -OutputDirectory $OutputDirectory -SigningThumbprint $certificates[0].Thumbprint `
        -SignExecutables -TimestampServer $env:VMCRATE_TIMESTAMP_SERVER
} finally {
    [IO.File]::Delete($pfxPath)
}
