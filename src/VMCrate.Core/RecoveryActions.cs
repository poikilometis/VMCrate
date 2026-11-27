namespace VMCrate.Core;

public sealed partial class LabService
{
    public void RestoreCatalogBackup(Guid expectedWorkspace)
    {
        using var workspaceLock = store.Lock();
        store.RestoreBackup(expectedWorkspace);
    }

    public async Task ResumeProvisionAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var request = state.Provisions.SingleOrDefault(item => item.Id == id)
            ?? throw new LabException("error.recoveryChanged");
        await FinishProvisionAsync(state, store.Location(state, FindMachine(state, id)), request);
    }

    public async Task RecreateMissingCloneAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var machine = FindMachine(state, id);
        var location = store.Location(state, machine);
        if (machine.Disposable || location.BaseDisk is null || state.Provisions.Any(item => item.Id == id) ||
            await ObserveOneAsync(location) is not { State: PowerState.Missing })
            throw new LabException("error.recoveryChanged");

        LocalPaths.RequireFile(location.BaseDisk, ".vhdx");
        await ProvisionMachineAsync(state, machine, null, 0, record: false);
    }

    public async Task RestoreIsolationAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var vm = FindMachine(state, id);
        var observation = await ObserveOneAsync(store.Location(state, vm));
        if (observation is not { State: PowerState.Off } || !observation.Findings.Contains("audit.network") ||
            state.Provisions.Any(item => item.Id == id))
            throw new LabException("error.recoveryChanged");
        if (vm.Network == NetworkMode.Updates)
            await ApplyNetworkAsync(state, vm, vm with { Network = NetworkMode.Offline, Proxy = null });
        else
            await hypervisor.SetNetworkAsync(store.Location(state, vm));
    }

    public async Task RemoveUnusedPrivateSegmentAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var observed = await hypervisor.ObserveRecoveryAsync(state.WorkspaceId);
        if (id == Guid.Empty || state.Machines.Any(vm => vm.PrivateSegment == id) ||
            !observed.Segments.Any(item => item.Id == id && !item.InUse))
            throw new LabException("error.recoveryChanged");
        await hypervisor.RemoveEmptySegmentAsync(state.WorkspaceId, id);
    }
}
