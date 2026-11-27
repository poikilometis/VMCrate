namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task<PackageInventory> ReadWindowsInventoryAsync(Guid id, string user, string password)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
            throw new LabException("error.guestProbe");
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var vm = FindMachine(state, id);
        if (vm is not { Guest: GuestFamily.Windows, Disposable: false, TemplateId: not null, Network: NetworkMode.Offline })
            throw new LabException("error.guestProbe");
        var isolation = await ObserveIsolationAsync(state, vm);
        if (isolation.Host is not { State: PowerState.Running, NetworkPolicyMatches: true })
            throw new LabException("error.isolation");
        return (await hypervisor.ReadWindowsInventoryAsync(store.Location(state, vm), user, password)).Validated();
    }
}
