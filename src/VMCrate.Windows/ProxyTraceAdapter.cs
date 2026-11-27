using System.Net;
using System.Net.Sockets;
using VMCrate.Core;

namespace VMCrate.Windows;

public sealed partial class PowerShellHypervisor
{
    public async Task<ProbeCheck> CheckHostProxyAsync(UpdateProxy proxy)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Parse(proxy.Address), proxy.Port, timeout.Token);
            return ProbeCheck.Passed;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            return ProbeCheck.Failed;
        }
    }

    public Task<ProxyGuestTrace> CheckGuestProxyAsync(MachineLocation location, ProxyTraceOptions options, string user,
        string password, string proxyUser, string proxyPassword) =>
        InvokeAsync<ProxyGuestTrace>("CheckGuestProxy", new
        {
            location, repository = options.Repository.AbsoluteUri, options.RelayPort, user, password, proxyUser, proxyPassword
        });
}
