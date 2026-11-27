using System.IO;
using VMCrate.Core;

namespace VMCrate.Tests;

internal sealed class FakeHypervisor : IHypervisor
{
    internal sealed record FakeMachine(MachineLocation Location, string Disk, string? Installer, string? Inbox = null,
        PowerState Power = PowerState.Off);
    internal Dictionary<Guid, FakeMachine> Machines { get; } = [];
    internal bool FailCreate { get; set; }
    internal bool FailAfterCreate { get; set; }
    internal bool NetworkDrift { get; set; }
    internal bool FailReset { get; set; }
    internal bool InterruptDiskRestore { get; set; }
    internal bool FailStart { get; set; }
    internal bool FailDelete { get; set; }
    internal string? FailCreateName { get; set; }
    internal bool FailObserve { get; set; }
    internal TaskCompletionSource? CreateStarted { get; set; }
    internal TaskCompletionSource? FinishCreate { get; set; }

    public Task<HostObservation> ObserveAsync(IReadOnlyList<MachineLocation> machines)
    {
        if (FailObserve)
            throw new LabException("error.host", "fixture host offline");

        var observations = machines.Select(vm =>
            new VmObservation(vm.Machine.Id,
                Machines.GetValueOrDefault(vm.Machine.Id)?.Power ?? PowerState.Missing, NetworkDrift ? ["audit.network"] : [], Machines.GetValueOrDefault(vm.Machine.Id)?.Disk)).ToArray();
        return Task.FromResult(new HostObservation(false, observations));
    }

    internal bool BootVerified { get; set; }
    internal bool MediaAttached { get; set; }
    internal List<Guid> RemovedSegments { get; } = [];

    internal OwnedSegment[] Segments { get; set; } = [];
    internal HostNetworkObservation ProxyRoute { get; set; } = new(true, "fixture tunnel", true, [], 1, false, "192.0.2.1", 24);
    internal ProbeCheck HostProxyTcp { get; set; } = ProbeCheck.Passed;

    public Task<RecoveryInventory> ObserveRecoveryAsync(Guid workspaceId) =>
        Task.FromResult(new RecoveryInventory(Machines.Values.Where(vm => vm.Location.WorkspaceId == workspaceId)
            .Select(vm => vm.Location.Machine.Id).ToArray(), Segments));

    public Task<HostNetworkObservation> ObserveNetworkAsync(string? proxyAddress) =>
        Task.FromResult(ProxyRoute);

    internal Guid[] IsolationPeers { get; private set; } = [];
    internal Action? GuestNetworkObserved { get; set; }
    internal int GuestNetworkCalls { get; private set; }

    public Task<HyperVIsolation> ObserveIsolationAsync(MachineLocation location, IReadOnlyList<MachineLocation> peers)
    {
        IsolationPeers = peers.Select(peer => peer.Machine.Id).ToArray();
        var vm = Machines.GetValueOrDefault(location.Machine.Id);
        var privateNetwork = location.Machine.Network == NetworkMode.Private;
        return Task.FromResult(new HyperVIsolation(vm?.Power ?? PowerState.Missing,
            vm is null ? null : !NetworkDrift, privateNetwork ? [new("fixture", "Private")] : [],
            privateNetwork ? !NetworkDrift : null, false, false, false, vm is not null, vm?.Inbox is not null));
    }

    public Task<GuestNetworkCheck> CheckGuestNetworkAsync(MachineLocation location, string user,
        string password, string? peerAddress)
    {
        GuestNetworkCalls++;
        GuestNetworkObserved?.Invoke();
        return Task.FromResult(new GuestNetworkCheck(1, 0, peerAddress is null ? null : true, 0, 0, 0, false));
    }

    public Task<PackageInventory> ReadWindowsInventoryAsync(MachineLocation location, string user, string password) =>
        Task.FromResult(new PackageInventory(1, "WindowsCBS", [new("WindowsBuild", "26200.1")]));

    internal string? LastPrivateAddress { get; private set; }
    public Task ConfigurePrivateAddressAsync(MachineLocation location, string address, string user, string password)
    {
        LastPrivateAddress = address;
        return Task.CompletedTask;
    }

    public Task<ProbeCheck> CheckHostProxyAsync(UpdateProxy proxy) => Task.FromResult(HostProxyTcp);

    public Task<ProxyGuestTrace> CheckGuestProxyAsync(MachineLocation location, ProxyTraceOptions options,
        string user, string password, string proxyUser, string proxyPassword) =>
        Task.FromResult(new ProxyGuestTrace(ProbeCheck.Passed, ProbeCheck.Failed, ProbeCheck.Failed,
            ProbeCheck.Unknown, 502, 0, 56));

    public Task<bool> CheckBootAsync(MachineLocation location) => Task.FromResult(BootVerified);
    public Task<bool> UsesMediaAsync(string path) => Task.FromResult(MediaAttached);
    public Task RemoveEmptySegmentAsync(Guid workspaceId, Guid segmentId)
    {
        RemovedSegments.Add(segmentId);
        return Task.CompletedTask;
    }

    public Task ValidateBaseDiskAsync(string path)
    {
        Checks.Expect(File.Exists(path), "Expected a copied disk for validation.");
        return Task.CompletedTask;
    }

