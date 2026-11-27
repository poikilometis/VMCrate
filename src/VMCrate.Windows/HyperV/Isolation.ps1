function Get-CrateIsolation($HostSettings, $Request) {
    $result = @{
        state = 'Missing'; switches = @(); enhancedSessionAllowed = [bool]$HostSettings.EnableEnhancedSessionMode
    }
    $registered = @(Get-VM)
    $location = $Request.location
    $vm = Find-CrateVm $location $registered
    if ($null -eq $vm) { return $result }

    $result.state = if ([string]$vm.State -in 'Off', 'Running', 'Saved') { [string]$vm.State } else { 'Other' }
    $adapters = @(Get-VMNetworkAdapter -VM $vm)
    $switches = @(Get-VMSwitch)
    $result.switches = @(
        foreach ($adapter in $adapters | Where-Object SwitchName) {
            $binding = $switches | Where-Object Id -EQ $adapter.SwitchId | Select-Object -First 1
            @{ name = $adapter.SwitchName; type = $(if ($binding) { [string]$binding.SwitchType }) }
        }
    )
    $result.networkPolicyMatches = Test-CrateNetwork $vm $location
    if ($location.machine.network -eq 'Private' -and $result.networkPolicyMatches) {
        try {
            $result.privateMembershipMatches = Test-CratePrivateMembership $adapters[0].SwitchId $registered $Request.peers
        } catch {
            $result.privateMembershipMatches = $null
        }
    }
    $services = @(Get-CrateRestrictedServices $vm)
    $result.fileCopyEnabled = Get-CrateServiceEnabled $services $crateFileCopyService
    $result.keyValueExchangeEnabled = Get-CrateServiceEnabled $services $crateKeyValueService
    $result.mediaLayoutMatches = Test-CrateMedia $vm $location
    if ($result.mediaLayoutMatches) {
        $result.inboxAttached = [bool](Get-CrateDvd $vm 2).Path
    }
    $result
}

function Get-CrateServiceEnabled($Services, [string]$Id) {
    $matching = @($Services | Where-Object { $_.Id -match $Id })
    if ($matching.Count -eq 1) { [bool]$matching[0].Enabled }
}

function Test-CratePrivateMembership([guid]$SwitchId, $Registered, $Peers) {
    # Compare VM identity as well as the switch. Matching a name cannot establish ownership.
    $expected = @{}
    foreach ($peer in $Peers) {
        try {
            $vm = Find-CrateVm $peer $Registered
            if ($vm) { $expected[[guid]$vm.Id] = $true }
        } catch {
            return $false
        }
    }
    $attached = @(Get-VMNetworkAdapter -All | Where-Object SwitchId -EQ $SwitchId)
    if ($attached.Count -eq 0) { return $false }
    foreach ($adapter in $attached) {
        $id = [guid]$adapter.VMId
        if ($adapter.IsManagementOs -or -not $expected.ContainsKey($id)) { return $false }
        $expected.Remove($id)
    }
    $expected.Count -eq 0
}
