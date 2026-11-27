#Requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/releases',
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SigningThumbprint)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseSignature.ps1')
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (-not $IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'Build VMCrate releases on Windows x64.'
}

$props = [xml](Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props') -Raw)
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-preview\.\d+)?$') { throw 'Invalid release version in Directory.Build.props.' }

$dotnet = (Get-Command dotnet.exe -CommandType Application | Select-Object -First 1).Source
$dotnetRoot = Split-Path -Parent $dotnet
$desktop = Join-Path $dotnetRoot 'shared/Microsoft.WindowsDesktop.App'
$runtimeVersion = Get-ChildItem -LiteralPath $desktop -Directory |
    Where-Object { $_.Name -match '^10\.0\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    Where-Object {
        (Test-Path -LiteralPath (Join-Path $dotnetRoot "host/fxr/$($_.Name)/hostfxr.dll")) -and
        (Test-Path -LiteralPath (Join-Path $dotnetRoot "shared/Microsoft.NETCore.App/$($_.Name)/coreclr.dll")) -and
        (Test-Path -LiteralPath (Join-Path $dotnetRoot "shared/Microsoft.AspNetCore.App/$($_.Name)/Microsoft.AspNetCore.dll"))
    } | Select-Object -First 1 -ExpandProperty Name
if (-not $runtimeVersion) { throw 'Install matching .NET 10 x64 Desktop, Core and ASP.NET Core runtimes.' }

$output = [IO.Path]::GetFullPath($OutputDirectory, $repository)
[void][IO.Directory]::CreateDirectory($output)
$output = (Resolve-Path -LiteralPath $output).Path
$tag = [guid]::NewGuid().ToString('N')
$stage = Join-Path $output ".staging-$tag"
$stagedZip = Join-Path $output ".staging-$tag.zip"
$stagedHash = Join-Path $output ".staging-$tag.sha256"
$stagedSignature = "$stagedHash.p7s"
$zipName = "VMCrate-$version-win-x64.zip"
$zip = Join-Path $output $zipName
if ((Test-Path -LiteralPath "$zip.sha256.p7s") -and -not $SigningThumbprint) {
    throw 'Choose a new version or sign the replacement with a release certificate.'
}
Push-Location -LiteralPath $repository
try {
    $publishArgs = @('publish', 'src/VMCrate.Desktop', '-c', 'Release', '--self-contained', 'false',
        '-o', $stage, '-m:1', '--disable-build-servers', '-p:AppHostDotNetSearch=AppRelative', '-p:AppHostRelativeDotNet=runtime')
    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    $cli = Join-Path $stage 'cli'
    $cliArgs = @('publish', 'src/VMCrate.Cli', '-c', 'Release', '--self-contained', 'false',
        '-o', $cli, '-m:1', '--disable-build-servers', '-p:AppHostDotNetSearch=AppRelative', '-p:AppHostRelativeDotNet=../runtime')
    & $dotnet @cliArgs
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }

    $queueHost = Join-Path $stage 'host'
    $hostArgs = @('publish', 'src/VMCrate.Host', '-c', 'Release', '--self-contained', 'false',
        '-o', $queueHost, '-m:1', '--disable-build-servers', '-p:AppHostDotNetSearch=AppRelative', '-p:AppHostRelativeDotNet=../runtime')
    & $dotnet @hostArgs
    if ($LASTEXITCODE -ne 0) { throw 'Remote host publish failed.' }

    $privateRuntime = Join-Path $stage 'runtime'
    foreach ($relative in @('dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt',
        "host/fxr/$runtimeVersion", "shared/Microsoft.NETCore.App/$runtimeVersion",
        "shared/Microsoft.WindowsDesktop.App/$runtimeVersion", "shared/Microsoft.AspNetCore.App/$runtimeVersion")) {
        $destination = Join-Path $privateRuntime $relative
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        Copy-Item -LiteralPath (Join-Path $dotnetRoot $relative) -Destination $destination -Recurse
    }
    Copy-Item -LiteralPath 'README.md', 'CONTRIBUTING.md', 'SECURITY.md' -Destination $stage
    Copy-Item -LiteralPath 'docs', 'examples' -Destination $stage -Recurse -Force
    foreach ($required in @('VMCrate.exe', 'cli/VMCrate.Cli.exe', 'host/VMCrate.Host.exe',
        'host/HyperV/Invoke-HyperV.ps1', 'HyperV/DiskRestore.ps1', 'HyperV/WorkingDiskTransfer.ps1',
        'HyperV/Invoke-HyperV.ps1', 'HyperV/ProxyTrace.ps1', 'HyperV/Isolation.ps1',
        'HyperV/WindowsInventory.ps1', 'examples/recipes.json', 'examples/remote-host.json',
        'docs/compatibility.md', 'docs/remote-queue.md', 'runtime/dotnet.exe',
        "runtime/shared/Microsoft.WindowsDesktop.App/$runtimeVersion/PresentationFramework.dll")) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $required) -PathType Leaf)) {
            throw "Incomplete release archive ($required)."
        }
    }

    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $stagedZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $digest = (Get-FileHash -LiteralPath $stagedZip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($stagedHash, "$digest  $zipName$([Environment]::NewLine)", [Text.Encoding]::ASCII)
    if ($SigningThumbprint) {
        $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$SigningThumbprint"
        New-CrateManifestSignature -Manifest $stagedHash -Destination $stagedSignature -Certificate $certificate
    }
    [IO.File]::Move($stagedZip, $zip, $true)
    [IO.File]::Move($stagedHash, "$zip.sha256", $true)
    if ($SigningThumbprint) {
        [IO.File]::Move($stagedSignature, "$zip.sha256.p7s", $true)
        Write-Output "$zip.sha256.p7s"
    }
    Write-Output $zip
    Write-Output "$zip.sha256"
} finally {
    Pop-Location
    foreach ($file in @($stagedZip, $stagedHash, $stagedSignature)) {
        if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file }
    }
    if (Test-Path -LiteralPath $stage -PathType Container) {
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        if ($resolvedStage -ne $stage -or (Split-Path -Parent $resolvedStage) -ne $output) {
            throw 'Unexpected archive staging path.'
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
