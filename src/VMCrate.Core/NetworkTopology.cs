using System.Net;

namespace VMCrate.Core;

public sealed record NetworkTopologyMember(Guid Id, string Name, GuestFamily Guest, PowerState State, NetworkMode Profile,
    string? Switch, string? ProposedIPv4);
public sealed record NetworkTopology(string Lab, bool HostNetworksRead, NetworkTopologyMember[] Machines);

public sealed partial class LabService
{
    public async Task<NetworkTopology> ReadNetworkTopologyAsync(string id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var members = GetLabMembers(state, id);
        var snapshot = await ObserveStateAsync(state);
        HostNetworkObservation? network = null;
        try { network = await hypervisor.ObserveNetworkAsync(null); }
        catch (LabException) { }

        var allocations = PlanPrivateAddresses(members, NetworkPrefixes(network));
        return new(id, network is not null && snapshot.Host is not null, members.Select(vm => new NetworkTopologyMember(vm.Id, vm.Name, vm.Guest,
            snapshot.StateOf(vm.Id), vm.Network, vm.PrivateSegment is Guid segment
                ? network?.Switches.SingleOrDefault(item => item.Type == "Private" &&
                    item.Name.Equals($"VMCrate-Private-{segment:N}", StringComparison.OrdinalIgnoreCase))?.Name
                : null, allocations.GetValueOrDefault(vm.Id))).ToArray());
    }

    public async Task ApplyPrivateWindowsAddressAsync(string lab, Guid id, string proposed, string user, string password)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
            throw new LabException("error.guestProbe");
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var members = GetLabMembers(state, lab);
        var vm = members.SingleOrDefault(member => member.Id == id);
        if (vm is not { Guest: GuestFamily.Windows, Network: NetworkMode.Private })
            throw new LabException("error.network");

        var network = await hypervisor.ObserveNetworkAsync(null);
        var plan = PlanPrivateAddresses(members, NetworkPrefixes(network));
        if (!plan.TryGetValue(id, out var expected) || expected != proposed)
            throw new LabException("error.labChanged");
        var isolation = await ObserveIsolationAsync(state, vm);
        if (isolation.Host is not { State: PowerState.Running, NetworkPolicyMatches: true, PrivateMembershipMatches: true })
            throw new LabException("error.isolation");

        var address = expected.Split('/')[0];
        await hypervisor.ConfigurePrivateAddressAsync(store.Location(state, vm), address, user, password);
    }

    private static IPNetwork[] NetworkPrefixes(HostNetworkObservation? network) =>
        (network?.Switches ?? []).SelectMany(item => item.IPv4Networks)
            .Select(text => IPNetwork.TryParse(NormalizePrefix(text), out var result) ? (IPNetwork?)result : null)
            .OfType<IPNetwork>().ToArray();

    internal static Dictionary<Guid, string> PlanPrivateAddresses(Machine[] members, IPNetwork[] used)
    {
        var proposals = new Dictionary<Guid, string>();
        var next = 0;
        foreach (var group in members.Where(vm => vm.Network == NetworkMode.Private && vm.PrivateSegment is not null)
                     .GroupBy(vm => vm.PrivateSegment!.Value).OrderBy(vm => vm.Key))
        {
            string prefix;
            while (true)
            {
                if (next >= 4096)
                    throw new LabException("error.network");
                prefix = $"10.{208 + next / 256}.{next % 256}";
                next++;
                var beginning = IPAddress.Parse(prefix + ".0");
                var end = IPAddress.Parse(prefix + ".255");
                var block = new IPNetwork(beginning, 24);
                if (used.All(item => !item.Contains(beginning) && !item.Contains(end) && !block.Contains(item.BaseAddress)))
                    break;
            }
            var host = 10;
            foreach (var vm in group.OrderBy(vm => vm.Name, StringComparer.Ordinal))
                proposals.Add(vm.Id, $"{prefix}.{host++}/24");
        }
        return proposals;
    }

    private static string NormalizePrefix(string prefix)
    {
        var parts = prefix.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out var length) || length is < 0 or > 32)
            return prefix;
        var bytes = address.GetAddressBytes();
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = (byte)(bytes[index] & (0xff << (8 - Math.Clamp(length - index * 8, 0, 8))));
        return $"{new IPAddress(bytes)}/{length}";
    }
}
