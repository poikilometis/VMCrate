function Get-CrateGuestProxy($Vm, $Request) {
    $location = $Request.location
    if ([string]$Vm.State -ne 'Running' -or $location.machine.guest -ne 'Windows' -or
        $location.machine.network -ne 'Updates' -or -not (Test-CrateNetwork $Vm $location)) {
        throw 'error.proxyBuilder'
    }

    $secure = ConvertTo-SecureString ([string]$Request.password) -AsPlainText -Force
    try {
        $credential = [Management.Automation.PSCredential]::new([string]$Request.user, $secure)
        $parameters = @(
            [string]$location.machine.proxy.address, [int]$location.machine.proxy.port,
            [int]$Request.relayPort, [string]$Request.repository, [string]$Request.proxyUser, [string]$Request.proxyPassword,
            [bool]$Request.measureDownload, [int]$Request.sampleLimit
        )
        $result = Invoke-Command -VMId $Vm.Id -Credential $credential -ArgumentList $parameters -ScriptBlock {
            param([string]$Address, [int]$Port, [int]$Relay, [string]$Repository, [string]$ProxyUser,
                [string]$ProxyPassword, [bool]$MeasureDownload, [int]$SampleLimit)
            $ErrorActionPreference = 'Stop'

            function Test-ProxyPort([string]$IP, [int]$Number) {
                $client = [Net.Sockets.TcpClient]::new()
                try {
                    if ($client.ConnectAsync($IP, $Number).Wait(5000)) { return 1 }
                    return 2
                } catch {
                    return 2
                } finally {
                    $client.Dispose()
                }
            }

            $next = Find-NetRoute -RemoteIPAddress $Address -ErrorAction SilentlyContinue |
                Where-Object { $_.PSObject.Properties['NextHop'] } | Select-Object -First 1
            $route = if ($null -ne $next) { 1 } else { 2 }
            $proxyTcp = Test-ProxyPort $Address $Port
            $gateway = 0
            if ($null -ne $next -and $next.NextHop -ne '0.0.0.0') {
                $neighbor = Get-NetNeighbor -InterfaceIndex $next.InterfaceIndex -IPAddress $next.NextHop -ErrorAction SilentlyContinue |
                    Select-Object -First 1
                if ($null -ne $neighbor) {
                    $gateway = switch ([string]$neighbor.State) {
                        { $_ -in 'Unreachable', 'Incomplete' } { 2 }
                        { $_ -in 'Reachable', 'Stale', 'Delay', 'Probe', 'Permanent' } { 1 }
                        default { 0 }
                    }
                }
            }

            $relayTcp = 0
            $endpoint = 'http://{0}:{1}' -f $Address, $Port
            if ($Relay -gt 0) {
                $endpoint = 'http://127.0.0.1:{0}' -f $Relay
                $relayTcp = Test-ProxyPort '127.0.0.1' $Relay
            }

            $curl = Join-Path ([Environment]::SystemDirectory) 'curl.exe'
            if (-not (Test-Path -LiteralPath $curl -PathType Leaf)) {
                throw 'curl.exe is unavailable'
            }
            $info = [Diagnostics.ProcessStartInfo]::new($curl)
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            $info.RedirectStandardInput = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $mode = if ($MeasureDownload) { '--range 0-{0} --max-filesize {1}' -f ($SampleLimit - 1), $SampleLimit } else { '--head' }
            $info.Arguments = '-q --config - --proxy "{0}" --noproxy "" --proto "=https" {1} --silent --output NUL --connect-timeout 5 --max-time 15 --write-out "%{{http_connect}} %{{http_code}} %{{size_download}} %{{time_total}}" "{2}"' -f $endpoint, $mode, $Repository
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            try {
                [void]$process.Start()
                $output = $process.StandardOutput.ReadToEndAsync()
                $errors = $process.StandardError.ReadToEndAsync()
                if ($ProxyUser) {
                    $value = ('{0}:{1}' -f $ProxyUser, $ProxyPassword).Replace('\', '\\').Replace('"', '\"')
                    $process.StandardInput.WriteLine(('proxy-user = "{0}"' -f $value))
                }
                $process.StandardInput.Close()
                $connect = 0
                $http = 0
                $bytes = 0
                $elapsed = 0
                $exitCode = 28
                if ($process.WaitForExit(18000)) {
                    $exitCode = $process.ExitCode
                    if ($output.Result -match '^(\d{3}) (\d{3}) (\d+) (\d+(?:\.\d+)?)$') {
                        $connect = [int]$Matches[1]
                        $http = [int]$Matches[2]
                        $bytes = [Math]::Min([long]$Matches[3], $SampleLimit)
                        $elapsed = [long](1000 * [decimal]::Parse($Matches[4], [Globalization.CultureInfo]::InvariantCulture))
                    }
                } else {
                    $process.Kill()
                }
                @{
                    route = $route; gateway = $gateway; proxyTcp = $proxyTcp; relayTcp = $relayTcp
                    connect = $connect; http = $http; curlExit = $exitCode; downloadBytes = $bytes; elapsedMs = $elapsed
                }
            } finally {
                $process.Dispose()
            }
        }
        @{
            route = @('Unknown', 'Passed', 'Failed')[[int]$result.route]
            gateway = @('Unknown', 'Passed', 'Failed')[[int]$result.gateway]
            proxyTcp = @('Unknown', 'Passed', 'Failed')[[int]$result.proxyTcp]
            relayTcp = @('Unknown', 'Passed', 'Failed')[[int]$result.relayTcp]
            connect = $result.connect; http = $result.http; curlExit = $result.curlExit
            downloadBytes = $result.downloadBytes; elapsedMs = $result.elapsedMs
        }
    } catch {
        # Remoting errors can include submitted arguments. Keep credentials out of diagnostics.
        throw 'error.proxyDirect'
    } finally {
        $secure.Dispose()
    }
}
