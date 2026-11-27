using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class LinuxInventoryWindow : WorkflowWindow
{
    internal LinuxInventoryWindow(Labels text, LabService lab, Template template) : base(text, "inventory.linuxTitle", 690, 485)
    {
        Note(Text["inventory.linuxHint"]);
        var command = new TextBox
        {
            Text = "sh /media/cdrom/VMCrate-inventory.sh > vmcrate-linux-inventory.json",
            IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, TextWrapping = TextWrapping.Wrap
        };
        Body.Children.Add(command);
        var machines = Field("tab.machines", new ComboBox { DisplayMemberPath = "Name" });
        var actions = Actions();
        var mount = Action(actions, "inventory.mount", async () =>
        {
            if (machines.SelectedItem is not Machine machine)
                return;
            await Task.Run(() => lab.MountLinuxInventoryCollectorAsync(template.Id, machine.Id));
            MessageBox.Show(this, Text["inventory.mounted"], Title);
        }, true);
        mount.IsEnabled = false;
        machines.SelectionChanged += (_, _) => mount.IsEnabled = machines.SelectedItem is Machine;
        Action(actions, "inventory.saveCollector", async () =>
        {
            if (SaveFile("inventory.saveCollector", "VMCrate-inventory.sh", ".sh") is { } path)
                await Task.Run(() => LabService.SaveLinuxInventoryCollector(path));
        });
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            var snapshot = await Task.Run(lab.ObserveAsync);
            machines.ItemsSource = snapshot.Machines.Where(vm => vm is { Disposable: false, Network: NetworkMode.Offline } &&
                vm.TemplateId == template.Id).ToArray();
            machines.SelectedIndex = 0;
        });
    }
}
