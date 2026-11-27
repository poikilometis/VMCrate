using System.Collections.Concurrent;
using System.Diagnostics;
using VMCrate.Core;

namespace VMCrate.Windows;

public sealed partial class PowerShellHypervisor
{
    private readonly ConcurrentDictionary<Guid, ConsoleProcess> consoles = new();

    public Task OpenConsoleAsync(MachineLocation location) => ConnectAsync(location, reconnect: false);
    public Task ReconnectConsoleAsync(MachineLocation location) => ConnectAsync(location, reconnect: true);
    public async Task CloseConsoleAsync(MachineLocation location)
    {
        if (!consoles.TryGetValue(location.Machine.Id, out var connection))
            return;
        using var process = connection.Open();
        if (process is not null)
            await CloseConsoleAsync(process);
        consoles.TryRemove(location.Machine.Id, out _);
    }

    private async Task ConnectAsync(MachineLocation location, bool reconnect)
    {
        var name = await InvokeAsync<string>("Console", new { location });
        var id = location.Machine.Id;
        using var previous = consoles.TryGetValue(id, out var connection) ? connection.Open() : null;
        if (reconnect && previous is not null)
            await CloseConsoleAsync(previous);

        var start = new ProcessStartInfo(Path.Combine(SystemDirectory, "vmconnect.exe")) { UseShellExecute = false };
        start.ArgumentList.Add("localhost");
        start.ArgumentList.Add(name);
        using var process = Process.Start(start) ?? throw new LabException("error.consoleReconnect");
        if ((reconnect || previous is null) && !process.HasExited)
        {
            try { consoles[id] = new(process.Id, process.StartTime); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
    }

    private static async Task CloseConsoleAsync(Process process)
    {
        try
        {
            if (!process.CloseMainWindow() && !process.HasExited)
                throw new LabException("error.consoleReconnect");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (OperationCanceledException error) { throw new LabException("error.consoleReconnect", inner: error); }
    }

    private sealed record ConsoleProcess(int Id, DateTime Started)
    {
        internal Process? Open()
        {
            Process process;
            try { process = Process.GetProcessById(Id); }
            catch (ArgumentException) { return null; }
            try
            {
                if (!process.HasExited && process.StartTime == Started &&
                    process.ProcessName.Equals("vmconnect", StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            process.Dispose();
            return null;
        }
    }
}
