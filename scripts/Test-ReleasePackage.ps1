#Requires -Version 7.0
[CmdletBinding()]
param([string]$Directory = 'artifacts/releases')

$ErrorActionPreference = 'Stop'
$archives = @(Get-ChildItem -LiteralPath $Directory -Filter 'VMCrate-*.zip' -File)
if ($archives.Count -ne 1) { throw 'Expected one VMCrate release archive.' }
$archive = $archives[0]
$digest = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-Content -LiteralPath "$($archive.FullName).sha256" -Raw).TrimEnd() -cne "$digest  $($archive.Name)") {
    throw 'The packaged ZIP does not match its checksum.'
}

$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = [IO.Path]::GetFullPath((Join-Path $temp ("vmcrate-smoke-" + [Guid]::NewGuid().ToString('N'))))
if (-not $scratch.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $scratch) -notmatch '^vmcrate-smoke-[a-f0-9]{32}$') { throw 'Invalid smoke test directory.' }

try {
    [IO.Compression.ZipFile]::ExtractToDirectory($archive.FullName, $scratch)
    $cli = Join-Path $scratch 'cli/VMCrate.Cli.exe'
    & $cli validate (Join-Path $scratch 'examples/two-node-lab.json')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged CLI could not validate its example.' }
    & $cli validate-recipes (Join-Path $scratch 'examples/recipes.json')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged CLI could not validate its recipes.' }
    $desktop = Start-Process -FilePath (Join-Path $scratch 'VMCrate.exe') -ArgumentList '--release-smoke' -WindowStyle Hidden -PassThru
    if (-not $desktop.WaitForExit(30000)) {
        $desktop.Kill()
        $desktop.WaitForExit()
        throw 'Packaged desktop exceeded its startup time limit.'
    }
    if ($desktop.ExitCode -ne 0) { throw 'Packaged desktop failed to load.' }
    Write-Output 'PASS ZIP checksum, portable CLI, recipes and WPF startup'
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
