using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class HostDiagnosticsWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly TextBox endpoint;
    private readonly TextBlock summary;
    private readonly ComboBox machines;
    private readonly Button probe;
    private readonly Button export;
    private HostDiagnostics? current;

    internal HostDiagnosticsWindow(Labels text, LabService lab) : base(text, "tools.diagnostics")
    {
        this.lab = lab;
        endpoint = Field("label.proxyIp", new TextBox());
        var buttons = Actions();
        Action(buttons, "action.refresh", RefreshAsync, true);
        export = Action(buttons, "action.vmReport", ExportAsync);
        summary = Note("");
        machines = Field("report.machine", new ComboBox { DisplayMemberPath = "Name" });
        machines.SelectionChanged += (_, _) => EnableProbe();
        probe = Action(Actions(), "action.guestProbe", ProbeAsync);
        export.IsEnabled = false;
        EnableProbe();
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var address = endpoint.Text.Trim();
        current = await Task.Run(() => lab.DiagnoseAsync(address));
        var host = current.Snapshot.Host;
        var network = current.Network;
        var rows = new List<string>
        {
            Text[host is null ? "host.unavailable" : host.EnhancedSession ? "host.enhanced" : "host.ready"],
            Text.Format("diag.findings", host?.Machines.Sum(vm => vm.Findings.Length).ToString() ?? "—"),
            Text.Format("storage.free", current.Storage.FreeBytes is long free ? MiB(free) : "—"),
            Text.Format("diag.nat", network?.NatCount?.ToString() ?? "—")
        };
        if (network?.OverlappingNats == true)
            rows.Add(Text["diag.overlap"]);
        if (network?.RouteConflicts is > 0)
            rows.Add(Text.Format("diag.routeConflicts", network.RouteConflicts));
        if (address.Length != 0)
            rows.Add(network?.RouteKnown == true
                ? Text.Format("diag.route", network.Interface ?? "—", network.SourceAddress ?? "—") +
                    (network.IsTunnel == true ? " · " + Text["diag.tunnel"] : "")
                : Text["diag.routeMissing"]);
        foreach (var item in network?.Switches ?? [])
            rows.Add($"{item.Name} · {item.Type} · {string.Join(", ", item.IPv4Networks)}");
        summary.Text = string.Join(Environment.NewLine, rows);
        machines.ItemsSource = current.Snapshot.Machines.Where(vm => vm.Guest == GuestFamily.Windows &&
            vm.Network is NetworkMode.Offline or NetworkMode.Private && current.Snapshot.StateOf(vm.Id) == PowerState.Running).ToArray();
        machines.SelectedIndex = machines.Items.Count > 0 ? 0 : -1;
        export.IsEnabled = true;
        EnableProbe();
    }

    private void EnableProbe()
    {
        if (probe is not null)
            probe.IsEnabled = machines.SelectedItem is Machine;
    }

    private Task ProbeAsync()
    {
        if (machines.SelectedItem is Machine machine)
            new GuestProbeWindow(Text, lab, machine) { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }

    private Task ExportAsync()
    {
        var path = SaveFile("tools.diagnostics", $"vmcrate-health-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        return path is null || current is not { } result ? Task.CompletedTask : Task.Run(() => result.Export(path));
    }
}
