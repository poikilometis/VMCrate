function Set-CratePrivateAddress($Vm, $Request) {
    $location = $Request.location
    $address = Get-CrateIpv4Bytes $Request.address
    if ($null -eq $address -or $address[0] -ne 10 -or $address[1] -lt 208 -or $address[1] -gt 223 -or
        $address[3] -lt 10 -or $address[3] -gt 73 -or $location.machine.network -ne 'Private' -or
        $location.machine.guest -ne 'Windows' -or [string]$Vm.State -ne 'Running' -or
        -not (Test-CrateNetwork $Vm $location)) {
        throw 'error.guestProbe'
    }

    $mac = (Get-VMNetworkAdapter -VM $Vm).MacAddress
    $secure = ConvertTo-SecureString ([string]$Request.password) -AsPlainText -Force
    try {
        $credential = [Management.Automation.PSCredential]::new([string]$Request.user, $secure)
        $result = Invoke-Command -VMId $Vm.Id -Credential $credential -ArgumentList ($address -join '.'), $mac -ScriptBlock {
            param([string]$Address, [string]$Mac)
            $interfaces = @(Get-NetAdapter | Where-Object { $_.MacAddress.Replace('-', '') -eq $Mac })
            if ($interfaces.Count -ne 1) { return $false }
            $index = $interfaces[0].InterfaceIndex
            $routes = @(Get-NetRoute -ErrorAction Stop | Where-Object InterfaceIndex -EQ $index)
            if (@($routes | Where-Object { $_.DestinationPrefix -in '0.0.0.0/0', '::/0' }).Count) { return $false }

            $existing = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object {
                $_.InterfaceIndex -eq $index -and $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '0.0.0.0'
            })
            if ($existing.Count) {
                return ($existing.Count -eq 1 -and $existing[0].IPAddress -eq $Address -and $existing[0].PrefixLength -eq 24)
            }

            New-NetIPAddress -InterfaceIndex $index -IPAddress $Address -PrefixLength 24 -AddressFamily IPv4 -ErrorAction Stop | Out-Null
            $true
        }
        if ($result -ne $true) { throw 'error.guestProbe' }
    } catch { throw 'error.guestProbe' }
    finally { $secure.Dispose() }
}
