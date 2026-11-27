namespace VMCrate.Core;

public sealed partial class LabService
{
    public string WorkspaceDirectory => store.Root;

    public async Task ExpireUpdateLeaseAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        if (state.Machines.SingleOrDefault(vm => vm.Id == id) is { } machine)
            await ExpireUpdateLeaseAsync(state, machine);
    }

    private async Task ExpireUpdateLeaseAsync(LabState state, Machine machine)
    {
        if (machine is not { Network: NetworkMode.Updates, Proxy: { } proxy } || !proxy.IsExpired(DateTimeOffset.UtcNow))
            return;
        var location = store.Location(state, machine);
        await hypervisor.CloseExpiredUpdateAsync(location);
        state.Machines[state.Machines.IndexOf(machine)] = machine with { Network = NetworkMode.Offline, Proxy = null };
        RecordOperation(state, machine.Id, RecordedOperation.ProxyHost, "status.proxyExpired");
        store.Save(state);
    }
}
