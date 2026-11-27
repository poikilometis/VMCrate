function Import-CrateHyperV {
    $systemModules = (Join-Path $PSHOME 'Modules\Hyper-V').TrimEnd('\') + '\'
    $module = Get-Module -ListAvailable -Name Hyper-V |
        Where-Object { $_.Path.StartsWith($systemModules, [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object Version -Descending | Select-Object -First 1
    if ($null -eq $module) { return $false }
    Import-Module $module.Path -ErrorAction Stop
    $true
}

function Get-CrateHostReadiness {
    $state = 'Unavailable'
    $detail = $null
    $capacity = Get-CrateHostCapacity
    try {
        if (-not (Import-CrateHyperV)) {
            $state = 'ModuleMissing'
        } elseif ((Get-Service -Name vmms -ErrorAction Stop).Status -ne 'Running') {
            $state = 'ServiceStopped'
        } else {
            Get-VMHost | Out-Null
            $state = 'Ready'
            if ($capacity.hypervisorPresent -eq $false) { $state = 'Unavailable' }
        }
    } catch {
        if ($_.CategoryInfo.Category -in 'PermissionDenied', 'SecurityError' -or
            $_.Exception -is [UnauthorizedAccessException] -or $_.Exception.HResult -eq -2147024891) {
            $state = 'AccessDenied'
        }
        $detail = $_.Exception.Message
    }
    @{ hyperV = $state; detail = $detail; capacity = $capacity }
}

function Get-CrateDisplayInfo {
    $video = $null
    try {
        $video = @(Get-CimInstance Win32_VideoController -Property Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution -OperationTimeoutSec 4 |
            ForEach-Object {
                @{ name = $_.Name; driver = $_.DriverVersion; width = $_.CurrentHorizontalResolution; height = $_.CurrentVerticalResolution }
            })
    } catch { }
    @{ monitors = 0; video = $video }
}

function Get-CrateHostCapacity {
    $capacity = @{
        edition = $null; hypervisorPresent = $null; firmwareVirtualization = $null
        restartRequired = $null; logicalProcessors = $null; freeMemoryBytes = $null
    }
    try {
        $windows = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name EditionID
        $capacity.edition = [string]$windows.EditionID
        $restartKeys = @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
        )
        $capacity.restartRequired = @($restartKeys | Where-Object { Test-Path -LiteralPath $_ }).Count -gt 0
    } catch { }
    try {
        $computer = Get-CimInstance Win32_ComputerSystem -Property HypervisorPresent,NumberOfLogicalProcessors -OperationTimeoutSec 4
        $capacity.hypervisorPresent = $computer.HypervisorPresent
        $capacity.logicalProcessors = $computer.NumberOfLogicalProcessors
        if ($computer.HypervisorPresent -eq $true) {
            $capacity.firmwareVirtualization = $true
        } elseif ($computer.HypervisorPresent -eq $false) {
            $cpu = @(Get-CimInstance Win32_Processor -Property VirtualizationFirmwareEnabled -OperationTimeoutSec 4)
            if ($cpu.Count -gt 0 -and @($cpu | Where-Object { $null -eq $_.VirtualizationFirmwareEnabled }).Count -eq 0) {
                $capacity.firmwareVirtualization = @($cpu | Where-Object { -not $_.VirtualizationFirmwareEnabled }).Count -eq 0
            }
        }
    } catch { }
    try {
        $os = Get-CimInstance Win32_OperatingSystem -Property FreePhysicalMemory -OperationTimeoutSec 4
        if ($null -ne $os.FreePhysicalMemory) { $capacity.freeMemoryBytes = [long]$os.FreePhysicalMemory * 1KB }
    } catch { }
    $capacity
}
