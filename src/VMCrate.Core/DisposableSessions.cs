namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task StartAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var machine = FindMachine(state, id);
        await StartMachineAsync(state, machine);
    }

    private async Task StartMachineAsync(LabState state, Machine machine)
    {
        ValidateStart(state, machine);
        if (await CloseOnRouteDriftAsync(state, machine))
            throw new LabException("error.proxyDrift");
        await ObserveBuildProxyAsync(state, machine, BuildProxySource.Start);
        ValidateStart(state, machine);
        var location = store.Location(state, machine);
        LocalPaths.RequireOrdinaryPath(location.Directory);
        await hypervisor.StartAsync(location);
        if (machine.Disposable)
        {
            // Arming follows a confirmed boot. An ambiguous boot never triggers automatic removal.
            state.Machines[state.Machines.IndexOf(machine)] = machine with { SessionStarted = true };
        }
        RecordOperation(state, machine.Id, RecordedOperation.Start);
        store.Save(state);
    }

    public async Task RestartGuestAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var machine = FindMachine(state, id);
        if (machine.Disposable)
            throw new LabException("error.guestRestart");
        ValidateStart(state, machine);
        var location = store.Location(state, machine);
        LocalPaths.RequireOrdinaryPath(location.Directory);
        if (await ObserveOneAsync(location) is not { State: PowerState.Running, Findings.Length: 0 })
            throw new LabException("error.guestRestart");

        await hypervisor.ShutdownAsync(location);
        if (await ObserveOneAsync(location) is not { State: PowerState.Off, Findings.Length: 0 })
            throw new LabException("error.guestRestart");
        await StartMachineAsync(state, machine);
    }

    private static void ValidateStart(LabState state, Machine machine)
    {
        RequireNoDiskRestore(state, machine.Id);
        if (state.Provisions.Any(item => item.Id == machine.Id))
            throw new LabException("error.recoveryRequired");
        if (machine.Proxy?.IsExpired(DateTimeOffset.UtcNow) == true)
            throw new LabException("status.proxyExpired");
        if (machine.SessionStarted)
            throw new LabException("error.sessionEnded");
    }

    public async Task EndDisposableAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var machine = FindMachine(state, id);
        if (!machine.Disposable)
            throw new LabException("error.sessionOnly");

        var location = store.Location(state, machine);
        LocalPaths.RequireOrdinaryPath(location.Directory);
        await hypervisor.PowerOffAsync(location);
        RecordOperation(state, id, RecordedOperation.PowerOff);
        await DeleteMachineCoreAsync(state, location);
    }

    public async Task<LabSnapshot> RefreshAsync()
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        LabException? leaseFailure = null;
        foreach (var machine in state.Machines.Where(vm => vm.Proxy?.IsExpired(DateTimeOffset.UtcNow) == true).ToArray())
        {
            try { await ExpireUpdateLeaseAsync(state, machine); }
            catch (LabException error) { leaseFailure ??= error; }
        }

        var snapshot = await ObserveStateAsync(state);
        var ending = state.Machines.Where(vm => vm.Disposable && vm.SessionStarted &&
            snapshot.StateOf(vm.Id) == PowerState.Off).ToArray();

        LabException? failure = leaseFailure;
        foreach (var machine in ending)
        {
            try
            {
                // The adapter confirms ownership and power state again immediately before removal.
                await DeleteMachineCoreAsync(state, store.Location(state, machine));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or LabException)
            {
                failure ??= new LabException("error.sessionCleanup", error.Message, error);
            }
        }

        if (snapshot.Host is not { } host || ending.Length == 0)
            return snapshot with { CleanupError = failure };

        var remainingIds = state.Machines.Select(vm => vm.Id).ToHashSet();
        var currentHost = host with { Machines = host.Machines.Where(vm => remainingIds.Contains(vm.Id)).ToArray() };
        return snapshot with { Host = currentHost, CleanupError = failure };
    }

    private async Task DeleteMachineCoreAsync(LabState state, MachineLocation location)
    {
        RequireNoDiskRestore(state, location.Machine.Id);
        if (state.RecoveryPoints.Any(point => point.Disks.Any(disk => disk.MachineId == location.Machine.Id)))
            throw new LabException("error.inUse", location.Machine.Name);
        LocalPaths.RequireOrdinaryPath(location.Directory);
        await hypervisor.DeleteAsync(location);
        LocalPaths.RemoveDirectory(store.Machines, location.Machine.Id);
        state.Machines.Remove(location.Machine);
        var oldJournal = state.Operations.ToArray();
        var oldArchives = state.ArchivedSessions.ToArray();
        RecordOperation(state, location.Machine.Id, RecordedOperation.Remove);
        ArchiveSession(state, location.Machine);
        var pending = state.Provisions.SingleOrDefault(item => item.Id == location.Machine.Id);
        if (pending is not null)
            state.Provisions.Remove(pending);
        try
        {
            store.Save(state);
        }
        catch
        {
            state.Machines.Add(location.Machine);
            state.Operations.Clear();
            state.Operations.AddRange(oldJournal);
            state.ArchivedSessions.Clear();
            state.ArchivedSessions.AddRange(oldArchives);
            if (pending is not null)
                state.Provisions.Add(pending);
            throw;
        }
    }
}
