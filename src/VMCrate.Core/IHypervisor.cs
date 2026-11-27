namespace VMCrate.Core;

public interface IHypervisor
{
    Task<HostObservation> ObserveAsync(IReadOnlyList<MachineLocation> machines);
    Task<RecoveryInventory> ObserveRecoveryAsync(Guid workspaceId);
    Task<HostNetworkObservation> ObserveNetworkAsync(string? proxyAddress);
    Task<HyperVIsolation> ObserveIsolationAsync(MachineLocation location, IReadOnlyList<MachineLocation> peers);
    Task<GuestNetworkCheck> CheckGuestNetworkAsync(MachineLocation location, string user, string password, string? peerAddress);
    Task<PackageInventory> ReadWindowsInventoryAsync(MachineLocation location, string user, string password);
    Task ConfigurePrivateAddressAsync(MachineLocation location, string address, string user, string password);
    Task<ProbeCheck> CheckHostProxyAsync(UpdateProxy proxy);
    Task<ProxyGuestTrace> CheckGuestProxyAsync(MachineLocation location, ProxyTraceOptions options,
        string user, string password, string proxyUser, string proxyPassword);
    Task<bool> CheckBootAsync(MachineLocation location);
    Task<bool> UsesMediaAsync(string path);
    Task RemoveEmptySegmentAsync(Guid workspaceId, Guid segmentId);
    Task ValidateBaseDiskAsync(string path);
    Task CreateAsync(MachineLocation location, string disk, string? installerIso, int diskGiB);
    Task<string> GetCaptureDiskAsync(MachineLocation location);
    Task ExportWorkingDiskAsync(MachineLocation location, string destination, CancellationToken cancellation);
    Task RegisterPreparedDiskAsync(MachineLocation location, string disk);
    Task BindDiskParentAsync(string disk, string parent);
    Task StartAsync(MachineLocation location);
    Task ShutdownAsync(MachineLocation location);
    Task PowerOffAsync(MachineLocation location);
    Task ResetDiskAsync(MachineLocation location, string replacementDisk);
    Task RestoreDisksAsync(IReadOnlyList<DiskRestoreChange> changes);
    Task DeleteAsync(MachineLocation location);
    Task SetNetworkAsync(MachineLocation location);
    Task CloseExpiredUpdateAsync(MachineLocation location);
    Task CloseDriftedUpdateAsync(MachineLocation location);
    Task SetInboxAsync(MachineLocation location, string? isoPath);
    Task EjectInstallerAsync(MachineLocation location);
    Task ArmInstallerSeedAsync(MachineLocation location, string isoPath);
    Task OpenConsoleAsync(MachineLocation location);
    Task ReconnectConsoleAsync(MachineLocation location);
    Task CloseConsoleAsync(MachineLocation location);
    Task AttachOutboxAsync(MachineLocation location, long createBytes);
    Task DetachOutboxAsync(MachineLocation location);
}

public interface IInboxWriter
{
    void Write(IReadOnlyList<string> sourceFiles, string stagingDirectory, string isoPath);
}
