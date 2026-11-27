function Test-CrateOutboxDrive($Drive, [string]$Disk) {
    $Drive.ControllerType -eq 'SCSI' -and $Drive.ControllerNumber -eq 0 -and $Drive.ControllerLocation -eq 3 -and
        $Drive.Path -eq $Disk
}

function Get-CrateOutboxDisk($Location) {
    $disk = [string]$Location.outboxDisk
    Assert-CratePath $disk
    $parent = [IO.Path]::GetDirectoryName($disk)
    if ([IO.Path]::GetFileName($disk) -ne 'output.vhdx' -or [IO.Path]::GetFileName($parent) -notmatch '^[a-f0-9]{32}$' -or
        [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($parent)) -ne 'outbox') { throw 'error.outbox' }
    $disk
}

function Add-CrateOutbox($Vm, $Location, [long]$CreateBytes) {
    if ($Location.machine.network -ne 'Offline' -or -not $Location.machine.templateId -or
        $Vm.State -notin @('Off', 'Running') -or @(Get-CrateFindings $Vm $Location).Count) { throw 'error.outbox' }
    $disk = Get-CrateOutboxDisk $Location
    $attached = @(Get-VMHardDiskDrive -VM $Vm | Where-Object { Test-CrateOutboxDrive $_ $disk })
    if ($attached.Count -eq 1) { return }
    if ($attached.Count -gt 1) { throw 'error.outbox' }
    if (-not (Test-Path -LiteralPath $disk -PathType Leaf)) {
        if ($CreateBytes -lt 64MB -or $CreateBytes -gt 8GB) { throw 'error.outbox' }
        New-VHD -Path $disk -Dynamic -SizeBytes $CreateBytes | Out-Null
    }
    Assert-CrateBaseDisk $disk
    Add-VMHardDiskDrive -VM $Vm -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 3 -Path $disk
}

function Remove-CrateOutbox($Vm, $Location) {
    $disk = Get-CrateOutboxDisk $Location
    $attached = @(Get-VMHardDiskDrive -VM $Vm | Where-Object { Test-CrateOutboxDrive $_ $disk })
    if ($attached.Count -gt 1) { throw 'error.outbox' }
    if ($attached.Count -eq 1) { Remove-VMHardDiskDrive -VMHardDiskDrive $attached[0] -Confirm:$false }
}
