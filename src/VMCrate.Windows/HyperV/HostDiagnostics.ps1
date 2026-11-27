function Get-CrateIpv4Bytes([string]$Address) {
    if ($Address -notmatch '^[0-9]{1,3}(\.[0-9]{1,3}){3}$') { return }
    $octets = @($Address.Split('.') | ForEach-Object { [int]$_ })
    if (@($octets | Where-Object { $_ -gt 255 }).Count -ne 0) { return }
    ,([byte[]]$octets)
}

function Get-CrateIpv4Range([string]$Prefix) {
    $parts = $Prefix.Split('/')
    if ($parts.Count -ne 2) { return }
    $bytes = Get-CrateIpv4Bytes $parts[0]
    $length = 0
    if ($null -eq $bytes -or -not [int]::TryParse($parts[1], [ref]$length) -or $length -lt 0 -or $length -gt 32) { return }
    [array]::Reverse($bytes)
    $number = [uint64][BitConverter]::ToUInt32($bytes, 0)
    $size = [uint64][Math]::Pow(2, 32 - $length)
    $first = $number - ($number % $size)
    @{ First = $first; Last = $first + $size - 1 }
}

function Get-CrateSwitchNetworks($Switch) {
    $name = 'vEthernet ({0})' -f $Switch.Name
    $link = [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
        Where-Object Name -EQ $name | Select-Object -First 1
    if ($null -eq $link) { return }
    foreach ($address in $link.GetIPProperties().UnicastAddresses) {
        if ($address.Address.AddressFamily -eq 'InterNetwork') {
            '{0}/{1}' -f $address.Address, $address.PrefixLength
        }
    }
}

function Find-CrateRouteInterface([byte[]]$Address) {
    if (-not ('VMCrate.RouteNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
namespace VMCrate {
    public static class RouteNative {
        [DllImport("iphlpapi.dll")]
        public static extern uint GetBestInterface(uint address, out uint interfaceIndex);
    }
}
'@
    }
    $index = [uint32]0
    $value = [BitConverter]::ToUInt32($Address, 0)
    if ([VMCrate.RouteNative]::GetBestInterface($value, [ref]$index) -ne 0) { return }
    foreach ($link in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if (-not $link.Supports([Net.NetworkInformation.NetworkInterfaceComponent]::IPv4)) { continue }
        $ipv4 = $link.GetIPProperties().GetIPv4Properties()
        if ($ipv4 -and $ipv4.Index -eq $index) { return $link }
    }
}

function Get-CrateRouteConflicts($Switches) {
    $conflicts = @{}
    $routes = @(Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object { $_.DestinationPrefix -ne '0.0.0.0/0' })
    foreach ($switch in $Switches) {
        if ($switch.type -ne 'Internal') { continue }
        $alias = 'vEthernet ({0})' -f $switch.name
        foreach ($network in $switch.ipv4Networks) {
            $attached = Get-CrateIpv4Range $network
            if (-not $attached) { continue }
            foreach ($route in $routes) {
                if ($route.InterfaceAlias -eq $alias) { continue }
                $routed = Get-CrateIpv4Range $route.DestinationPrefix
                if ($routed -and $attached.First -le $routed.Last -and $routed.First -le $attached.Last) {
                    $conflicts['{0}/{1}/{2}' -f $switch.id, $route.InterfaceIndex, $route.DestinationPrefix] = $true
                }
            }
        }
    }
    $conflicts.Count
}

function Get-CrateHostNetwork($Request) {
    $switches = @()
    try {
        $switches = @(Get-VMSwitch | ForEach-Object {
            @{ id = $_.Id; name = $_.Name; type = [string]$_.SwitchType; ipv4Networks = @(Get-CrateSwitchNetworks $_) }
        })
    } catch { }

    $natCount = $null
    $overlap = $null
    try {
        $nats = @(Get-NetNat -ErrorAction Stop)
        $natCount = $nats.Count
        $ranges = @($nats | ForEach-Object { Get-CrateIpv4Range $_.InternalIPInterfaceAddressPrefix })
        $overlap = $false
        for ($j = 0; $j -lt $ranges.Count; $j++) {
            for ($k = $j + 1; $k -lt $ranges.Count; $k++) {
                if ($ranges[$j].First -le $ranges[$k].Last -and $ranges[$k].First -le $ranges[$j].Last) {
                    $overlap = $true
                }
            }
        }
    } catch { }

    $conflicts = $null
    try {
        if ($switches.Count -gt 0) { $conflicts = Get-CrateRouteConflicts $switches }
    } catch { }

    $name = $null
    $isTunnel = $null
    $source = $null
    $length = $null
    if ($Request.proxyAddress) {
        $address = Get-CrateIpv4Bytes $Request.proxyAddress
        if ($null -ne $address) {
            try {
                $selected = @(Find-NetRoute -RemoteIPAddress ($address -join '.') -ErrorAction Stop) |
                    Where-Object { $_.PSObject.Properties['IPAddress'] } | Select-Object -First 1
                if ($selected) {
                    $name = $selected.InterfaceAlias
                    $isTunnel = $name -match 'VPN|WireGuard|Wintun|Tailscale|TAP'
                    $source = $selected.IPAddress
                    $length = $selected.PrefixLength
                }
            } catch { }
            if (-not $name) {
                try {
                    $link = Find-CrateRouteInterface $address
                    if ($link) {
                        $name = $link.Name
                        $isTunnel = $link.NetworkInterfaceType -in 'Tunnel', 'Ppp' -or $name -match 'VPN|WireGuard|Wintun|Tailscale|TAP'
                    }
                } catch { }
            }
        }
    }
    @{
        routeKnown = $null -ne $name; interface = $name; isTunnel = $isTunnel
        switches = $switches; natCount = $natCount; overlappingNats = $overlap
        sourceAddress = $source; prefixLength = $length; routeConflicts = $conflicts
    }
}
