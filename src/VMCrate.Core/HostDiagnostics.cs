using System.Text.Json;

namespace VMCrate.Core;

public sealed record HostSwitch(Guid Id, string Name, string Type, string[] IPv4Networks);
public sealed record HostNetworkObservation(
    bool RouteKnown, string? Interface, bool? IsTunnel, HostSwitch[] Switches,
    int? NatCount, bool? OverlappingNats, string? SourceAddress, int? PrefixLength, int? RouteConflicts = null,
    int? InterfaceIndex = null, string? NextHop = null, string? DestinationPrefix = null, string[]? NatPrefixes = null);

public sealed record HostDiagnosticReport(
    DateTimeOffset CreatedUtc, bool HyperVAccessible, bool? EnhancedSession,
    long? FreeBytes, long TemplateBytes, long MachineBytes, long MediaBytes,
    int ManagedVms, int? Findings, bool? RouteKnown, bool? UsesTunnel,
    int? InternalSwitches, int? NatCount, bool? OverlappingNats, int? RouteConflicts);

public sealed record HostDiagnostics(
    LabSnapshot Snapshot, WorkspaceStorage Storage, HostNetworkObservation? Network)
{
    public void Export(string path) =>
        OutputFile.WriteNew(path, ".json", stream => JsonSerializer.Serialize(stream, Summarize(), JsonDefaults.Options));

    internal HostDiagnosticReport Summarize() => new(DateTimeOffset.UtcNow, Snapshot.Host is not null,
            Snapshot.Host?.EnhancedSession, Storage.FreeBytes, Storage.TemplateBytes, Storage.MachineBytes,
            Storage.MediaBytes, Snapshot.Machines.Count, Snapshot.Host?.Machines.Sum(vm => vm.Findings.Length),
            Network?.RouteKnown, Network?.IsTunnel, Network?.Switches.Count(sw => sw.Type == "Internal"),
            Network?.NatCount, Network?.OverlappingNats, Network?.RouteConflicts);
}

public sealed record GuestNetworkCheck(int ActiveAdapters, int DefaultRoutes, bool? PeerReachable,
    int? IPv4DefaultRoutes = null, int? IPv6DefaultRoutes = null, int? DnsServers = null, bool? InboxReadable = null);

public sealed partial class LabService
{
    public async Task<HostDiagnostics> DiagnoseAsync(string? proxyAddress)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        return await DiagnoseAsync(state, proxyAddress);
    }

    private async Task<HostDiagnostics> DiagnoseAsync(LabState state, string? proxyAddress)
    {
        var snapshot = await ObserveStateAsync(state);
        HostNetworkObservation? network = null;
        try
        {
            network = await hypervisor.ObserveNetworkAsync(proxyAddress);
        }
        catch (LabException)
        {
            // Storage and hypervisor observations still describe the partial result.
        }
        return new HostDiagnostics(snapshot, ScanStorage(state), network);
    }

    public async Task<GuestNetworkCheck> CheckGuestNetworkAsync(Guid id, string user, string password, string? peerAddress)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var vm = FindMachine(state, id);
        if (vm.Guest != GuestFamily.Windows || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
            throw new LabException("error.guestProbe");
        if (vm.Network == NetworkMode.Updates)
            throw new LabException("error.guestProbe");

        if (!string.IsNullOrWhiteSpace(peerAddress))
        {
            if (vm.Network != NetworkMode.Private ||
                !System.Net.IPAddress.TryParse(peerAddress, out var peer) ||
                peer.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !IsPrivatePeer(peer.GetAddressBytes()))
                throw new LabException("error.guestProbe");
            peerAddress = peer.ToString();
        }

        return await hypervisor.CheckGuestNetworkAsync(store.Location(state, vm), user, password, peerAddress);
    }

    private static bool IsPrivatePeer(byte[] address) =>
        address[0] == 10 || (address[0] == 172 && address[1] is >= 16 and <= 31) ||
        (address[0] == 192 && address[1] == 168);
}
