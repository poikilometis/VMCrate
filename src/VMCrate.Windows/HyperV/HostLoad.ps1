function Get-CrateHostLoad($Request) {
    $volume = [string]$Request.volume
    if ($volume -notmatch '^[A-Za-z]:$') { throw 'error.localPath' }
    $cpu = $null
    $latency = $null
    try {
        $cpu = (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name = '_Total'").PercentProcessorTime
    } catch { }
    try {
        $filter = "Name = '$volume'"
        $first = Get-CimInstance Win32_PerfRawData_PerfDisk_LogicalDisk -Filter $filter
        Start-Sleep -Milliseconds 350
        $last = Get-CimInstance Win32_PerfRawData_PerfDisk_LogicalDisk -Filter $filter
        $count = [double]$last.AvgDisksecPerTransfer_Base - $first.AvgDisksecPerTransfer_Base
        $delta = [double]$last.AvgDisksecPerTransfer - $first.AvgDisksecPerTransfer
        if ($count -gt 0 -and $delta -ge 0 -and $last.Frequency_PerfTime -gt 0) {
            $latency = 1000 * $delta / $last.Frequency_PerfTime / $count
        }
    } catch { }
    @{ cpuPercent = $cpu; diskLatencyMs = $latency; observedUtc = [datetimeoffset]::UtcNow.ToString('o') }
}
