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

function Invoke-CrateDesktop([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $scratch 'VMCrate.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $desktop = [Diagnostics.Process]::Start($start)
    try {
        if (-not $desktop.WaitForExit(30000)) {
            $desktop.Kill()
            $desktop.WaitForExit()
            throw 'Packaged desktop exceeded its startup time limit.'
        }
        if ($desktop.ExitCode -ne 0) { throw 'Packaged desktop command failed.' }
    } finally { $desktop.Dispose() }
}

function Test-CrateBackgroundWorker {
    $workspace = Join-Path $scratch 'worker-fixture'
    $template = [Guid]::NewGuid()
    $baseDirectory = Join-Path $workspace "templates/$($template.ToString('N'))"
    $queueDirectory = Join-Path $workspace 'jobs'
    $null = New-Item -ItemType Directory -Path $baseDirectory, $queueDirectory -Force
    $disk = Join-Path $baseDirectory 'base.vhdx'
    [IO.File]::WriteAllText($disk, 'Release smoke fixture for template hash')
    $hash = (Get-FileHash -LiteralPath $disk -Algorithm SHA256).Hash.ToLowerInvariant()
    $catalog = @{ workspaceId = [Guid]::NewGuid(); version = 9; machines = @(); templates = @(
        @{ id = $template; name = 'CI fixture'; guest = 'Linux'; secureBoot = $false; sha256 = $hash }
    ) } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $workspace 'state.json'), $catalog)
    $path = Join-Path $queueDirectory 'queue.json'
    $running = @{ id = [Guid]::NewGuid(); request = @{ kind = 'Verify'; resource = $template };
        status = 'Running'; attempts = 1; updatedUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject @($running) -Depth 5))
    Invoke-CrateDesktop @('--local-jobs', $workspace)
    $job = @(Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)
    if ($job.Count -ne 1 -or $job[0].status -ne 'Succeeded' -or $job[0].attempts -ne 2) {
        throw 'Packaged background worker could not recover an interrupted verification.'
    }
}

try {
    [IO.Compression.ZipFile]::ExtractToDirectory($archive.FullName, $scratch)
    $cli = Join-Path $scratch 'cli/VMCrate.Cli.exe'
    & $cli validate (Join-Path $scratch 'examples/two-node-lab.json')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged CLI could not validate its example.' }
    & $cli validate-recipes (Join-Path $scratch 'examples/recipes.json')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged CLI could not validate its recipes.' }
    Invoke-CrateDesktop @('--release-smoke')
    Test-CrateBackgroundWorker
    Write-Output 'PASS ZIP checksum, portable CLI, recipes, WPF startup and background worker recovery'
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
