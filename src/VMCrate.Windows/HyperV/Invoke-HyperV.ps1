[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Observe', 'ValidateBaseDisk', 'Create', 'CaptureDisk', 'Start',
        'PowerOff', 'ResetDisk', 'Delete', 'SetNetwork', 'SetInbox', 'EjectInstaller', 'Console',
        'ObserveNetwork', 'ObserveIsolation', 'CheckGuestNetwork', 'CheckBoot', 'UsesMedia', 'RemoveEmptySegment', 'HostReadiness',
        'ObserveRecovery', 'CheckGuestProxy', 'RestoreDisks', 'ArmInstallerSeed', 'CloseExpiredUpdate', 'DisplayInfo', 'ConfigurePrivateAddress', 'WindowsInventory', 'ExportWorkingDisk', 'RegisterPreparedDisk')]
    [string]$Operation
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

. (Join-Path $PSScriptRoot 'Ownership.ps1')
. (Join-Path $PSScriptRoot 'Network.ps1')
. (Join-Path $PSScriptRoot 'Media.ps1')
. (Join-Path $PSScriptRoot 'Machine.ps1')
. (Join-Path $PSScriptRoot 'HostDiagnostics.ps1')
. (Join-Path $PSScriptRoot 'UpdateNetwork.ps1')
. (Join-Path $PSScriptRoot 'WorkflowChecks.ps1')
. (Join-Path $PSScriptRoot 'HostSetup.ps1')
. (Join-Path $PSScriptRoot 'RecoveryInventory.ps1')
. (Join-Path $PSScriptRoot 'ProxyTrace.ps1')
. (Join-Path $PSScriptRoot 'Isolation.ps1')
. (Join-Path $PSScriptRoot 'DiskRestore.ps1')
. (Join-Path $PSScriptRoot 'PrivateAddress.ps1')
. (Join-Path $PSScriptRoot 'WindowsInventory.ps1')
. (Join-Path $PSScriptRoot 'WorkingDiskTransfer.ps1')

try {
    $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
    if ($Operation -eq 'DisplayInfo') {
        Write-CrateReply @{ ok = $true; data = (Get-CrateDisplayInfo) }
        exit 0
    }
    if ($Operation -eq 'HostReadiness') {
        Write-CrateReply @{ ok = $true; data = (Get-CrateHostReadiness) }
        exit 0
    }
    try {
        if (-not (Import-CrateHyperV)) { throw 'The Windows Hyper-V management module is not installed.' }
        if ($Operation -ne 'ObserveNetwork') { $hostSettings = Get-VMHost }
    } catch {
        if ($Operation -ne 'ObserveNetwork') {
            Write-CrateReply @{ ok = $false; code = 'error.host'; detail = $_.Exception.Message }
            exit 0
        }
    }

    $data = switch ($Operation) {
        'Observe' { Get-CrateObservation $hostSettings $request.machines }
        'ObserveRecovery' { Get-CrateRecoveryInventory $request.workspaceId }
        'ObserveNetwork' { Get-CrateHostNetwork $request }
        'ObserveIsolation' { Get-CrateIsolation $hostSettings $request }
        'UsesMedia' { Test-CrateMediaInUse $request.path }
        'RemoveEmptySegment' { Remove-CrateEmptySegment $request; $true }
        'ValidateBaseDisk' { Assert-CrateBaseDisk $request.path; $true }
        'Create' { New-CrateMachine $request; $true }
        'RegisterPreparedDisk' { New-CrateMachine $request -Prepared; $true }
        'RestoreDisks' { Restore-CrateDisks $request.changes; $true }
        default { Invoke-CrateMachineAction $Operation $request }
    }
    Write-CrateReply @{ ok = $true; data = $data }
} catch {
    $detail = $_.Exception.Message
    $knownErrors = @('error.machine', 'error.foreign', 'error.off', 'error.layout',
        'error.baseDisk', 'error.isolation', 'error.network', 'error.localPath',
        'error.updateSwitch', 'error.updateAcl', 'error.updateBuilder', 'error.guestProbe',
        'error.proxyBuilder', 'error.proxyDirect', 'error.transferState', 'error.buildSpace')
    $code = if ($detail -in $knownErrors) { $detail } else { 'error.backend' }
    Write-CrateReply @{ ok = $false; code = $code; detail = $(if ($detail -ne $code) { $detail }) }
}
