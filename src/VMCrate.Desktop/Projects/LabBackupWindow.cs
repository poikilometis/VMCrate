using System.IO;
using System.Windows.Controls;
using Microsoft.Win32;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class LabBackupWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly ComboBox groups;
    private readonly TextBlock status;
    private readonly ListBox contents;
    private readonly Button restore;
    private string? manifest;
    private LabBackupPreview? preview;

    internal LabBackupWindow(Labels text, LabService lab, IReadOnlyList<Machine> machines) : base(text, "backup.title", 760, 640)
    {
        this.lab = lab;
        Note(Text["backup.hint"]);
        groups = Field("tools.labs", new ComboBox { ItemsSource = machines.Select(vm => vm.BlueprintId).OfType<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() });
        groups.SelectedIndex = 0;
        var actions = Actions();
        Action(actions, "backup.save", () => ExportAsync(false)).IsEnabled = groups.Items.Count > 0;
        Action(actions, "jobs.background", () => ExportAsync(true)).IsEnabled = groups.Items.Count > 0;
        Action(actions, "backup.preview", PreviewAsync, primary: true);
        Action(actions, "backup.drill", DrillAsync);
        Action(actions, "backup.drillReceipt", ReadDrillAsync);
        restore = Action(actions, "backup.restore", RestoreAsync);
        restore.IsEnabled = false;
        status = Note("");
        contents = Field("tab.machines", new ListBox { MinHeight = 150, MaxHeight = 260 });
    }

    private async Task ExportAsync(bool background)
    {
        if (groups.SelectedItem is not string labId)
            return;
        var folder = new OpenFolderDialog { Title = Text["backup.save"], Multiselect = false };
        if (folder.ShowDialog(this) != true)
            return;
        var target = Path.Combine(folder.FolderName, $"vmcrate-{labId}-{DateTime.Now:yyyyMMdd-HHmmss}");
        if (background)
        {
            await Task.Run(() => LocalJobProcess.Enqueue(lab, new(LocalJobKind.Backup, Blueprint: labId, Destination: target)));
            new LocalJobsWindow(Text, lab) { Owner = this }.ShowDialog();
            return;
        }
        await WithCancellationAsync(async token =>
        {
            await Task.Run(() => lab.BackupLabAsync(labId, target, token), token);
            status.Text = Text["status.done"] + Environment.NewLine + Path.Combine(target, LabBackup.ManifestName);
        });
    }

    private Task PreviewAsync()
    {
        if (SelectFile("backup.preview", "JSON (*.json)|*.json") is not { } file)
            return Task.CompletedTask;
        manifest = null;
        preview = null;
        restore.IsEnabled = false;
        contents.ItemsSource = null;
        status.Text = "";
        return WithCancellationAsync(async token =>
        {
            var result = await Task.Run(() => lab.PreviewLabBackupAsync(file, token), token);
            manifest = file;
            preview = result;
            ShowPreview(result);
        });
    }

    private void ShowPreview(LabBackupPreview result)
    {
        var backup = result.Backup;
        status.Text = Text.Format("backup.summary", backup.BlueprintId, backup.Machines.Length, backup.Templates.Length) +
            Environment.NewLine + Text.Format("backup.space", MiB(result.RequiredBytes),
                result.FreeBytes is long size ? MiB(size) : "—", result.AlreadyRestored);
        contents.ItemsSource = backup.Machines.Select(vm =>
            $"{vm.Name} · {backup.Templates.Single(item => item.Id == vm.TemplateId).Name} · {Text["network." + vm.Network]}" +
            (vm.PrivateSegment is Guid segment ? $" · {segment.ToString("N")[..8]}" : "")).ToArray();
        restore.IsEnabled = true;
    }

    private Task RestoreAsync()
    {
        if (manifest is not { } file || preview is not { } result ||
            !Text.Confirm(this, "backup.confirm", result.Backup.BlueprintId))
            return Task.CompletedTask;
        return WithCancellationAsync(async token =>
        {
            await Task.Run(() => lab.RestoreLabBackupAsync(file, result, token), token);
            status.Text = Text["backup.restored"];
            restore.IsEnabled = false;
        });
    }

    private async Task DrillAsync()
    {
        if (SelectFile("backup.drill", "JSON (*.json)|*.json") is not { } file)
            return;
        await Task.Run(() => LocalJobProcess.Enqueue(lab, new(LocalJobKind.RestoreDrill, Destination: file)));
        new LocalJobsWindow(Text, lab) { Owner = this }.ShowDialog();
    }

    private async Task ReadDrillAsync()
    {
        if (SelectFile("backup.drillReceipt", "JSON (*.json)|*.json") is not { } file)
            return;
        var result = await Task.Run(() => lab.ReadRestoreDrill(file));
        status.Text = result is null ? Text["backup.drillNone"] : Text.Format("backup.drillDone",
            result.CompletedUtc.ToLocalTime().ToString("g"), result.Booted, (result.ElapsedMs / 1000d).ToString("N1"));
    }
}
