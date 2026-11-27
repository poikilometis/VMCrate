using System.Globalization;
using System.Text;

namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task<ProxyHostTrace> TraceHostProxyAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var (machine, proxy) = ProxyBuilder(state, id);
        VmObservation? observed = null;
        try { observed = await ObserveOneAsync(store.Location(state, machine)); }
        catch (LabException) { }
        var lookup = ReadProxyRouteAsync(proxy.Address);
        var tcp = hypervisor.CheckHostProxyAsync(proxy);
        await Task.WhenAll(lookup, tcp);
        var network = await lookup;
        RecordOperation(state, id, RecordedOperation.ProxyHost, (await tcp).ToString());
        store.Save(state);
        return new(observed?.State ?? PowerState.Unavailable,
            observed is { State: PowerState.Off or PowerState.Running } && !observed.Findings.Contains("audit.network"),
            network, network?.Switches.SingleOrDefault(item => item.Id == proxy.SwitchId), await tcp);
    }

    private async Task<HostNetworkObservation?> ReadProxyRouteAsync(string address)
    {
        try { return await hypervisor.ObserveNetworkAsync(address); }
        catch (LabException) { return null; }
    }

    public async Task<ProxyGuestTrace> TraceWindowsProxyAsync(Guid id, ProxyTraceOptions options,
        string user, string password, string proxyUser, string proxyPassword, UpdateProxy? expected = null)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password) ||
            proxyUser.Any(char.IsControl) || proxyPassword.Any(char.IsControl) ||
            proxyUser.Contains(':') || (proxyUser.Length == 0 && proxyPassword.Length != 0) ||
            (options.RelayPort is not null && proxyUser.Length != 0))
            throw new LabException("error.proxyOptions");

        using var workspaceLock = store.Lock();
        var state = store.Load();
        var (machine, proxy) = ProxyBuilder(state, id);
        if (expected is not null && (expected.Address != proxy.Address || expected.Port != proxy.Port || expected.SwitchId != proxy.SwitchId))
            throw new LabException("error.proxyBuilder");
        var location = store.Location(state, machine);
        await RequireRunningProxyBuilderAsync(location, GuestFamily.Windows);
        var trace = await hypervisor.CheckGuestProxyAsync(location, options, user, password, proxyUser, proxyPassword);
        RecordOperation(state, id, RecordedOperation.ProxyGuest, trace.Result.ToString());
        store.Save(state);
        return trace;
    }

    public async Task AttachLinuxProxyProbeAsync(Guid id, ProxyTraceOptions options)
    {
        options.Validate();
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var (machine, proxy) = ProxyBuilder(state, id);
        var location = store.Location(state, machine);
        await RequireRunningProxyBuilderAsync(location, GuestFamily.Linux);
        var staging = Path.Combine(store.Root, "probe");
        var stageId = Guid.NewGuid();
        var directory = LocalPaths.MakeDirectory(Path.Combine(staging, stageId.ToString("N")));
        var file = Path.Combine(directory, "VMCrate-proxy.sh");
        try
        {
            await File.WriteAllTextAsync(file, LinuxProxyScript(proxy, options), new UTF8Encoding(false));
            await SendFilesCoreAsync(location, [file]);
        }
        finally { LocalPaths.RemoveDirectory(staging, stageId); }
    }

    private static (Machine Machine, UpdateProxy Proxy) ProxyBuilder(LabState state, Guid id)
    {
        var machine = FindMachine(state, id);
        if (machine.Network != NetworkMode.Updates || machine.Proxy is not { } proxy ||
            machine.TemplateId is not null || machine.BlueprintId is not null || machine.Disposable)
            throw new LabException("error.proxyBuilder");
        return (machine, UpdateProxy.Create(proxy.SwitchId, proxy.Address, proxy.Port, proxy.ExpiresUtc));
    }

    private async Task RequireRunningProxyBuilderAsync(MachineLocation location, GuestFamily family)
    {
        var observed = await ObserveOneAsync(location);
        if (location.Machine.Guest != family || observed is not { State: PowerState.Running } ||
            observed.Findings.Contains("audit.network"))
            throw new LabException("error.proxyBuilder");
    }

    private static string LinuxProxyScript(UpdateProxy proxy, ProxyTraceOptions options)
    {
        using var stream = typeof(LabService).Assembly.GetManifestResourceStream("VMCrate.Core.UpdateProxyProbe.sh")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("@PROXY_ADDRESS@", proxy.Address, StringComparison.Ordinal)
            .Replace("@PROXY_PORT@", proxy.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@RELAY_PORT@", options.RelayPort?.ToString(CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal)
            .Replace("@REPOSITORY@", options.Repository.AbsoluteUri, StringComparison.Ordinal);
    }
}
