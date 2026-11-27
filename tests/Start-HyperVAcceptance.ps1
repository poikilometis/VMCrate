[CmdletBinding()]
param([Parameter(Mandatory)][string]$Fixtures, [switch]$CheckWindowsGuest)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$fixturesPath = (Resolve-Path -LiteralPath $Fixtures).Path
$runName = 'run-{0:yyyyMMdd-HHmmss}-{1}' -f [datetime]::Now, [guid]::NewGuid().ToString('N').Substring(0, 8)
$run = Join-Path $root "artifacts\hyperv-live\$runName"
$null = New-Item -ItemType Directory -Path $run
$log = Join-Path $run 'run.log'
Write-Host "Acceptance log $log"

Push-Location -LiteralPath $root
try {
    $acceptanceArgs = @('--hyperv-live', $run, $fixturesPath)
    if ($CheckWindowsGuest) { $acceptanceArgs += '--windows-guest' }
    & dotnet run --project tests/VMCrate.Tests -c Release -- @acceptanceArgs 2>&1 |
        Tee-Object -FilePath $log
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
