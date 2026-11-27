function Get-CrateRecoveryInventory([guid]$WorkspaceId) {
    $scope = 'VMCrate/v1/{0:N}/' -f $WorkspaceId
    $registered = @(Get-VM)
    $owned = @(
        foreach ($vm in $registered) {
            $id = [guid]::Empty
            $notes = [string]$vm.Notes
            if ($notes.StartsWith($scope, [StringComparison]::Ordinal) -and
                [guid]::TryParseExact($notes.Substring($scope.Length), 'N', [ref]$id) -and
                $notes -ceq ($scope + $id.ToString('N')) -and $vm.Name -eq ('VMCrate-{0:N}' -f $id)) {
                $id
            }
        }
    )
    $segments = @(
        foreach ($switch in @(Get-VMSwitch | Where-Object SwitchType -EQ 'Private')) {
            $id = [guid]::Empty
            $notes = [string]$switch.Notes
            if ($notes.StartsWith($scope, [StringComparison]::Ordinal) -and
                [guid]::TryParseExact($notes.Substring($scope.Length), 'N', [ref]$id) -and
                $notes -ceq ($scope + $id.ToString('N')) -and $switch.Name -eq ('VMCrate-Private-{0:N}' -f $id)) {
                @{ id = $id; inUse = (Test-CrateSwitchInUse $switch $registered) }
            }
        }
    )
    @{ machines = $owned; segments = $segments }
}

function Test-CrateSwitchInUse($Switch, $Registered) {
    foreach ($vm in $Registered) {
        if (@(Get-VMNetworkAdapter -VM $vm | Where-Object SwitchId -EQ $Switch.Id).Count) { return $true }
        foreach ($snapshot in @(Get-VMSnapshot -VM $vm)) {
            if (@(Get-VMNetworkAdapter -VMSnapshot $snapshot | Where-Object SwitchId -EQ $Switch.Id).Count) { return $true }
        }
    }
    $false
}
