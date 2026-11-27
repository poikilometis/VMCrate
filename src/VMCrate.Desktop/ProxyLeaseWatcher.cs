using System.Diagnostics;
using System.Globalization;
using System.IO;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

internal sealed record ProxyLeaseWatcher(Guid Machine, string Workspace, DateTimeOffset ExpiresUtc)
{
    internal static ProxyLeaseWatcher? Parse(string[] args)
    {
        if (args is not ["--close-update-lease", var id, var workspace, var stamp] ||
            !Guid.TryParseExact(id, "N", out var machine) || machine == Guid.Empty ||
            !Path.IsPathFullyQualified(workspace) ||
            !long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < 1 || ticks > DateTimeOffset.UtcNow.AddDays(2).Ticks)
            return null;
        return new(machine, LocalPaths.FullPath(workspace), new DateTimeOffset(ticks, TimeSpan.Zero));
    }

    internal static void Start(Guid machine, string workspace, DateTimeOffset expiry)
    {
        var executable = LocalPaths.RequireFile(Path.Combine(AppContext.BaseDirectory, "VMCrate.exe"), ".exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--close-update-lease", machine.ToString("N"), workspace,
                     expiry.UtcTicks.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        using var launched = Process.Start(start) ?? throw new LabException("error.proxyLease");
    }

    internal async Task<int> RunAsync()
    {
        if (!WorkspaceVolume.HasCatalog(Workspace))
            return 0;
        var runtime = Path.Combine(Workspace, "jobs");
        LocalPaths.RequireOrdinaryPath(runtime);
        Directory.CreateDirectory(runtime);
        var path = Path.Combine(runtime, $"proxy-{Machine:N}-{ExpiresUtc.UtcTicks}.lock");
        LocalPaths.RequireOrdinaryPath(path);
        FileStream singleton;
        try { singleton = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return 0; }
        using var ownership = singleton;

        var lab = new LabService(Workspace, new PowerShellHypervisor(), new UdfInboxWriter());
        var attempts = 0;
        while (attempts < 60)
        {
            try
            {
                if (await lab.WatchUpdateRouteAsync(Machine, ExpiresUtc))
                    return 0;
                attempts = 0;
            }
            catch (LabException error) when (error.Key == "error.busy")
            {
                attempts = 0;
            }
            catch (LabException error) when (error.Key is "error.host" or "error.backend" or "error.timeout")
            {
                attempts++;
            }
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
        return 1;
    }

    internal static async Task WakeAsync(LabService lab)
    {
        var snapshot = await lab.ObserveAsync();
        foreach (var machine in snapshot.Machines.Where(vm => vm.Proxy?.ExpiresUtc is not null))
            Start(machine.Id, lab.WorkspaceDirectory, machine.Proxy!.ExpiresUtc!.Value);
    }
}
