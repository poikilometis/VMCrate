function New-CrateMachine($Request, [switch]$Prepared) {
    $location = $Request.location
    $name = Get-CrateName $location
    if (Find-CrateVm $location @(Get-VM)) {
        throw 'error.foreign'
    }
    Assert-CratePath $Request.disk
    if (-not (Test-CrateArtifact $Request.disk $location.diskDirectory '.vhdx')) {
        throw 'error.layout'
    }

    if ($Prepared) {
        if ($Request.installerIso -or $location.machine.disposable -or $location.machine.network -notin @('Offline', 'Private') -or
            ($location.machine.network -eq 'Private' -and (-not $location.baseDisk -or -not $location.machine.privateSegment))) {
            throw 'error.transferState'
        }
        if ($location.baseDisk) { Assert-CratePreparedChild $Request.disk $location.baseDisk }
        else { Assert-CrateBaseDisk $Request.disk }
    } elseif ($location.baseDisk) {
        Assert-CratePath $location.baseDisk
        New-VHD -Path $Request.disk -ParentPath $location.baseDisk -Differencing | Out-Null
    } else {
        Assert-CratePath $Request.installerIso
        New-VHD -Path $Request.disk -SizeBytes ([long]$Request.diskGiB * 1GB) -Dynamic | Out-Null
    }

    $configuration = Join-Path $location.directory 'hyperv'
    Assert-CratePath $configuration
    [void][IO.Directory]::CreateDirectory($configuration)
    $vm = $null
    try {
        $creation = @{
            Name = $name; Generation = 2; Path = $configuration; VHDPath = $Request.disk
            MemoryStartupBytes = [long]$location.machine.memoryGiB * 1GB
        }
        $vm = New-VM @creation
        $settings = @{
            VM = $vm; Notes = (Get-CrateMarker $location); ProcessorCount = $location.machine.cpuCount
            StaticMemory = $true; AutomaticCheckpointsEnabled = $false; CheckpointType = 'Disabled'
            AutomaticStartAction = 'Nothing'; AutomaticStopAction = 'TurnOff'
        }
        Set-VM @settings
        Set-CrateNetwork $vm $location
        Get-CrateRestrictedServices $vm | Where-Object Enabled | Disable-VMIntegrationService

        Get-VMDvdDrive -VM $vm | Remove-VMDvdDrive -Confirm:$false
        $installer = Add-VMDvdDrive -VM $vm -ControllerNumber 0 -ControllerLocation 1 -Passthru
        Add-VMDvdDrive -VM $vm -ControllerNumber 0 -ControllerLocation 2 | Out-Null
        if ($Request.installerIso) {
            Set-VMDvdDrive -VMDvdDrive $installer -Path $Request.installerIso
            Set-VMFirmware -VM $vm -FirstBootDevice $installer
        } elseif ($Prepared) {
            Set-VMFirmware -VM $vm -FirstBootDevice (Get-CrateDisk $vm $location)
        }
        $firmware = if ($location.machine.guest -eq 'Windows') { 'MicrosoftWindows' } else { 'MicrosoftUEFICertificateAuthority' }
        if ($location.machine.secureBoot) {
            Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate $firmware
        } else {
            Set-VMFirmware -VM $vm -EnableSecureBoot Off
        }
        if ($location.machine.guest -eq 'Windows') {
            Set-VMKeyProtector -VM $vm -NewLocalKeyProtector
            Enable-VMTPM -VM $vm
        }
    } catch {
        if ($null -ne $vm) {
            Remove-VM -VM $vm -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Get-CrateFindings($Vm, $Location) {
    if (-not (Test-CrateNetwork $Vm $Location)) { 'audit.network' }
    $blueprint = $Location.machine.PSObject.Properties['blueprintId']
    if ($null -ne $blueprint -and $blueprint.Value -and
        ($Vm.ProcessorCount -ne $Location.machine.cpuCount -or $Vm.DynamicMemoryEnabled -or
         $Vm.MemoryStartup -ne ([long]$Location.machine.memoryGiB * 1GB))) {
        'audit.resources'
    }
    if (@(Get-CrateRestrictedServices $Vm | Where-Object Enabled).Count) { 'audit.integration' }
    if ($Vm.AutomaticCheckpointsEnabled -or [string]$Vm.CheckpointType -ne 'Disabled' -or
        @(Get-VMSnapshot -VM $Vm).Count) { 'audit.checkpoints' }
    if (-not (Test-CrateMedia $Vm $Location)) { 'audit.media' }
    try {
        Get-CrateDisk $Vm $Location | Out-Null
    } catch {
        'audit.disk'
    }
}

function Get-CrateObservation($HostSettings, $Locations) {
    $registered = @(Get-VM)
    $rows = @(
        foreach ($location in $Locations) {
            try {
                $vm = Find-CrateVm $location $registered
                if ($null -eq $vm) {
                    @{ id = $location.machine.id; state = 'Missing'; findings = @('audit.missing') }
                    continue
                }
                $state = if ([string]$vm.State -in @('Off', 'Running', 'Saved')) { [string]$vm.State } else { 'Other' }
                $drives = @(Get-VMHardDiskDrive -VM $vm)
                $attached = if ($drives.Count -eq 1) { $drives[0].Path }
                @{ id = $location.machine.id; state = $state; findings = @(Get-CrateFindings $vm $location)
                    attachedDisk = $attached }
            } catch {
                @{ id = $location.machine.id; state = 'Unavailable'; findings = @('audit.unavailable') }
            }
        }
    )
    @{ enhancedSession = [bool]$HostSettings.EnableEnhancedSessionMode; machines = $rows }
}

function Reset-CrateDisk($Vm, $Location, [string]$Replacement) {
    Assert-CrateOff $Vm
    if (-not $Location.baseDisk -or @(Get-VMSnapshot -VM $Vm).Count -or
        -not (Test-CrateArtifact $Replacement $Location.diskDirectory '.vhdx')) {
        throw 'error.layout'
    }
    $drive = Get-CrateDisk $Vm $Location
    Assert-CratePath $Replacement
    $previous = $drive.Path
    New-VHD -Path $Replacement -ParentPath $Location.baseDisk -Differencing | Out-Null
    Set-CrateInbox $Vm $Location $null
    # Keep both disks until Hyper-V acknowledges the replacement.
    Set-VMHardDiskDrive -VMHardDiskDrive $drive -Path $Replacement
    Remove-CrateArtifact $previous $Location.diskDirectory '.vhdx'
}

function Invoke-CrateMachineAction([string]$Action, $Request) {
    $location = $Request.location
    $vm = Find-CrateVm $location @(Get-VM)
    if ($null -eq $vm) {
        if ($Action -in @('Delete', 'DetachOutbox')) { return $true }
        throw 'error.machine'
    }
    switch ($Action) {
        'CheckBoot' { return (Test-CrateBoot $vm) }
        'CheckGuestProxy' { return (Get-CrateGuestProxy $vm $Request) }
        'CheckGuestNetwork' { return (Get-CrateGuestNetwork $vm $Request) }
        'ConfigurePrivateAddress' { Set-CratePrivateAddress $vm $Request }
        'WindowsInventory' { return (Get-CrateWindowsInventory $vm $Request) }
        'ExportWorkingDisk' { Export-CrateWorkingDisk $vm $Request }
        'CaptureDisk' {
            Assert-CrateOff $vm
            if (@(Get-VMSnapshot -VM $vm).Count) { throw 'error.layout' }
            return (Get-CrateDisk $vm $location).Path
        }
        'Start' {
            Assert-CrateOff $vm
            if (@(Get-CrateFindings $vm $location).Count) { throw 'error.isolation' }
            Start-VM -VM $vm | Out-Null
        }
        'Shutdown' {
            if ($vm.State -ne 'Running' -or @(Get-CrateFindings $vm $location).Count) { throw 'error.guestRestart' }
            Stop-VM -VM $vm -Confirm:$false | Out-Null
        }
        'PowerOff' {
            if ([string]$vm.State -eq 'Saved') {
                Remove-VMSavedState -VM $vm -Confirm:$false
            } elseif ([string]$vm.State -ne 'Off') {
                Stop-VM -VM $vm -TurnOff -Force
            }
        }
        'ResetDisk' { Reset-CrateDisk $vm $location $Request.replacementDisk }
        'Delete' {
            Assert-CrateOff $vm
            Remove-VM -VM $vm -Force
        }
        'SetNetwork' { Set-CrateNetwork $vm $location }
        'CloseExpiredUpdate' { Close-CrateExpiredUpdate $vm $location }
        'CloseDriftedUpdate' { Close-CrateDriftedUpdate $vm $location }
        'SetInbox' { Set-CrateInbox $vm $location $Request.isoPath }
        'EjectInstaller' { Remove-CrateInstaller $vm }
        'ArmInstallerSeed' { Set-CrateInstallerSeed $vm $location $Request.isoPath }
        'Console' { return $vm.Name }
        'AttachOutbox' { Add-CrateOutbox $vm $location $Request.createBytes }
        'DetachOutbox' { Remove-CrateOutbox $vm $location }
    }
    $true
}
