function Get-CratePrivateSwitch($Location, [switch]$Create) {
    $id = [guid]$Location.workspaceId
    $notes = 'VMCrate/v1/{0:N}' -f $id
    $segment = $Location.machine.PSObject.Properties['privateSegment']
    if ($null -ne $segment -and $segment.Value) {
        $id = [guid]$segment.Value
        $notes = '{0}/{1:N}' -f $notes, $id
    }
    $name = 'VMCrate-Private-{0:N}' -f $id
    $switches = @(Get-VMSwitch | Where-Object Name -EQ $name)
    if ($switches.Count -eq 0) {
        if ($Create) {
            New-VMSwitch -Name $name -SwitchType Private -Notes $notes
        }
        return
    }
    if ($switches.Count -ne 1 -or [string]$switches[0].SwitchType -ne 'Private' -or
        $switches[0].Notes -cne $notes) {
        throw 'error.network'
    }
    $switches[0]
}

function Set-CrateNetwork($Vm, $Location) {
    Assert-CrateOff $Vm
    $adapters = @(Get-VMNetworkAdapter -VM $Vm)
    if ($adapters.Count -ne 1) {
        throw 'error.layout'
    }
    $adapter = $adapters[0]
    if ($adapter.SwitchName) {
        Disconnect-VMNetworkAdapter -VMNetworkAdapter $adapter
    }
    Clear-CrateUpdateRules $adapter
    Set-VMNetworkAdapter -VMNetworkAdapter $adapter -DhcpGuard On -RouterGuard On -MacAddressSpoofing Off -PortMirroring None
    if ($Location.machine.network -eq 'Private') {
        $private = Get-CratePrivateSwitch $Location -Create
        Connect-VMNetworkAdapter -VMNetworkAdapter $adapter -VMSwitch $private
    } elseif ($Location.machine.network -eq 'Updates') {
        Connect-CrateUpdateNetwork $adapter $Location
    }
}

function Test-CrateNetwork($Vm, $Location) {
    $adapters = @(Get-VMNetworkAdapter -VM $Vm)
    if ($adapters.Count -ne 1) {
        return $false
    }
    $adapter = $adapters[0]
    if ([string]$adapter.DhcpGuard -ne 'On' -or [string]$adapter.RouterGuard -ne 'On' -or
        [string]$adapter.MacAddressSpoofing -ne 'Off' -or [string]$adapter.PortMirroringMode -ne 'None') {
        return $false
    }
    if ($Location.machine.network -eq 'Offline') {
        return -not $adapter.SwitchName
    }
    try {
        if ($Location.machine.network -eq 'Updates') {
            $update = Get-CrateUpdateSwitch $Location
            return ($adapter.SwitchId -eq $update.Id -and (Test-CrateUpdateRules $adapter $Location.machine.proxy))
        }
        $private = Get-CratePrivateSwitch $Location
        return ($null -ne $private -and $adapter.SwitchId -eq $private.Id)
    } catch {
        return $false
    }
}

# Hyper-V exposes localized names. Component GUIDs are stable across host languages.
$crateFileCopyService = '6C09BB55-D683-4DA0-8931-C9BF705F6480'
$crateKeyValueService = '2A34B1C2-FD73-4043-8A5B-DD2159BC743F'

function Get-CrateRestrictedServices($Vm) {
    $ids = "$crateFileCopyService|$crateKeyValueService"
    Get-VMIntegrationService -VM $Vm | Where-Object { $_.Id -match $ids }
}
