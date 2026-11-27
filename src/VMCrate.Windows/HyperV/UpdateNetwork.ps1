# The priority range identifies the rules managed by VMCrate on the builder's adapter.
function Get-CrateUpdateRules($Proxy) {
    @{ Action = 'Deny'; Direction = 'Inbound'; RemoteIPAddress = '0.0.0.0/0'; Weight = 18200 }
    @{ Action = 'Deny'; Direction = 'Outbound'; RemoteIPAddress = '0.0.0.0/0'; Weight = 18201 }
    @{ Action = 'Deny'; Direction = 'Inbound'; RemoteIPAddress = '::/0'; Weight = 18202 }
    @{ Action = 'Deny'; Direction = 'Outbound'; RemoteIPAddress = '::/0'; Weight = 18203 }
    @{
        Action = 'Allow'; Direction = 'Outbound'; RemoteIPAddress = ('{0}/32' -f $Proxy.address)
        Weight = 18204; RemotePort = [string]$Proxy.port; Protocol = 'TCP'; Stateful = $true
    }
}

function Clear-CrateUpdateRules($Adapter) {
    @(Get-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $Adapter |
        Where-Object { $_.Weight -in (18200..18204) }) |
        Remove-VMNetworkAdapterExtendedAcl -Confirm:$false
}

function Get-CrateUpdateSwitch($Location) {
    $proxy = $Location.machine.proxy
    if ($null -eq $proxy -or $Location.machine.templateId -or $Location.machine.disposable -or
        $Location.machine.blueprintId -or [int]$proxy.port -lt 1 -or [int]$proxy.port -gt 65535) {
        throw 'error.updateBuilder'
    }
    if (Test-CrateExpiredProxy $proxy) { throw 'error.updateBuilder' }
    $switches = @(Get-VMSwitch | Where-Object Id -EQ ([guid]$proxy.switchId))
    if ($switches.Count -ne 1 -or [string]$switches[0].SwitchType -ne 'Internal') {
        throw 'error.updateSwitch'
    }
    $switch = $switches[0]
    $networks = @(Get-CrateSwitchNetworks $switch)
    $natRanges = @(Get-NetNat | ForEach-Object { Get-CrateIpv4Range $_.InternalIPInterfaceAddressPrefix })
    foreach ($network in $networks) {
        $range = Get-CrateIpv4Range $network
        foreach ($nat in $natRanges) {
            if ($range -and $nat.First -le $range.First -and $nat.Last -ge $range.Last) {
                return $switch
            }
        }
    }
    throw 'error.updateSwitch'
}

function Test-CrateExpiredProxy($Proxy) {
    $expiry = $Proxy.PSObject.Properties['expiresUtc']
    $null -ne $expiry -and $expiry.Value -and
        [datetimeoffset]::Parse([string]$expiry.Value, [Globalization.CultureInfo]::InvariantCulture) -le [datetimeoffset]::UtcNow
}

function Close-CrateExpiredUpdate($Vm, $Location) {
    if ($Location.machine.network -ne 'Updates' -or $Location.machine.templateId -or
        $Location.machine.disposable -or $Location.machine.blueprintId -or
        -not (Test-CrateExpiredProxy $Location.machine.proxy)) {
        throw 'error.updateBuilder'
    }
    $adapters = @(Get-VMNetworkAdapter -VM $Vm)
    if ($adapters.Count -ne 1) { throw 'error.layout' }
    if ($adapters[0].SwitchName) {
        Disconnect-VMNetworkAdapter -VMNetworkAdapter $adapters[0]
    }
    Clear-CrateUpdateRules $adapters[0]
    $Location.machine.network = 'Offline'
    $Location.machine.proxy = $null
    if (-not (Test-CrateNetwork $Vm $Location)) { throw 'error.updateAcl' }
}

function Test-CrateUpdateRules($Adapter, $Proxy) {
    $actual = @(Get-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $Adapter)
    $expected = @(Get-CrateUpdateRules $Proxy)
    if ($actual.Count -ne $expected.Count) { return $false }
    foreach ($rule in $expected) {
        $found = @($actual | Where-Object Weight -EQ $rule.Weight)
        if ($found.Count -ne 1) { return $false }
        $item = $found[0]
        if ([string]$item.Direction -ne $rule.Direction -or [string]$item.Action -ne $rule.Action -or
            $item.RemoteIPAddress -ne $rule.RemoteIPAddress -or
            $item.LocalIPAddress -notin @($null, '', 'ANY', '*', '0.0.0.0/0', '::/0') -or
            $item.LocalPort -notin @($null, '', 'ANY', '*', '0', '0-65535')) { return $false }
        if ($rule.Action -eq 'Allow') {
            if ($item.RemotePort -ne $rule.RemotePort -or $item.Protocol -notin 'TCP', '6' -or -not $item.Stateful) {
                return $false
            }
        } elseif ($item.RemotePort -notin @($null, '', 'ANY', '*', '0', '0-65535') -or
            $item.Protocol -notin @($null, '', 'ANY', '*', '0', '256')) { return $false }
    }
    $true
}

function Connect-CrateUpdateNetwork($Adapter, $Location) {
    $switch = Get-CrateUpdateSwitch $Location
    if (@(Get-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $Adapter).Count -ne 0) {
        throw 'error.updateAcl'
    }
    try {
        foreach ($rule in @(Get-CrateUpdateRules $Location.machine.proxy)) {
            Add-VMNetworkAdapterExtendedAcl -VMNetworkAdapter $Adapter @rule | Out-Null
        }
        if (-not (Test-CrateUpdateRules $Adapter $Location.machine.proxy)) {
            throw 'error.updateAcl'
        }
        Connect-VMNetworkAdapter -VMNetworkAdapter $Adapter -VMSwitch $switch
    } catch {
        # Installation is done while disconnected. An incomplete policy never opens egress.
        $current = @(Get-VMNetworkAdapter -VMName $Adapter.VMName | Where-Object Name -EQ $Adapter.Name)
        if ($current.Count -eq 1 -and $current[0].SwitchName) {
            Disconnect-VMNetworkAdapter -VMNetworkAdapter $current[0]
        }
        Clear-CrateUpdateRules $Adapter
        throw
    }
}
