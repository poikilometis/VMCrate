using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class UpdateProxyWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly Guid id;
    private readonly TextBox address;
    private readonly TextBox port;
    private readonly TextBox duration;
    private readonly ComboBox switches;
    private readonly TextBlock route;
    private readonly Button disable;

    internal UpdateProxyWindow(Labels text, LabService lab, Machine machine) : base(text, "tools.updates", 620, 545)
    {
        this.lab = lab;
        id = machine.Id;
        Note(machine.Name);
        Note(Text["hint.updateProxy"]);
        switches = Field("label.switch", new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id" });
        address = Field("label.proxyIp", new TextBox { Text = machine.Proxy?.Address ?? "", FlowDirection = System.Windows.FlowDirection.LeftToRight });
        port = Field("label.port", new TextBox { Text = machine.Proxy?.Port.ToString() ?? "" });
        duration = Field("proxy.duration", new TextBox { Text = "60", MaxLength = 4 });
        route = Note(machine.Proxy?.ExpiresUtc?.ToLocalTime().ToString("g") ?? "");
        var buttons = Actions();
        Action(buttons, "action.checkRoute", CheckRouteAsync);
        Action(buttons, "action.enableProxy", EnableAsync, true);
        disable = Action(buttons, "action.disableProxy", DisableAsync);
        disable.IsEnabled = machine.Proxy is not null;
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            var state = await Task.Run(() => lab.DiagnoseAsync(null));
            switches.ItemsSource = state.Network?.Switches.Where(item => item.Type == "Internal").ToArray() ?? [];
            switches.SelectedValue = machine.Proxy?.SwitchId;
            if (switches.SelectedIndex < 0 && switches.Items.Count > 0)
                switches.SelectedIndex = 0;
        });
    }

    private async Task CheckRouteAsync()
    {
        var endpoint = address.Text;
        var state = await Task.Run(() => lab.DiagnoseAsync(endpoint));
        var network = state.Network;
        route.Text = network?.RouteKnown == true
            ? Text.Format("diag.route", network.Interface ?? "—", network.SourceAddress ?? "—") +
                (network.IsTunnel == true ? " · " + Text["diag.tunnel"] : "")
            : Text["diag.routeMissing"];
        if (switches.SelectedItem is HostSwitch selected)
            route.Text += Environment.NewLine + string.Join(", ", selected.IPv4Networks);
    }

    private async Task EnableAsync()
    {
        var minutes = Integer(duration.Text);
        if (minutes is < 1 or > 1440)
            throw new LabException("error.proxyLease");
        var spec = UpdateProxy.Create(switches.SelectedValue is Guid key ? key : Guid.Empty, address.Text, Integer(port.Text),
            DateTimeOffset.UtcNow.AddMinutes(minutes));
        await CheckRouteAsync();
        if (!Text.Confirm(this, "confirm.proxy", $"{spec.Address}:{spec.Port}", route.Text))
            return;
        await Task.Run(async () =>
        {
            ProxyLeaseWatcher.Start(id, lab.WorkspaceDirectory, spec.ExpiresUtc!.Value);
            await lab.SetUpdateProxyAsync(id, spec);
        });
        route.Text += Environment.NewLine + spec.ExpiresUtc!.Value.ToLocalTime().ToString("g");
        disable.IsEnabled = true;
    }

    private async Task DisableAsync()
    {
        await Task.Run(() => lab.SetUpdateProxyAsync(id, null));
        route.Text = Text["network.Offline"];
        disable.IsEnabled = false;
    }
}
