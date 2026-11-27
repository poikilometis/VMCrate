using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VMCrate.Core;

namespace VMCrate.Windows;

public sealed partial class PowerShellHypervisor : IHypervisor
{
    private static readonly string SystemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string PowerShell = Path.Combine(SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    private readonly string script = Path.Combine(AppContext.BaseDirectory, "HyperV", "Invoke-HyperV.ps1");

    public Task<HostObservation> ObserveAsync(IReadOnlyList<MachineLocation> machines) =>
        InvokeAsync<HostObservation>("Observe", new { machines });

    public Task<RecoveryInventory> ObserveRecoveryAsync(Guid workspaceId) =>
        InvokeAsync<RecoveryInventory>("ObserveRecovery", new { workspaceId });

    public Task<HostNetworkObservation> ObserveNetworkAsync(string? proxyAddress) =>
        InvokeAsync<HostNetworkObservation>("ObserveNetwork", new { proxyAddress });

    public Task<HyperVIsolation> ObserveIsolationAsync(MachineLocation location, IReadOnlyList<MachineLocation> peers) =>
        InvokeAsync<HyperVIsolation>("ObserveIsolation", new { location, peers });

    public Task<GuestNetworkCheck> CheckGuestNetworkAsync(MachineLocation location, string user,
        string password, string? peerAddress) =>
        InvokeAsync<GuestNetworkCheck>("CheckGuestNetwork", new { location, user, password, peerAddress });

    public Task<PackageInventory> ReadWindowsInventoryAsync(MachineLocation location, string user, string password) =>
        InvokeAsync<PackageInventory>("WindowsInventory", new { location, user, password });

    public Task ConfigurePrivateAddressAsync(MachineLocation location, string address, string user, string password) =>
        InvokeAsync<bool>("ConfigurePrivateAddress", new { location, address, user, password });

    public Task<bool> CheckBootAsync(MachineLocation location) => InvokeAsync<bool>("CheckBoot", new { location });
    public Task<bool> UsesMediaAsync(string path) => InvokeAsync<bool>("UsesMedia", new { path });
    public Task RemoveEmptySegmentAsync(Guid workspaceId, Guid segmentId) =>
        InvokeAsync<bool>("RemoveEmptySegment", new { workspaceId, segmentId });

    public Task ValidateBaseDiskAsync(string path) => InvokeAsync<bool>("ValidateBaseDisk", new { path });

    public Task CreateAsync(MachineLocation location, string disk, string? installerIso, int diskGiB) =>
        InvokeAsync<bool>("Create", new { location, disk, installerIso, diskGiB });

    public Task<string> GetCaptureDiskAsync(MachineLocation location) =>
        InvokeAsync<string>("CaptureDisk", new { location });

    public Task ExportWorkingDiskAsync(MachineLocation location, string destination, CancellationToken cancellation) =>
        InvokeAsync<bool>("ExportWorkingDisk", new { location, destination }, TimeSpan.FromHours(2), cancellation);

    public Task RegisterPreparedDiskAsync(MachineLocation location, string disk) =>
        InvokeAsync<bool>("RegisterPreparedDisk", new { location, disk, installerIso = (string?)null, diskGiB = 0 });

    public Task StartAsync(MachineLocation location) => InvokeAsync<bool>("Start", new { location });
    public Task PowerOffAsync(MachineLocation location) => InvokeAsync<bool>("PowerOff", new { location });
    public Task ResetDiskAsync(MachineLocation location, string replacementDisk) =>
        InvokeAsync<bool>("ResetDisk", new { location, replacementDisk });
    public Task RestoreDisksAsync(IReadOnlyList<DiskRestoreChange> changes) =>
        InvokeAsync<bool>("RestoreDisks", new { changes });

    public Task DeleteAsync(MachineLocation location) => InvokeAsync<bool>("Delete", new { location });
    public Task SetNetworkAsync(MachineLocation location) => InvokeAsync<bool>("SetNetwork", new { location });
    public Task CloseExpiredUpdateAsync(MachineLocation location) => InvokeAsync<bool>("CloseExpiredUpdate", new { location });
    public Task SetInboxAsync(MachineLocation location, string? isoPath) =>
        InvokeAsync<bool>("SetInbox", new { location, isoPath });
    public Task EjectInstallerAsync(MachineLocation location) =>
        InvokeAsync<bool>("EjectInstaller", new { location });

    public Task ArmInstallerSeedAsync(MachineLocation location, string isoPath) =>
        InvokeAsync<bool>("ArmInstallerSeed", new { location, isoPath });

    public async Task OpenConsoleAsync(MachineLocation location)
    {
        var name = await InvokeAsync<string>("Console", new { location });
        var start = new ProcessStartInfo(Path.Combine(SystemDirectory, "vmconnect.exe")) { UseShellExecute = false };
        start.ArgumentList.Add("localhost");
        start.ArgumentList.Add(name);
        using var process = Process.Start(start);
    }

    private async Task<T> InvokeAsync<T>(string operation, object request, TimeSpan? timeLimit = null,
        CancellationToken cancellation = default)
    {
        if (!File.Exists(PowerShell) || !File.Exists(script))
            throw new LabException("error.host");

        var start = new ProcessStartInfo(PowerShell)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        // A fresh Windows client defaults to Restricted. Honor any configured policy.
        var launcher = "if (-not (Get-ExecutionPolicy -List | Where-Object ExecutionPolicy -ne Undefined)) " +
            "{ Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned -Force }; " +
            $"& '{script.Replace("'", "''", StringComparison.Ordinal)}' -Operation '{operation}'";
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", launcher })
            start.ArgumentList.Add(argument);

        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new LabException("error.host");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(timeLimit ?? TimeSpan.FromMinutes(3));
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, JsonDefaults.Options).AsMemory(),
                timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException error)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            cancellation.ThrowIfCancellationRequested();
            throw new LabException("error.timeout", operation, error);
        }
        catch (IOException error)
        {
            throw new LabException("error.backend", errors.IsCompletedSuccessfully ? errors.Result : null, error);
        }

        var standardOutput = await output;
        var standardError = await errors;
        try
        {
            var reply = JsonSerializer.Deserialize<Reply<T>>(standardOutput, JsonDefaults.Options);
            if (reply is null)
                throw new LabException("error.backend", standardError);
            if (!reply.Ok)
                throw new LabException(reply.Code ?? "error.backend", reply.Detail);
            if (process.ExitCode != 0 || reply.Data is null)
                throw new LabException("error.backend", standardError);
            return reply.Data;
        }
        catch (JsonException error)
        {
            var detail = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            throw new LabException("error.backend", detail, error);
        }
    }

    private sealed record Reply<T>(bool Ok, T? Data, string? Code, string? Detail);
}
