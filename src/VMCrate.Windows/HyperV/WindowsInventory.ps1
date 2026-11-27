function Get-CrateWindowsInventory($Vm, $Request) {
    $location = $Request.location
    if ($location.machine.guest -ne 'Windows' -or -not $location.machine.templateId -or
        $location.machine.disposable -or $location.machine.network -ne 'Offline' -or
        [string]$Vm.State -ne 'Running' -or -not (Test-CrateNetwork $Vm $location)) {
        throw 'error.guestProbe'
    }
    $secure = ConvertTo-SecureString ([string]$Request.password) -AsPlainText -Force
    try {
        $credential = [Management.Automation.PSCredential]::new([string]$Request.user, $secure)
        $entries = @(Invoke-Command -VMId $Vm.Id -Credential $credential -ScriptBlock {
            $os = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
            [pscustomobject]@{ Name = 'WindowsBuild'; Version = '{0}.{1}' -f $os.CurrentBuildNumber, $os.UBR }
            Get-HotFix -ErrorAction Stop | Select-Object -ExpandProperty HotFixID -Unique | ForEach-Object {
                if ($_ -match '^KB[0-9]+$') {
                    [pscustomobject]@{ Name = $_; Version = 'installed' }
                }
            }
        })
        @{
            version = 1; ecosystem = 'WindowsCBS'
            packages = @($entries | Where-Object {
                ($_.Name -eq 'WindowsBuild' -and $_.Version -match '^[0-9]+\.[0-9]+$') -or
                ($_.Name -match '^KB[0-9]+$' -and $_.Version -eq 'installed')
            } | ForEach-Object { @{ name = [string]$_.Name; version = [string]$_.Version } })
        }
    } catch { throw 'error.guestProbe' }
    finally { $secure.Dispose() }
}
