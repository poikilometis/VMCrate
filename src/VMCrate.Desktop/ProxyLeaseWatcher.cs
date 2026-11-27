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
        while (ExpiresUtc > DateTimeOffset.UtcNow)
        {
            var remaining = ExpiresUtc - DateTimeOffset.UtcNow;
            await Task.Delay((int)Math.Clamp(remaining.TotalMilliseconds, 1, 3000));
        }
        if (!WorkspaceVolume.HasCatalog(Workspace))
            return 0;

        var lab = new LabService(Workspace, new PowerShellHypervisor(), new UdfInboxWriter());
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await lab.ExpireUpdateLeaseAsync(Machine);
                return 0;
            }
            catch (LabException error) when (error.Key is "error.busy" or "error.host" or "error.backend")
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
        return 1;
    }
}
