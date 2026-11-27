using System.Text.Json;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class WindowsInventoryWindow : WorkflowWindow
{
    internal WindowsInventoryWindow(Labels text, LabService lab, Template template, Guid? trial = null) : base(text, "inventory.collect", 640, 530)
    {
        Note(template.Name);
        Note(Text["inventory.windowsHint"]);
        var machines = Field("report.machine", new ComboBox { DisplayMemberPath = nameof(Machine.Name) });
        var toolbar = Actions();
        var collect = Action(toolbar, "inventory.collect", () =>
        {
            if (machines.SelectedItem is not Machine vm)
                return Task.CompletedTask;

            new IsolationGuestDialog(Text, async (user, password) =>
            {
                var inventory = await Task.Run(() => lab.ReadWindowsInventoryAsync(vm.Id, user, password));
                var output = SaveFile("inventory.collect", "windows-inventory.json");
                if (output is not null)
                    await Task.Run(() => OutputFile.WriteNew(output, ".json",
                        stream => JsonSerializer.Serialize(stream, inventory, JsonDefaults.Options)));
            }, "inventory.collect", "inventory.windowsHint") { Owner = this }.ShowDialog();
            return Task.CompletedTask;
        }, true);
        collect.IsEnabled = false;
        machines.SelectionChanged += (_, _) => collect.IsEnabled = machines.SelectedItem is Machine;
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            var snapshot = await Task.Run(lab.ObserveAsync);
            machines.ItemsSource = snapshot.Machines.Where(vm => (vm.TemplateId == template.Id || vm.Id == trial) &&
                !vm.Disposable && vm.Network == NetworkMode.Offline && snapshot.StateOf(vm.Id) == PowerState.Running).ToArray();
            machines.SelectedIndex = machines.Items.Count == 0 ? -1 : 0;
        });
    }
}
