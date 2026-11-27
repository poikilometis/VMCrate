using System.Runtime.InteropServices;

namespace VMCrate.Windows;

public sealed record DisplayDevice(string Name, string? Driver, int? Width, int? Height);
public sealed record DisplayDiagnostics(int Monitors, DisplayDevice[]? Video);

public sealed partial class PowerShellHypervisor
{
    public Task<DisplayDiagnostics> ReadDisplaysAsync() => InvokeAsync<DisplayDiagnostics>("DisplayInfo", new { });

    public static int MonitorCount => GetSystemMetrics(80);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
