# Exercises the adapter functions with local fixtures. No Hyper-V commands reach the host.
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$adapterScripts = Join-Path $PSScriptRoot '..\src\VMCrate.Windows\HyperV'
foreach ($name in @('Ownership.ps1', 'Network.ps1', 'Media.ps1', 'Machine.ps1', 'UpdateNetwork.ps1', 'HostDiagnostics.ps1', 'WorkflowChecks.ps1', 'RecoveryInventory.ps1', 'ProxyTrace.ps1', 'HostSetup.ps1', 'Isolation.ps1', 'DiskRestore.ps1', 'WorkingDiskTransfer.ps1')) {
    . (Join-Path $adapterScripts $name)
}

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Error([string]$Code, [scriptblock]$Action) {
    try {
        & $Action
    } catch {
        if ($_.Exception.Message -eq $Code) { return }
        throw
    }
    throw "Expected $Code."
}

$script:rules = @()
$script:injectAclFailure = $false
function Get-VMNetworkAdapterExtendedAcl($VMNetworkAdapter) { $script:rules }
function Add-VMNetworkAdapterExtendedAcl($VMNetworkAdapter, $Action, $Direction, $RemoteIPAddress, $Weight,
    $RemotePort, $Protocol, [bool]$Stateful = $false) {
    if ($script:injectAclFailure -and $Weight -eq 18204) { throw 'fixture.aclFailed' }
    $script:rules += [pscustomobject]@{
        Action = $Action; Direction = $Direction; RemoteIPAddress = $RemoteIPAddress; Weight = $Weight
        RemotePort = $RemotePort; Protocol = $Protocol; Stateful = $Stateful; LocalIPAddress = $null; LocalPort = $null
    }
}
function Remove-VMNetworkAdapterExtendedAcl {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(ValueFromPipeline)]$InputObject)
    process { $script:rules = @($script:rules | Where-Object { $_.Weight -ne $InputObject.Weight }) }
}
function Get-CrateSwitchNetworks($Switch) { '10.30.0.1/24' }
function Get-NetNat { [pscustomobject]@{ InternalIPInterfaceAddressPrefix = '10.30.0.0/24' } }
function Get-NetRoute {
    [CmdletBinding()]
    param($AddressFamily)
    [pscustomobject]@{ DestinationPrefix = '10.30.0.0/24'; InterfaceAlias = 'vEthernet (fixture internal)'; InterfaceIndex = 30 }
    [pscustomobject]@{ DestinationPrefix = '10.30.0.0/25'; InterfaceAlias = 'fixture VPN'; InterfaceIndex = 32 }
    [pscustomobject]@{ DestinationPrefix = '0.0.0.0/0'; InterfaceAlias = 'fixture VPN'; InterfaceIndex = 32 }
}
function Get-VM { $script:fixtureVm }
$script:foreignNic = $null
function Get-VMNetworkAdapter($Vm, [switch]$All) {
    $script:fixtureNic
    if ($All -and $script:foreignNic) { $script:foreignNic }
}
function Get-VMSwitch { $script:fixtureSwitch }
function New-VMSwitch($Name, $SwitchType, $Notes) {
    $script:fixtureSwitch = [pscustomobject]@{ Name = $Name; SwitchType = $SwitchType; Notes = $Notes; Id = [guid]::NewGuid() }
    $script:fixtureSwitch
}
function Get-VMSnapshot($Vm) { }
function Get-VMIntegrationService($Vm) { $script:fixtureServices }
function Get-VMHardDiskDrive($Vm) { $script:fixtureDrive }
function Get-VHD($Path) {
    if ([IO.Path]::GetFileName($Path) -eq 'system.vhdx') {
        return [pscustomobject]@{ VhdFormat = 'VHDX'; VhdType = 'Dynamic'; ParentPath = $null; Attached = $false; Size = 16MB }
    }
    [pscustomobject]@{ VhdFormat = 'VHDX'; VhdType = 'Differencing'; ParentPath = $script:baseDisk; Attached = $false; Size = 16MB }
}
function Set-VHD {
    [CmdletBinding(SupportsShouldProcess)]
    param($Path, $ParentPath, [switch]$IgnoreIdMismatch)
    Assert-Check (-not $IgnoreIdMismatch) 'A copied disk was rebound without checking its parent identity.'
    Assert-Check ($ParentPath -eq $script:baseDisk -and [IO.File]::Exists($Path)) 'Temporary disk parent changed.'
}
function Convert-VHD {
    [CmdletBinding(SupportsShouldProcess)]
    param($Path, $DestinationPath, $VHDType)
    Assert-Check ($Path -ne $script:fixtureDrive.Path -and $VHDType -eq 'Dynamic') 'Export tried to convert the original attachment.'
    [IO.File]::Copy($Path, $DestinationPath)
}
function New-VHD($Path, $ParentPath, [switch]$Differencing) { [IO.File]::WriteAllText($Path, 'new disk') }
function Get-VMDvdDrive($Vm) { $script:fixtureDvd }
function Set-VMDvdDrive($VMDvdDrive, $Path) { $VMDvdDrive.Path = $Path }
function Set-VMHardDiskDrive($VMHardDiskDrive, $Path) {
    if ($script:failDiskSwap) { throw 'fixture.swap' }
    $VMHardDiskDrive.Path = $Path
}
function Start-VM($Vm) { $script:startCalls++ }
function Stop-VM {
    [CmdletBinding(SupportsShouldProcess)]
    param($Vm, [switch]$TurnOff, [switch]$Force)
    Assert-Check (-not $TurnOff -and -not $Force) 'Guest shutdown attempted a hard power-off.'
    $Vm.State = 'Off'
}
function Connect-VMNetworkAdapter($VMNetworkAdapter, $VMSwitch) {
    $VMNetworkAdapter.SwitchName = $VMSwitch.Name
    $VMNetworkAdapter.SwitchId = $VMSwitch.Id
}
function Disconnect-VMNetworkAdapter($VMNetworkAdapter) {
    $VMNetworkAdapter.SwitchName = $null
    $VMNetworkAdapter.SwitchId = [guid]::Empty
}
function Set-VMNetworkAdapter($VMNetworkAdapter, $DhcpGuard, $RouterGuard, $MacAddressSpoofing, $PortMirroring) {
    $VMNetworkAdapter.DhcpGuard = $DhcpGuard
    $VMNetworkAdapter.RouterGuard = $RouterGuard
    $VMNetworkAdapter.MacAddressSpoofing = $MacAddressSpoofing
    $VMNetworkAdapter.PortMirroringMode = $PortMirroring
}

