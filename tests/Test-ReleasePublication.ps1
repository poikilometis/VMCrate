#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$fixture = [IO.Directory]::CreateTempSubdirectory('vmcrate-release-api-').FullName
$zipName = 'VMCrate-0.7.0-win-x64.zip'
$zip = Join-Path $fixture $zipName
$manifest = "$zip.sha256"
$state = @{ Draft = $false; Published = $false; ListingsAfterCreate = 0; AssetListings = 0;
    Assets = [Collections.Generic.List[object]]::new() }

# Model GitHub's reads without credentials or a network connection.
function gh {
    $global:LASTEXITCODE = 0
    $command = "$($args[0]) $($args[1])"
    if ($command -eq 'api repos/example/fixture/releases?per_page=100') {
        if ($state.Draft) { $state.ListingsAfterCreate++ }
        if ($state.ListingsAfterCreate -ge 2 -or $state.Published) {
            ConvertTo-Json -InputObject @(@{ id = 7; tag_name = 'v0.7.0'; draft = (-not $state.Published);
                prerelease = $false; assets = @($state.Assets.ToArray()) }) -Depth 5
        } else {
            '[]'
        }
    } elseif ($command -eq 'api repos/example/fixture/releases/7/assets') {
        $state.AssetListings++
        $assets = $state.Assets.ToArray()
        if ($state.AssetListings -eq 1) { $assets = @() }
        ConvertTo-Json -InputObject @($assets) -Depth 5
    } elseif ($command -eq 'release create') {
        if ($state.Draft) { throw 'Duplicate draft creation.' }
        $state.Draft = $true
    } elseif ($command -eq 'release upload') {
        $file = $args[3]
        $state.Assets.Add(@{ name = (Split-Path -Leaf $file); state = 'uploaded';
            digest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() })
    } elseif ($command -eq 'release edit') {
        if ($state.AssetListings -lt 2 -or $state.Assets.Count -ne 2) { throw 'Unverified release published.' }
        $state.Published = $true
    } else {
        throw "Unexpected GitHub fixture command ($command)."
    }
}

function Start-Sleep([int]$Seconds) {}

try {
    [IO.File]::WriteAllText($zip, 'Test release bytes')
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($manifest, "$hash  $zipName")
    $publish = Join-Path $PSScriptRoot '../scripts/Publish-TaggedRelease.ps1'
    & $publish -Tag v0.7.0 -Repository 'example/fixture' -Directory $fixture
    if (-not $state.Published -or $state.ListingsAfterCreate -lt 2) { throw 'The release was not published after delayed reads.' }
    & $publish -Tag v0.7.0 -Repository 'example/fixture' -Directory $fixture
    if ($state.Assets.Count -ne 2) { throw 'An existing publication was modified.' }
    Write-Output 'PASS delayed draft visibility, delayed asset visibility and idempotent rerun'
} finally {
    [IO.File]::Delete($zip)
    [IO.File]::Delete($manifest)
    [IO.Directory]::Delete($fixture)
}
