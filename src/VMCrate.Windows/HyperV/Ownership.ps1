function Write-CrateReply($Reply) {
    [Console]::WriteLine(($Reply | ConvertTo-Json -Depth 12 -Compress))
}

function Get-CrateName($Location) {
    'VMCrate-{0:N}' -f [guid]$Location.machine.id
}

function Get-CrateMarker($Location) {
    'VMCrate/v1/{0:N}/{1:N}' -f [guid]$Location.workspaceId, [guid]$Location.machine.id
}

function Find-CrateVm($Location, $Candidates) {
    $name = Get-CrateName $Location
    $marker = Get-CrateMarker $Location
    $ownedCandidates = @($Candidates | Where-Object { $_.Name -eq $name -or $_.Notes -ceq $marker })
    if ($ownedCandidates.Count -gt 1 -or ($ownedCandidates.Count -eq 1 -and
        ($ownedCandidates[0].Name -ne $name -or $ownedCandidates[0].Notes -cne $marker))) {
        throw 'error.foreign'
    }
    if ($ownedCandidates.Count -eq 1) {
        $ownedCandidates[0]
    }
}

function Assert-CrateOff($Vm) {
    if ([string]$Vm.State -ne 'Off') {
        throw 'error.off'
    }
}

function Assert-CratePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -notmatch '^[A-Za-z]:\\[^:]*$') {
        throw 'error.localPath'
    }
    for ($part = $full; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if ((Test-Path -LiteralPath $part) -and
            ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'error.localPath'
        }
    }
}

function Test-CrateArtifact([string]$Path, [string]$Directory, [string]$Extension) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }
    $id = [guid]::Empty
    return ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path)) -eq [IO.Path]::GetFullPath($Directory) -and
        [IO.Path]::GetExtension($Path) -eq $Extension -and
        [guid]::TryParseExact([IO.Path]::GetFileNameWithoutExtension($Path), 'N', [ref]$id))
}

function Assert-CrateBaseDisk([string]$Path) {
    Assert-CratePath $Path
    $disk = Get-VHD -Path $Path
    if ([string]$disk.VhdFormat -ne 'VHDX' -or [string]$disk.VhdType -notin @('Dynamic', 'Fixed') -or
        $disk.ParentPath -or $disk.Attached) {
        throw 'error.baseDisk'
    }
}

function Get-CrateDisk($Vm, $Location) {
    $drives = @(Get-VMHardDiskDrive -VM $Vm)
    if ($drives.Count -ne 1 -or $drives[0].ControllerLocation -ne 0 -or
        $drives[0].ControllerNumber -ne 0 -or
        -not (Test-CrateArtifact $drives[0].Path $Location.diskDirectory '.vhdx')) {
        throw 'error.layout'
    }

    $drive = $drives[0]
    Assert-CratePath $drive.Path
    $image = Get-VHD -Path $drive.Path
    if ($Location.baseDisk) {
        Assert-CratePath $Location.baseDisk
        if (-not $image.ParentPath -or [IO.Path]::GetFullPath($image.ParentPath) -ne
            [IO.Path]::GetFullPath($Location.baseDisk)) {
            throw 'error.layout'
        }
    } elseif ($image.ParentPath) {
        throw 'error.layout'
    }
    $drive
}

function Remove-CrateArtifact([string]$Path, [string]$Directory, [string]$Extension) {
    if (Test-CrateArtifact $Path $Directory $Extension) {
        try {
            Assert-CratePath $Path
            [IO.File]::Delete($Path)
        } catch {
            # A locked artifact can be collected when its owning VM is removed.
        }
    }
}