$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixtureRoot = Join-Path $tempParent ('vmcrate-adapter-' + [guid]::NewGuid().ToString('N'))
$disks = Join-Path $fixtureRoot 'disks'
$inbox = Join-Path $fixtureRoot 'inbox'
$null = [IO.Directory]::CreateDirectory($disks)
$null = [IO.Directory]::CreateDirectory($inbox)
$script:baseDisk = Join-Path $fixtureRoot 'base.vhdx'
$location = [pscustomobject]@{
    machine = [pscustomobject]@{ id = [guid]::NewGuid(); network = 'Offline' }
    workspaceId = [guid]::NewGuid()
    baseDisk = $script:baseDisk
    diskDirectory = $disks
    inboxDirectory = $inbox
}
$request = [pscustomobject]@{ location = $location }
$script:fixtureVm = [pscustomobject]@{
    Id = [guid]::NewGuid()
    Name = Get-CrateName $location
    Notes = Get-CrateMarker $location
    State = 'Off'
    AutomaticCheckpointsEnabled = $false
    CheckpointType = 'Disabled'
    MemoryStartup = 4GB
    ProcessorCount = 2
    DynamicMemoryEnabled = $false
}
$script:fixtureNic = [pscustomobject]@{
    SwitchName = $null; SwitchId = [guid]::Empty
    Name = 'Network Adapter'; VMName = $script:fixtureVm.Name; VMId = $script:fixtureVm.Id; IsManagementOs = $false
    DhcpGuard = 'On'; RouterGuard = 'On'; MacAddressSpoofing = 'Off'; PortMirroringMode = 'None'
}
$script:fixtureSwitch = [pscustomobject]@{
    Name = 'VMCrate-Private-{0:N}' -f $location.workspaceId
    Notes = 'VMCrate/v1/{0:N}' -f $location.workspaceId
    Id = [guid]::NewGuid(); SwitchType = 'Private'
}
$script:fixtureServices = @(
    @{ Id = 'Microsoft:123\6C09BB55-D683-4DA0-8931-C9BF705F6480'; Enabled = $false; Name = 'Dienst' }
    @{ Id = 'Microsoft:123\2A34B1C2-FD73-4043-8A5B-DD2159BC743F'; Enabled = $false; Name = 'Daten' }
)
$oldDisk = Join-Path $disks ([guid]::NewGuid().ToString('N') + '.vhdx')
$script:fixtureDrive = [pscustomobject]@{ ControllerNumber = 0; ControllerLocation = 0; Path = $oldDisk }
$script:fixtureDvd = @(
    [pscustomobject]@{ ControllerNumber = 0; ControllerLocation = 1; Path = $null }
    [pscustomobject]@{ ControllerNumber = 0; ControllerLocation = 2; Path = $null }
)
$script:failDiskSwap = $false
$script:startCalls = 0

