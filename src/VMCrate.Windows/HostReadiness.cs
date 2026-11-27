using System.Runtime.InteropServices;
using VMCrate.Core;

namespace VMCrate.Windows;

public sealed partial class PowerShellHypervisor
{
    public Task<HostLoad> ReadHostLoadAsync(string workspace) => InvokeAsync<HostLoad>("HostLoad",
        new { volume = Path.GetPathRoot(LocalPaths.FullPath(workspace))!.TrimEnd(Path.DirectorySeparatorChar) }, TimeSpan.FromSeconds(15));

    public async Task<HostReadiness> ReadHostReadinessAsync()
    {
        var windows11X64 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
            RuntimeInformation.OSArchitecture == Architecture.X64;
        var sandbox = OfflineSandbox.CanLaunch;
        var build = Environment.OSVersion.Version.Build;
        try
        {
            var info = await InvokeAsync<ReadinessReply>("HostReadiness", new { });
            var capacity = info.Capacity;
            return new(info.HyperV, windows11X64, sandbox, info.Detail,
                new(build, capacity?.Edition, capacity?.FirmwareVirtualization, capacity?.RestartRequired,
                    capacity?.LogicalProcessors, capacity?.FreeMemoryBytes));
        }
        catch (LabException error)
        {
            return new(HyperVReadiness.Unavailable, windows11X64, sandbox, error.Detail,
                new(build, null, null, null, null, null));
        }
    }

    private sealed record ReadinessReply(HyperVReadiness HyperV, string? Detail, CapacityReply? Capacity);
    private sealed record CapacityReply(string? Edition, bool? FirmwareVirtualization, bool? RestartRequired,
        int? LogicalProcessors, long? FreeMemoryBytes);
}
