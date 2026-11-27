using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace VMCrate.Core;

public sealed record UpdateProxy(Guid SwitchId, string Address, int Port, DateTimeOffset? ExpiresUtc = null,
    string? ReviewedRoute = null)
{
    public static UpdateProxy Create(Guid switchId, string address, int port, DateTimeOffset? expiresUtc = null)
    {
        if (switchId == Guid.Empty || port is < 1 or > 65535 ||
            !IPAddress.TryParse(address.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            !IsPublic(ip))
            throw new LabException("error.updateEndpoint");

        if (expiresUtc is { } expiry && (expiry <= DateTimeOffset.UtcNow || expiry > DateTimeOffset.UtcNow.AddDays(1)))
            throw new LabException("error.proxyLease");

        return new UpdateProxy(switchId, ip.ToString(), port, expiresUtc);
    }

    internal bool IsExpired(DateTimeOffset now) => ExpiresUtc is { } end && end <= now;

    public string? RouteFingerprint(HostNetworkObservation? observation)
    {
        if (observation is not { RouteKnown: true, Interface: not null, SourceAddress: not null, OverlappingNats: not true } ||
            observation.RouteConflicts is > 0 || observation.Switches.SingleOrDefault(item => item.Id == SwitchId) is not { Type: "Internal" } adapter)
            return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            observation.Interface, observation.SourceAddress, observation.IsTunnel, observation.PrefixLength,
            observation.InterfaceIndex, observation.NextHop, observation.DestinationPrefix, observation.NatCount,
            NatPrefixes = observation.NatPrefixes?.Order(StringComparer.Ordinal).ToArray(),
            Networks = adapter.IPv4Networks.Order(StringComparer.Ordinal).ToArray()
        });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static bool IsPublic(IPAddress ip)
    {
        var a = ip.GetAddressBytes();
        return a[0] is > 0 and < 224 and not (10 or 127) &&
            !(a[0] == 100 && a[1] is >= 64 and <= 127) &&
            !(a[0] == 169 && a[1] == 254) &&
            !(a[0] == 172 && a[1] is >= 16 and <= 31) &&
            !(a[0] == 192 && a[1] == 168) &&
            !(a[0] == 192 && a[1] == 0) &&
            !(a[0] == 198 && a[1] is 18 or 19);
    }
}

public sealed partial class LabService
{
    public async Task SetUpdateProxyAsync(Guid id, UpdateProxy? proxy)
    {
        if (proxy is not null)
            proxy = UpdateProxy.Create(proxy.SwitchId, proxy.Address, proxy.Port, proxy.ExpiresUtc) with { ReviewedRoute = proxy.ReviewedRoute };

        using var workspaceLock = store.Lock();
        var state = store.Load();
        var machine = FindMachine(state, id);
        if (machine.TemplateId is not null || machine.Disposable || machine.BlueprintId is not null ||
            (proxy is null && machine.Network != NetworkMode.Updates))
            throw new LabException("error.updateBuilder");

        if (proxy?.ReviewedRoute is { } reviewed && proxy.RouteFingerprint(await ReadProxyRouteAsync(proxy.Address)) != reviewed)
            throw new LabException("error.proxyDrift");

        var updated = machine with
        {
            Network = proxy is null ? NetworkMode.Offline : NetworkMode.Updates,
            Proxy = proxy
        };
        await ApplyNetworkAsync(state, machine, updated);
    }

    private async Task ApplyNetworkAsync(LabState state, Machine previous, Machine updated)
    {
        await hypervisor.SetNetworkAsync(store.Location(state, updated));
        state.Machines[state.Machines.IndexOf(previous)] = updated;
        store.Save(state);
    }
}
