namespace VMCrate.Core;

public sealed partial class LabService
{
    public string WorkspaceDirectory => store.Root;

    public (Guid Id, DateTimeOffset ExpiresUtc)[] ReadUpdateLeases() => store.Load().Machines
        .Where(vm => vm.Proxy?.ExpiresUtc is not null)
        .Select(vm => (vm.Id, vm.Proxy!.ExpiresUtc!.Value)).ToArray();

    public async Task ExpireUpdateLeaseAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        if (state.Machines.SingleOrDefault(vm => vm.Id == id) is { } machine)
            await ExpireUpdateLeaseAsync(state, machine);
    }

    public async Task<bool> WatchUpdateRouteAsync(Guid id, DateTimeOffset expiry)
    {
        var state = store.Load();
        var machine = state.Machines.SingleOrDefault(vm => vm.Id == id);
        if (machine?.Proxy is not { } proxy || proxy.ExpiresUtc != expiry)
            return true;
        var expired = proxy.IsExpired(DateTimeOffset.UtcNow);
        var location = store.Location(state, machine);
        if (!expired && (proxy.ReviewedRoute is null || await MatchesReviewedRouteAsync(location, proxy)))
            return false;

        // Enforce the host policy even if a long copy temporarily owns the workspace lock.
        if (expired)
            await hypervisor.CloseExpiredUpdateAsync(location);
        else
            await hypervisor.CloseDriftedUpdateAsync(location);

        using var workspaceLock = store.Lock();
        var latest = store.Load();
        if (latest.Machines.SingleOrDefault(vm => vm.Id == id) is { } current && current.Proxy == proxy)
            SetClosedLease(latest, current, expired ? "status.proxyExpired" : "error.proxyDrift");
        return true;
    }

    private async Task<bool> CloseOnRouteDriftAsync(LabState state, Machine machine)
    {
        if (machine.Proxy is not { ReviewedRoute: not null } proxy)
            return false;
        var location = store.Location(state, machine);
        if (await MatchesReviewedRouteAsync(location, proxy))
            return false;
        await hypervisor.CloseDriftedUpdateAsync(location);
        SetClosedLease(state, machine, "error.proxyDrift");
        return true;
    }

    private async Task<bool> MatchesReviewedRouteAsync(MachineLocation location, UpdateProxy proxy) =>
        proxy.RouteFingerprint(await ReadProxyRouteAsync(proxy.Address)) is { } current && current == proxy.ReviewedRoute &&
        await ObserveOneAsync(location) is { State: PowerState.Running or PowerState.Off } observed &&
        !observed.Findings.Contains("audit.network");

    private async Task ExpireUpdateLeaseAsync(LabState state, Machine machine)
    {
        if (machine is not { Network: NetworkMode.Updates, Proxy: { } proxy } || !proxy.IsExpired(DateTimeOffset.UtcNow))
            return;
        var location = store.Location(state, machine);
        await hypervisor.CloseExpiredUpdateAsync(location);
        SetClosedLease(state, machine, "status.proxyExpired");
    }

    private void SetClosedLease(LabState state, Machine machine, string reason)
    {
        state.Machines[state.Machines.IndexOf(machine)] = machine with { Network = NetworkMode.Offline, Proxy = null };
        RecordOperation(state, machine.Id, RecordedOperation.ProxyHost, reason);
        store.Save(state);
    }
}
