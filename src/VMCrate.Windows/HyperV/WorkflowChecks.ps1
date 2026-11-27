function Test-CrateBoot($Vm) {
    if ([string]$Vm.State -ne 'Running' -or (Get-CrateDvd $Vm 1).Path) {
        return $false
    }
    [string]$Vm.Heartbeat -like 'Ok*'
}

function Get-CrateGuestNetwork($Vm, $Request) {
    $location = $Request.location
    if ([string]$Vm.State -ne 'Running' -or $location.machine.guest -ne 'Windows' -or
        $location.machine.network -notin 'Offline', 'Private' -or
        -not (Test-CrateNetwork $Vm $location)) {
        throw 'error.guestProbe'
    }

    $secure = ConvertTo-SecureString ([string]$Request.password) -AsPlainText -Force
    try {
        $credential = [Management.Automation.PSCredential]::new([string]$Request.user, $secure)
        $result = Invoke-Command -VMId $Vm.Id -Credential $credential -ArgumentList ([string]$Request.peerAddress) -ScriptBlock {
            param([string]$Peer)
            $live = @(Get-NetAdapter | Where-Object Status -EQ 'Up')
            $routes = @(Get-NetRoute | Where-Object { $_.DestinationPrefix -in '0.0.0.0/0', '::/0' })
            $reachable = if ($Peer) { [bool](Test-Connection -ComputerName $Peer -Count 1 -Quiet -ErrorAction SilentlyContinue) }
            $dns = $null
            try {
                $indices = @($live | ForEach-Object ifIndex)
                $dns = @(Get-DnsClientServerAddress -ErrorAction Stop | Where-Object {
                    $_.InterfaceIndex -in $indices
                } | ForEach-Object { $_.ServerAddresses } | Where-Object { $_ }).Count
            } catch { }
            $inbox = $null
            try {
                $inbox = @(Get-Volume -ErrorAction Stop | Where-Object {
                    $_.DriveType -eq 'CD-ROM' -and $_.FileSystemLabel -eq 'VMCRATE_INBOX' -and $_.DriveLetter -and
                    (Test-Path -LiteralPath ('{0}:\' -f $_.DriveLetter) -PathType Container)
                }).Count -gt 0
            } catch { }
            [pscustomobject]@{
                ActiveAdapters = $live.Count; DefaultRoutes = $routes.Count; PeerReachable = $reachable
                IPv4DefaultRoutes = @($routes | Where-Object DestinationPrefix -EQ '0.0.0.0/0').Count
                IPv6DefaultRoutes = @($routes | Where-Object DestinationPrefix -EQ '::/0').Count
                DnsServers = $dns; InboxReadable = $inbox
            }
        }
        @{
            activeAdapters = $result.ActiveAdapters; defaultRoutes = $result.DefaultRoutes; peerReachable = $result.PeerReachable
            ipv4DefaultRoutes = $result.IPv4DefaultRoutes; ipv6DefaultRoutes = $result.IPv6DefaultRoutes
            dnsServers = $result.DnsServers; inboxReadable = $result.InboxReadable
        }
    } catch {
        # Remoting exceptions can contain local names. Do not echo the credential input or detailed remoting error.
        throw 'error.guestProbe'
    } finally {
        $secure.Dispose()
    }
}

function Test-CrateMediaInUse([string]$Path) {
    Assert-CratePath $Path
    foreach ($vm in @(Get-VM)) {
        if (@(Get-VMDvdDrive -VM $vm | Where-Object Path -EQ $Path).Count) { return $true }
        foreach ($snapshot in @(Get-VMSnapshot -VM $vm)) {
            if (@(Get-VMDvdDrive -VMSnapshot $snapshot | Where-Object Path -EQ $Path).Count) { return $true }
        }
    }
    $false
}

function Remove-CrateEmptySegment($Request) {
    $location = [pscustomobject]@{
        workspaceId = [guid]$Request.workspaceId
        machine = [pscustomobject]@{ privateSegment = [guid]$Request.segmentId }
    }
    if ($location.machine.privateSegment -eq [guid]::Empty) { throw 'error.network' }
    $switch = Get-CratePrivateSwitch $location
    if ($null -eq $switch) { return }
    if (Test-CrateSwitchInUse $switch @(Get-VM)) { return }
    Remove-VMSwitch -VMSwitch $switch -Force -Confirm:$false
}
