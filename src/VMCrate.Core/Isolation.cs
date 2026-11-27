namespace VMCrate.Core;

public sealed record SwitchBinding(string Name, string? Type);

public sealed record HyperVIsolation(PowerState State, bool? NetworkPolicyMatches, SwitchBinding[] Switches,
    bool? PrivateMembershipMatches, bool? FileCopyEnabled, bool? KeyValueExchangeEnabled,
    bool? EnhancedSessionAllowed, bool? MediaLayoutMatches, bool? InboxAttached);

public sealed record IsolationObservation(NetworkMode Profile, DateTimeOffset ObservedUtc, HyperVIsolation Host,
    UpdateProxy? Proxy, GuestNetworkCheck? Guest = null, DateTimeOffset? GuestObservedUtc = null, bool GuestReported = false)
{
    public bool? DefaultRouteCheck => Guest is { IPv4DefaultRoutes: int v4, IPv6DefaultRoutes: int v6 } &&
        Profile is NetworkMode.Offline or NetworkMode.Private ? v4 == 0 && v6 == 0 : null;
}

public sealed partial class LabService
{
    public async Task<IsolationObservation> ObserveIsolationAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        return await ObserveIsolationAsync(state, FindMachine(state, id));
    }

    public async Task<IsolationObservation> CheckWindowsIsolationAsync(Guid id, string user, string password)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
            throw new LabException("error.guestProbe");

        using var workspaceLock = store.Lock();
        var state = store.Load();
        var vm = FindMachine(state, id);
        var before = await ObserveIsolationAsync(state, vm);
        if (vm.Guest != GuestFamily.Windows || vm.Network == NetworkMode.Updates ||
            before.Host is not { State: PowerState.Running, NetworkPolicyMatches: true })
            return before;

        var guest = await hypervisor.CheckGuestNetworkAsync(store.Location(state, vm), user, password, null);
        var observedUtc = DateTimeOffset.UtcNow;
        var after = await ObserveIsolationAsync(state, vm);
        return after.Host is { State: PowerState.Running, NetworkPolicyMatches: true }
            ? after with { Guest = guest, GuestObservedUtc = observedUtc }
            : after;
    }

    private async Task<IsolationObservation> ObserveIsolationAsync(LabState state, Machine vm)
    {
        var peers = vm.Network == NetworkMode.Private
            ? state.Machines.Where(peer => peer.Network == NetworkMode.Private && peer.PrivateSegment == vm.PrivateSegment)
                .Select(peer => store.Location(state, peer)).ToArray()
            : [];
        var host = await hypervisor.ObserveIsolationAsync(store.Location(state, vm), peers);
        return new(vm.Network, DateTimeOffset.UtcNow, host, vm.Proxy);
    }
}
