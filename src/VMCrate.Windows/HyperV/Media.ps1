function Get-CrateDvd($Vm, [int]$Location) {
    $drives = @(Get-VMDvdDrive -VM $Vm | Where-Object {
        $_.ControllerNumber -eq 0 -and $_.ControllerLocation -eq $Location
    })
    if ($drives.Count -ne 1) {
        throw 'error.layout'
    }
    $drives[0]
}

function Set-CrateInbox($Vm, $Location, $Iso) {
    $drive = Get-CrateDvd $Vm 2
    $previous = $drive.Path
    if ($Iso) {
        Assert-CratePath $Iso
        if (-not (Test-CrateArtifact $Iso $Location.inboxDirectory '.iso')) {
            throw 'error.layout'
        }
        Set-VMDvdDrive -VMDvdDrive $drive -Path $Iso
    } else {
        Set-VMDvdDrive -VMDvdDrive $drive -Path $null
    }
    if ($previous -ne $Iso) {
        Remove-CrateArtifact $previous $Location.inboxDirectory '.iso'
    }
}

function Remove-CrateInstaller($Vm) {
    $drive = Get-CrateDvd $Vm 1
    Set-VMDvdDrive -VMDvdDrive $drive -Path $null
}

function Set-CrateInstallerSeed($Vm, $Location, [string]$Iso) {
    Assert-CrateOff $Vm
    if ($Location.baseDisk -or $Location.machine.guest -ne 'Linux' -or $Location.machine.secureBoot -or
        $Location.machine.network -ne 'Offline' -or -not (Test-CrateNetwork $Vm $Location) -or
        -not (Get-CrateDvd $Vm 1).Path) {
        throw 'error.layout'
    }
    Set-CrateInbox $Vm $Location $Iso
    Set-VMFirmware -VM $Vm -FirstBootDevice (Get-CrateDvd $Vm 2)
}

function Test-CrateMedia($Vm, $Location) {
    try {
        $installer = Get-CrateDvd $Vm 1
        $inbox = Get-CrateDvd $Vm 2
        if ($installer.Path) {
            Assert-CratePath $installer.Path
            if ([IO.Path]::GetExtension($installer.Path) -ne '.iso' -or
                -not [IO.File]::Exists($installer.Path)) {
                return $false
            }
        }
        if ($inbox.Path) {
            Assert-CratePath $inbox.Path
            if (-not [IO.File]::Exists($inbox.Path)) {
                return $false
            }
        }
        return (@(Get-VMDvdDrive -VM $Vm).Count -eq 2 -and
            (-not $Location.baseDisk -or -not $installer.Path) -and
            (-not $inbox.Path -or (Test-CrateArtifact $inbox.Path $Location.inboxDirectory '.iso')))
    } catch {
        return $false
    }
}