try {
    [IO.File]::WriteAllText($oldDisk, 'old disk')
    $foreign = [pscustomobject]@{ Name = $script:fixtureVm.Name; Notes = 'another lab' }
    Assert-Error 'error.foreign' { Find-CrateVm $location @($foreign) }
    $renamed = [pscustomobject]@{ Name = 'renamed'; Notes = $script:fixtureVm.Notes }
    Assert-Error 'error.foreign' { Find-CrateVm $location @($renamed) }
    Assert-Check ((Find-CrateVm $location @($script:fixtureVm)) -eq $script:fixtureVm) 'Owned VM was not found.'
    Write-Output 'PASS ownership markers'

    Assert-Check (Test-CrateNetwork $script:fixtureVm $location) 'Offline adapter was rejected.'
    $location.machine.network = 'Private'
    Set-CrateNetwork $script:fixtureVm $location
    Assert-Check (Test-CrateNetwork $script:fixtureVm $location) 'Valid private switch was rejected.'
    $request | Add-Member -NotePropertyName peers -NotePropertyValue @($location)
    $hostSettings = @{ EnableEnhancedSessionMode = $true }
    $observation = Get-CrateIsolation $hostSettings $request
    Assert-Check ($observation.privateMembershipMatches -and $observation.switches[0].type -eq 'Private' -and
        $observation.enhancedSessionAllowed -and $observation.fileCopyEnabled -eq $false -and
        $observation.keyValueExchangeEnabled -eq $false) 'Configured isolation evidence was incomplete.'
    $script:foreignNic = [pscustomobject]@{ SwitchId = $script:fixtureNic.SwitchId; VMId = [guid]::NewGuid(); IsManagementOs = $false }
    $observation = Get-CrateIsolation $hostSettings $request
    Assert-Check ($observation.networkPolicyMatches -and -not $observation.privateMembershipMatches) 'An unowned attachment passed membership verification.'
    $script:foreignNic = $null
    Write-Output 'PASS isolation observations identify a foreign private-switch attachment'
    $script:fixtureSwitch.SwitchType = 'External'
    Assert-Error 'error.network' { Set-CrateNetwork $script:fixtureVm $location }
    Assert-Check (-not $script:fixtureNic.SwitchName) 'Adapter was not disconnected.'
    $location.machine.network = 'Offline'
    Write-Output 'PASS private mode refuses external switches'

    $segmentId = [guid]::NewGuid()
    $location.machine | Add-Member -NotePropertyName privateSegment -NotePropertyValue $segmentId
    $location.machine.network = 'Private'
    Set-CrateNetwork $script:fixtureVm $location
    $expectedSegmentName = 'VMCrate-Private-{0:N}' -f $segmentId
    Assert-Check ($script:fixtureSwitch.Name -eq $expectedSegmentName -and (Test-CrateNetwork $script:fixtureVm $location)) 'Blueprint switch has an unexpected identity.'
    $inventory = Get-CrateRecoveryInventory $location.workspaceId
    Assert-Check ($inventory.machines.Count -eq 1 -and $inventory.segments.Count -eq 1 -and
        $inventory.segments[0].inUse) 'Recovery did not recognize the owned and attached segment.'
    $script:fixtureSwitch.Notes = 'different workspace'
    Assert-Check ((Get-CrateRecoveryInventory $location.workspaceId).segments.Count -eq 0) 'Recovery listed another workspace switch.'
    Write-Output 'PASS recovery inventory scopes resources and identifies attachments'
    Assert-Check (-not (Test-CrateNetwork $script:fixtureVm $location)) 'A switch owned by another workspace was accepted.'
    $location.machine.privateSegment = $null
    $location.machine.network = 'Offline'
    Set-CrateNetwork $script:fixtureVm $location
    Write-Output 'PASS blueprint segments have independent switch ownership'

    $location.machine | Add-Member -NotePropertyMembers @{ blueprintId = 'lab'; memoryGiB = 4; cpuCount = 2 }
    $script:fixtureVm.ProcessorCount = 3
    Assert-Check (@(Get-CrateFindings $script:fixtureVm $location) -contains 'audit.resources') 'Blueprint hardware drift was missed.'
    Assert-Error 'error.isolation' { Invoke-CrateMachineAction 'Start' $request }
    $script:fixtureVm.ProcessorCount = 2
    Write-Output 'PASS blueprint startup blocks hardware drift'

    $null = Invoke-CrateMachineAction 'Start' $request
    $script:fixtureServices[1].Enabled = $true
    Assert-Error 'error.isolation' { Invoke-CrateMachineAction 'Start' $request }
    Assert-Check ($script:startCalls -eq 1) 'Unsafe VM reached Start-VM.'
    $script:fixtureServices[1].Enabled = $false
    $script:fixtureVm.State = 'Running'
    Assert-Error 'error.off' { Set-CrateNetwork $script:fixtureVm $location }
    $null = Invoke-CrateMachineAction 'Shutdown' $request
    Assert-Check ($script:fixtureVm.State -eq 'Off') 'Guest shutdown did not finish.'
    Write-Output 'PASS graceful shutdown invokes guest integration'
    $script:fixtureVm.State = 'Off'
    Write-Output 'PASS start checks guest integration and network changes require power off'

    $script:fixtureSwitch.SwitchType = 'Internal'
    $location.machine.blueprintId = $null
    $location.machine | Add-Member -NotePropertyMembers @{
        proxy = [pscustomobject]@{ switchId = $script:fixtureSwitch.Id; address = '8.8.4.4'; port = 8443 }
        templateId = $null
        disposable = $false
    }
    $location.machine.network = 'Updates'
    $script:injectAclFailure = $true
    Assert-Error 'fixture.aclFailed' { Set-CrateNetwork $script:fixtureVm $location }
    Assert-Check (-not $script:fixtureNic.SwitchName -and $script:rules.Count -eq 0) 'Failed proxy ACL update connected the builder.'
    $script:injectAclFailure = $false
    Set-CrateNetwork $script:fixtureVm $location
    Assert-Check ($script:rules.Count -eq 5 -and (Test-CrateNetwork $script:fixtureVm $location)) 'Update policy verification failed.'
    $location.machine | Add-Member -NotePropertyName guest -NotePropertyValue 'Windows'
    $script:fixtureVm.State = 'Running'
    function Invoke-Command($VMId, $Credential, $ArgumentList, [scriptblock]$ScriptBlock) {
        Assert-Check ($ArgumentList[0] -eq '8.8.4.4' -and $ArgumentList[2] -eq 2080) 'Incorrect guest probe inputs.'
        Assert-Check (-not $ArgumentList[6] -and $ArgumentList[7] -eq 262144) 'Incorrect sample settings.'
        @{ route = 1; gateway = 2; proxyTcp = 2; relayTcp = 1; connect = 502; http = 0; curlExit = 56; downloadBytes = 0; elapsedMs = 614 }
    }
    $trace = Get-CrateGuestProxy $script:fixtureVm ([pscustomobject]@{
        location = $location; user = 'fixture'; password = 'fixture'; relayPort = 2080
        repository = 'https://catalog.update.microsoft.com/'; proxyUser = ''; proxyPassword = ''; measureDownload = $false; sampleLimit = 262144
    })
    Assert-Check ($trace.gateway -eq 'Failed' -and $trace.connect -eq 502 -and $trace.http -eq 0 -and $trace.elapsedMs -eq 614) 'Guest diagnostics lost layer results.'
    $script:fixtureVm.State = 'Off'
    Write-Output 'PASS guest proxy diagnostics preserve route and CONNECT observations'
    $lease = $location.machine.proxy
    $lease | Add-Member -NotePropertyName expiresUtc -NotePropertyValue ([datetimeoffset]::UtcNow.AddMinutes(1).ToString('o'))
    Assert-Error 'error.updateBuilder' { Close-CrateExpiredUpdate $script:fixtureVm $location }
    $lease.expiresUtc = [datetimeoffset]::UtcNow.AddMinutes(-1).ToString('o')
    $script:fixtureVm.State = 'Running'
    Close-CrateExpiredUpdate $script:fixtureVm $location
    Assert-Check ($script:fixtureVm.State -eq 'Running' -and -not $script:fixtureNic.SwitchName -and
        $script:rules.Count -eq 0 -and $location.machine.network -eq 'Offline') 'Expired proxy was not closed without a power change.'
    Write-Output 'PASS expired update leases detach and clear ACLs'
    $script:fixtureVm.State = 'Off'
    $lease.expiresUtc = $null
    $location.machine.proxy = $lease
    $location.machine.network = 'Updates'
    Set-CrateNetwork $script:fixtureVm $location
    $script:rules[4].RemoteIPAddress = '0.0.0.0/0'
    Assert-Check (-not (Test-CrateNetwork $script:fixtureVm $location)) 'A broader ACL was accepted.'
    $location.machine.network = 'Offline'
    Set-CrateNetwork $script:fixtureVm $location
    Assert-Check (-not $script:fixtureNic.SwitchName -and $script:rules.Count -eq 0) 'Leaving Proxy retained the adapter or ACL.'
    Write-Output 'PASS scoped update ACLs and fail-closed rollback'

    $routeSwitch = @{ type = 'Internal'; name = 'fixture internal'; id = $script:fixtureSwitch.Id; ipv4Networks = @('10.30.0.1/24') }
    Assert-Check ((Get-CrateRouteConflicts @($routeSwitch)) -eq 1) 'Overlapping host route not detected.'
    Write-Output 'PASS route conflicts ignore the default and owning switch routes'

    $replacement = Join-Path $disks ([guid]::NewGuid().ToString('N') + '.vhdx')
    $script:failDiskSwap = $true
    Assert-Error 'fixture.swap' { Reset-CrateDisk $script:fixtureVm $location $replacement }
    Assert-Check ((Test-Path -LiteralPath $oldDisk) -and $script:fixtureDrive.Path -eq $oldDisk) 'Failed reset lost the current disk.'
    $script:failDiskSwap = $false
    Reset-CrateDisk $script:fixtureVm $location $replacement
    Assert-Check ($script:fixtureDrive.Path -eq $replacement -and -not (Test-Path -LiteralPath $oldDisk)) 'Reset did not commit.'
    Assert-Error 'error.layout' { Reset-CrateDisk $script:fixtureVm $location (Join-Path $fixtureRoot 'foreign.vhdx') }
    Write-Output 'PASS failed swaps preserve the old disk'
    $copy = Join-Path $disks ([guid]::NewGuid().ToString('N') + '.vhdx')
    [IO.File]::WriteAllText($copy, 'restore copy')
    $changes = @(@{ location = $location; previous = $replacement; replacement = $copy })
    Restore-CrateDisks $changes
    Restore-CrateDisks $changes
    Assert-Check ($script:fixtureDrive.Path -eq $copy -and (Test-Path -LiteralPath $replacement)) 'Restore lost the original or failed to resume.'
    Write-Output 'PASS restore attachment retries preserve the previous disk'

    $transferDirectory = Join-Path $fixtureRoot 'transfer'
    $null = [IO.Directory]::CreateDirectory($transferDirectory)
    $transferDisk = Join-Path $transferDirectory 'system.vhdx'
    $transfer = @{ location = $location; destination = $transferDisk }
    $script:fixtureVm.State = 'Running'
    Assert-Error 'error.off' { Export-CrateWorkingDisk $script:fixtureVm $transfer }
    $script:fixtureVm.State = 'Off'
    Export-CrateWorkingDisk $script:fixtureVm $transfer
    Assert-Check ([IO.File]::ReadAllText($transferDisk) -eq 'restore copy' -and
        $script:fixtureDrive.Path -eq $copy -and ([IO.Directory]::GetFiles($transferDirectory)).Count -eq 1) 'Export changed the source or left a staging disk.'
    Write-Output 'PASS working disk export preserves the registered source'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $tempParent -or
        [IO.Path]::GetFileName($resolved) -notlike 'vmcrate-adapter-*') {
        throw 'Unexpected fixture directory.'
    }
    [IO.Directory]::Delete($resolved, $true)
}
