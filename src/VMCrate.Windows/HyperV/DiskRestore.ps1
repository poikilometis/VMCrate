function Restore-CrateDisks($Changes) {
    $registered = @(Get-VM)
    foreach ($change in $Changes) {
        $location = $change.location
        $vm = Find-CrateVm $location $registered
        if ($null -eq $vm) { throw 'error.machine' }
        Assert-CrateOff $vm
        if (-not $location.baseDisk -or $location.machine.disposable -or
            @(Get-CrateFindings $vm $location).Count -or
            -not (Test-CrateArtifact $change.previous $location.diskDirectory '.vhdx') -or
            -not (Test-CrateArtifact $change.replacement $location.diskDirectory '.vhdx')) {
            throw 'error.layout'
        }

        $drive = Get-CrateDisk $vm $location
        if ($drive.Path -notin $change.previous, $change.replacement) { throw 'error.layout' }
        Assert-CratePath $change.replacement
        $candidate = Get-VHD -Path $change.replacement
        if ([string]$candidate.VhdFormat -ne 'VHDX' -or [string]$candidate.VhdType -ne 'Differencing' -or
            -not $candidate.ParentPath -or [IO.Path]::GetFullPath($candidate.ParentPath) -ne [IO.Path]::GetFullPath($location.baseDisk)) {
            throw 'error.layout'
        }
    }

    # The caller retains all paths and holds a durable restore plan while changes apply.
    foreach ($change in $Changes) {
        $vm = Find-CrateVm $change.location @(Get-VM)
        if ($null -eq $vm) { throw 'error.machine' }
        Assert-CrateOff $vm
        $drive = Get-CrateDisk $vm $change.location
        if ($drive.Path -eq $change.replacement) { continue }
        if ($drive.Path -ne $change.previous) { throw 'error.layout' }
        Set-VMHardDiskDrive -VMHardDiskDrive $drive -Path $change.replacement
        if ((Get-CrateDisk $vm $change.location).Path -ne $change.replacement) { throw 'error.layout' }
    }
}
