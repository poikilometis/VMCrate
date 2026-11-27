using System.IO;
using System.Windows.Controls;
using Microsoft.Win32;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class MachineTransferWindow : WorkflowWindow
{
    internal MachineTransferWindow(Labels text, LabService lab, LabSnapshot? snapshot) : base(text, "transfer.title", 730, 595)
    {
        Note(Text["transfer.hint"]);
        var machines = Field("tab.machines", new ComboBox { DisplayMemberPath = "Name" });
        machines.ItemsSource = snapshot?.Machines.Where(vm => !vm.Disposable && snapshot.StateOf(vm.Id) == PowerState.Off).ToArray();
        machines.SelectedIndex = 0;
        var notice = Note("");
        var export = Action(Actions(), "transfer.export", () => WithCancellationAsync(async token =>
        {
            if (machines.SelectedItem is not Machine machine || !Text.Confirm(this, "transfer.confirm", machine.Name))
                return;
            var folder = new OpenFolderDialog { Title = Text["transfer.export"] };
            if (folder.ShowDialog(this) != true)
                return;
            var directory = Path.Combine(folder.FolderName, $"VMCrate-{Guid.NewGuid():N}");
            await Task.Run(() => lab.ExportMachineAsync(machine.Id, directory, cancellation: token), token);
            notice.Text = Text.Format("transfer.saved", directory);
        }));
        export.IsEnabled = machines.SelectedItem is Machine;
        machines.SelectionChanged += (_, _) => export.IsEnabled = machines.SelectedItem is Machine;

        Body.Children.Add(new Separator());
        var name = Field("label.name", new TextBox { MaxLength = 64 });
        var ready = Note("");
        string? manifest = null;
        var importActions = Actions();
        var import = Action(importActions, "transfer.import", () => WithCancellationAsync(async token =>
        {
            if (manifest is null)
                return;
            var targetName = name.Text;
            await Task.Run(() => lab.ImportMachineAsync(manifest, targetName, cancellation: token), token);
            FinishDialog();
        }), true);
        import.IsEnabled = false;
        Action(importActions, "action.browse", () =>
        {
            if (SelectFile("transfer.import", "VMCrate (vm.json)|vm.json") is { } path)
            {
                var record = MachineTransfer.Read(path);
                name.Text = record.Name;
                ready.Text = $"{Text["guest." + record.Guest]} · {MiB(record.Bytes)} MiB";
                manifest = path;
                import.IsEnabled = true;
            }
            return Task.CompletedTask;
        });
    }
}
