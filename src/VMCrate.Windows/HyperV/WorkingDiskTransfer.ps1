function Assert-CratePreparedChild([string]$Path, [string]$Parent) {
    Assert-CratePath $Path
    Assert-CratePath $Parent
    $disk = Get-VHD -Path $Path
    if ($disk.Attached -or $disk.VhdFormat -ne 'VHDX' -or $disk.VhdType -ne 'Differencing' -or
        -not $disk.ParentPath -or [IO.Path]::GetFullPath($disk.ParentPath) -ne [IO.Path]::GetFullPath($Parent)) {
        throw 'error.layout'
    }
}

function Set-CrateCopiedDiskParent([string]$Path, [string]$Parent) {
    Assert-CratePath $Path
    Assert-CratePath $Parent
    Set-VHD -Path $Path -ParentPath $Parent -Confirm:$false
    Assert-CratePreparedChild $Path $Parent
}

function Export-CrateWorkingDisk($Vm, $Request) {
    $location = $Request.location
    Assert-CrateOff $Vm
    if ($location.machine.disposable -or $location.machine.network -eq 'Updates' -or
        @(Get-CrateFindings $Vm $location).Count) {
        throw 'error.transferState'
    }

    $disk = (Get-CrateDisk $Vm $location).Path
    $destination = [string]$Request.destination
    Assert-CratePath $destination
    if ([IO.Path]::GetFileName($destination) -ne 'system.vhdx' -or [IO.File]::Exists($destination)) {
        throw 'error.layout'
    }

    # An open read handle prevents the VM from acquiring the disk for a write while it is being copied.
    $guard = [IO.File]::Open($disk, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Assert-CrateOff $Vm
        $required = if ($location.baseDisk) { [long](Get-VHD -Path $disk).Size + $guard.Length + 256MB } else { $guard.Length }
        $volume = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($destination))
        if ($volume.AvailableFreeSpace -lt $required) { throw 'error.buildSpace' }
        # Convert a detached copy. The registered disk remains attached to the stopped VM.
        $temporary = if ($location.baseDisk) {
            Join-Path ([IO.Path]::GetDirectoryName($destination)) ([guid]::NewGuid().ToString('N') + '.vhdx')
        } else { $destination }
        $output = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $guard.CopyTo($output)
            $output.Flush($true)
        } finally { $output.Dispose() }

        if ($location.baseDisk) {
            try {
                Set-VHD -Path $temporary -ParentPath $location.baseDisk
                Convert-VHD -Path $temporary -DestinationPath $destination -VHDType Dynamic -Confirm:$false
            } finally { [IO.File]::Delete($temporary) }
        }
        Assert-CrateBaseDisk $destination
    } finally { $guard.Dispose() }
}