    public async Task CreateAsync(MachineLocation location, string disk, string? installerIso, int diskGiB)
    {
        File.WriteAllText(disk, "disk");
        if (location.Machine.BlueprintId is null)
            Checks.Expect(location.Machine.Network == NetworkMode.Offline, "VM was not created offline.");
        CreateStarted?.TrySetResult();
        if (FinishCreate is not null)
            await FinishCreate.Task;
        if (FailCreate || location.Machine.Name == FailCreateName)
            throw new LabException("error.backend");
        Machines.Add(location.Machine.Id, new FakeMachine(location, disk, installerIso));
        if (FailAfterCreate)
            throw new LabException("error.backend");
    }

    public Task ExportWorkingDiskAsync(MachineLocation location, string destination, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        File.Copy(Find(location).Disk, destination);
        return Task.CompletedTask;
    }

    public Task RegisterPreparedDiskAsync(MachineLocation location, string disk)
    {
        if (FailCreate)
            throw new LabException("error.backend");
        Machines.Add(location.Machine.Id, new FakeMachine(location, disk, null));
        if (FailAfterCreate)
            throw new LabException("error.backend");
        return Task.CompletedTask;
    }

    public Task BindDiskParentAsync(string disk, string parent)
    {
        Checks.Expect(File.Exists(disk) && File.Exists(parent), "Expected both disks for a restored chain.");
        return Task.CompletedTask;
    }

    public Task<string> GetCaptureDiskAsync(MachineLocation location) => Task.FromResult(Find(location).Disk);
    public Task StartAsync(MachineLocation location) => FailStart
        ? throw new LabException("error.backend")
        : Update(location, vm => vm with { Power = PowerState.Running });
    public Task PowerOffAsync(MachineLocation location) => Update(location, vm => vm with { Power = PowerState.Off });
    internal int GuestShutdowns { get; private set; }
    public Task ShutdownAsync(MachineLocation location)
    {
        GuestShutdowns++;
        return Update(location, vm => vm with { Power = PowerState.Off });
    }
    public Task OpenConsoleAsync(MachineLocation location) => Task.CompletedTask;
    public Task ReconnectConsoleAsync(MachineLocation location) => Task.CompletedTask;
    public Task CloseDriftedUpdateAsync(MachineLocation location) => CloseExpiredUpdateAsync(location);
    public Task CloseConsoleAsync(MachineLocation location) => Task.CompletedTask;
    public Task AttachOutboxAsync(MachineLocation location, long createBytes)
    {
        var path = location.OutboxDisk ?? throw new LabException("error.outbox");
        if (createBytes > 0 && !File.Exists(path))
            File.WriteAllText(path, "outbox disk fixture");
        return Update(location, vm => vm with { Location = location });
    }
    public Task DetachOutboxAsync(MachineLocation location) => Machines.ContainsKey(location.Machine.Id)
        ? Update(location, vm => vm with { Location = location with { OutboxDisk = null } }) : Task.CompletedTask;
    public Task ArmInstallerSeedAsync(MachineLocation location, string isoPath) => SetInboxAsync(location, isoPath);
    public Task EjectInstallerAsync(MachineLocation location) => Update(location, vm => vm with { Installer = null });
    public Task SetInboxAsync(MachineLocation location, string? isoPath) =>
        Update(location, vm => vm with { Inbox = isoPath });
    public Task CloseExpiredUpdateAsync(MachineLocation location) =>
        Update(location, vm => vm with { Location = location with
        {
            Machine = location.Machine with { Network = NetworkMode.Offline, Proxy = null }
        } });

    public Task SetNetworkAsync(MachineLocation location) => Update(location, vm => vm with { Location = location });

    public Task DeleteAsync(MachineLocation location)
    {
        if (FailDelete)
            throw new LabException("error.backend");
        Machines.Remove(location.Machine.Id);
        return Task.CompletedTask;
    }

    public Task ResetDiskAsync(MachineLocation location, string replacementDisk)
    {
        if (FailReset)
            throw new LabException("error.backend");
        var previous = Find(location).Disk;
        File.WriteAllText(replacementDisk, "reset disk");
        File.Delete(previous);
        return Update(location, vm => vm with { Disk = replacementDisk, Inbox = null });
    }

    public async Task RestoreDisksAsync(IReadOnlyList<DiskRestoreChange> changes)
    {
        foreach (var change in changes)
        {
            var current = Find(change.Location).Disk;
            if (current == change.Replacement)
                continue;
            Checks.Expect(current == change.Previous && File.Exists(change.Replacement), "Restore attached an unexpected disk.");
            await Update(change.Location, vm => vm with { Disk = change.Replacement });
            if (InterruptDiskRestore)
                throw new LabException("error.backend");
        }
    }

    private FakeMachine Find(MachineLocation vm) => Machines[vm.Machine.Id];

    private Task Update(MachineLocation location, Func<FakeMachine, FakeMachine> update)
    {
        Machines[location.Machine.Id] = update(Find(location));
        return Task.CompletedTask;
    }
}

internal sealed class FakeInbox : IInboxWriter
{
    public void Write(IReadOnlyList<string> files, string stagingDirectory, string isoPath)
    {
        foreach (var path in files)
            File.Copy(path, Path.Combine(stagingDirectory, Path.GetFileName(path)));
        File.WriteAllLines(isoPath, files);
    }
}
