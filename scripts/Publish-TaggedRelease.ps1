#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+(?:-preview\.\d+)?$')][string]$Tag,
    [Parameter(Mandatory)][string]$Repository,
    [string]$Directory = 'artifacts/releases')

$ErrorActionPreference = 'Stop'
$zipName = "VMCrate-$($Tag.Substring(1))-win-x64.zip"
$zip = Join-Path $Directory $zipName
$manifest = "$zip.sha256"
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-Content -LiteralPath $manifest -Raw).TrimEnd() -cne "$hash  $zipName") {
    throw 'The release ZIP checksum is incorrect.'
}
$files = @($zip, $manifest)
if (Test-Path -LiteralPath "$manifest.p7s" -PathType Leaf) { $files += "$manifest.p7s" }

$releases = gh release list --repo $Repository --limit 100 --json tagName,isDraft | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not list GitHub releases.' }
$existing = $releases | Where-Object tagName -CEQ $Tag
if ($existing -and -not $existing.isDraft) {
    Write-Output "Release $Tag is already published."
    return
}
if (-not $existing) {
    $options = @('--repo', $Repository, '--verify-tag', '--draft', '--title', "VMCrate $Tag")
    $notes = Join-Path $PSScriptRoot "../docs/releases/$Tag.md"
    if (Test-Path -LiteralPath $notes -PathType Leaf) { $options += @('--notes-file', $notes) }
    else { $options += '--generate-notes' }
    if ($Tag.Contains('-')) { $options += '--prerelease' }
    gh release create $Tag @options
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the GitHub release draft.' }
}

$release = gh api "repos/$Repository/releases?per_page=100" | ConvertFrom-Json |
    Where-Object tag_name -CEQ $Tag
if ($LASTEXITCODE -ne 0 -or -not $release.draft -or $release.prerelease -ne $Tag.Contains('-')) {
    throw 'The release draft or its release channel could not be verified.'
}
$expectedNames = @($files | ForEach-Object { Split-Path -Leaf $_ })
if ($release.assets | Where-Object { $_.name -cnotin $expectedNames }) { throw 'The draft contains unexpected assets.' }
foreach ($file in $files) {
    $name = Split-Path -Leaf $file
    $asset = $release.assets | Where-Object name -CEQ $name
    if ($asset) {
        $digest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($asset.state -ne 'uploaded' -or $asset.digest -cne $digest) { throw "The draft asset differs ($name)." }
    } else {
        gh release upload $Tag $file --repo $Repository
        if ($LASTEXITCODE -ne 0) { throw "Could not upload $name." }
    }
}
$uploaded = gh api "repos/$Repository/releases/$($release.id)/assets" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or @($uploaded).Count -ne $files.Count) { throw 'The complete release assets are unavailable.' }
foreach ($file in $files) {
    $name = Split-Path -Leaf $file
    $asset = $uploaded | Where-Object name -CEQ $name
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($null -eq $asset -or $asset.state -ne 'uploaded' -or $asset.digest -cne $digest) {
        throw "The uploaded asset differs ($name)."
    }
}
gh release edit $Tag --repo $Repository --verify-tag --draft=false
if ($LASTEXITCODE -ne 0) { throw 'Could not publish the GitHub release.' }
