using System.Text.Json;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class NetworkTopologyWindow : WorkflowWindow
{
    internal NetworkTopologyWindow(Labels text, LabService lab, string id) : base(text, "tools.topology", 818, 550)
    {
        Note(id);
        Note(Text["topology.hint"]);
        var summary = Note("");
        var rows = Field("label.network", new ListBox { Height = 245, DisplayMemberPath = nameof(Row.Text) });
        NetworkTopology? view = null;
        async Task RefreshAsync()
        {
            view = await Task.Run(() => lab.ReadNetworkTopologyAsync(id));
            summary.Text = view.HostNetworksRead ? Text["setup.Ready"] : Text["host.unavailable"];
            rows.ItemsSource = view.Machines.Select(vm => new Row(vm, $"{vm.Name} · {Text["power." + vm.State]} · " +
                $"{Text["network." + vm.Profile]} · {vm.ProposedIPv4 ?? "—"} · {vm.Switch ?? "—"}")).ToArray();
        }
        var actions = Actions();
        Action(actions, "action.refresh", RefreshAsync, true);
        var configure = Action(actions, "topology.configure", () =>
        {
            if (rows.SelectedItem is Row { Member: { ProposedIPv4: { } address } member })
                new IsolationGuestDialog(Text, (user, password) => Task.Run(() =>
                    lab.ApplyPrivateWindowsAddressAsync(id, member.Id, address, user, password)),
                    "topology.configure", "topology.credentials") { Owner = this }.ShowDialog();
            return Task.CompletedTask;
        });
        configure.IsEnabled = false;
        rows.SelectionChanged += (_, _) => configure.IsEnabled = rows.SelectedItem is Row
            { Member: { Guest: GuestFamily.Windows, State: PowerState.Running, ProposedIPv4: not null } };
        Action(actions, "action.save", () =>
        {
            if (view is not { } topology || SaveFile("tools.topology", "vmcrate-network-plan.json") is not { } path)
                return Task.CompletedTask;
            return Task.Run(() => OutputFile.WriteNew(path, ".json", stream =>
                JsonSerializer.Serialize(stream, topology, JsonDefaults.Options)));
        });
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    private sealed record Row(NetworkTopologyMember Member, string Text);
}
