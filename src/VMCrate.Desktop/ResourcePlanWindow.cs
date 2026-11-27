using System.Windows.Controls;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

internal sealed class ResourcePlanWindow : WorkflowWindow
{
    internal ResourcePlanWindow(Labels text, LabService lab) : base(text, "resources.title", 710, 560)
    {
        var capacity = Note("");
        var rows = Field("tools.builds", new ListBox { MinHeight = 170 });
        Action(Actions(), "action.refresh", RefreshAsync, primary: true);
        Loaded += async (_, _) => await RunAsync(RefreshAsync);

        async Task RefreshAsync()
        {
            var host = new PowerShellHypervisor();
            var readiness = host.ReadHostReadinessAsync();
            var load = host.ReadHostLoadAsync(lab.WorkspaceDirectory);
            await Task.WhenAll(readiness, load);
            var result = await load;
            var volume = WorkspaceVolume.Inspect(lab.WorkspaceDirectory);
            var hardware = (await readiness).Capacity;
            capacity.Text = Text.Format("setup.capacity", hardware?.LogicalProcessors?.ToString() ?? "—",
                hardware?.FreeMemoryBytes is long memory ? MiB(memory) : "—") + Environment.NewLine +
                Text.Format("resources.load", result.CpuPercent?.ToString() ?? "—", result.DiskLatencyMs?.ToString("N2") ?? "—",
                    volume.FreeBytes is long free ? MiB(free) : "—", result.ObservedUtc.ToLocalTime().ToString("g"));
            if (result.CpuPercent >= 90)
                capacity.Text += Environment.NewLine + Text["resources.cpu"];
            if (result.DiskLatencyMs >= 20)
                capacity.Text += Environment.NewLine + Text["resources.disk"];
            var plans = await Task.Run(lab.ReadBuildResources);
            rows.ItemsSource = plans.Select(plan => $"{plan.Name} · {plan.MemoryGiB} GiB · {plan.Processors} vCPU\n" +
                Text.Format("build.space", GiB(plan.Space.FreeBytes), GiB(plan.Space.CacheBytes), GiB(plan.Space.FullDiskGrowthBytes))).ToArray();
        }
    }

    private static string GiB(long? bytes) => bytes is long value ? (value / (double)(1L << 30)).ToString("N1") : "—";
}
