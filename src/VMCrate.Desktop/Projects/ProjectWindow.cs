using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class ProjectWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly CheckBox include;
    private readonly Button export, import, lockDependencies;
    private readonly TextBlock overview;
    private readonly ListBox items;
    private LabBlueprint? blueprint;
    private ProjectPreview? preview;
    private string? source;

    internal ProjectWindow(Labels text, LabService lab, LabBlueprint? initial = null) : base(text, "tools.projects", 820, 635)
    {
        this.lab = lab;
        blueprint = initial;
        Note(Text["project.hint"]);
        var toolbar = Actions();
        Action(toolbar, "action.openBlueprint", OpenBlueprintAsync);
        Action(toolbar, "project.open", OpenArchiveAsync);
        Action(toolbar, "library.copy", () =>
        {
            new LibraryCopyWindow(Text, lab) { Owner = this }.ShowDialog();
            return Task.CompletedTask;
        });
        include = new CheckBox { Content = Text["project.disks"], Margin = new Thickness(0, 12, 0, 8) };
        Body.Children.Add(include);
        overview = Note("");
        items = Field("tab.machines", new ListBox { Height = 175 });
        var operations = Actions();
        export = Action(operations, "project.export", ExportAsync);
        import = Action(operations, "project.import", ImportAsync, true);
        Action(operations, "project.checkKit", CheckKitAsync);
        lockDependencies = Action(operations, "project.lock", LockAsync);
        Render();
    }

    private async Task OpenBlueprintAsync()
    {
        if (SelectFile("action.openBlueprint", "JSON (*.json)|*.json") is not { } path)
            return;
        blueprint = await Task.Run(() => LabBlueprint.Read(path));
        preview = null;
        source = null;
        Render();
    }

    private async Task OpenArchiveAsync()
    {
        if (SelectFile("project.open", "ZIP (*.zip)|*.zip") is not { } path)
            return;
        preview = await Task.Run(() => lab.PreviewProject(path));
        blueprint = null;
        source = path;
        Render();
    }

    private void Render()
    {
        export.IsEnabled = include.IsEnabled = lockDependencies.IsEnabled = blueprint is not null;
        import.IsEnabled = preview?.Ready == true;
        if (preview is { } check)
        {
            overview.Text = $"{check.Blueprint.Id} · {MiB(check.ImportBytes)} MiB";
            items.ItemsSource = check.Disks.Select(item =>
                $"{item.Disk.Name} · {item.Disk.Revision} · " +
                (item.LocalName ?? Text[item.Disk.Included ? "project.embedded" : "project.missing"])).ToArray();
        }
        else
        {
            overview.Text = blueprint?.Id ?? "";
            items.ItemsSource = blueprint?.Machines.Select(vm => $"{vm.Name} · {vm.Template} · " +
                (vm.Segment ?? Text["network.Offline"])).ToArray();
        }
    }

    private Task ExportAsync()
    {
        if (blueprint is not { } plan || SaveFile("project.export", plan.Id + ".zip", ".zip") is not { } path)
            return Task.CompletedTask;
        var disks = include.IsChecked == true;
        return WithCancellationAsync(token => Task.Run(() => lab.ExportProjectAsync(plan, path, disks, token), token));
    }

    private Task LockAsync()
    {
        if (blueprint is not { } value || SaveFile("project.lock", value.Id + ".lock.json") is not { } path)
            return Task.CompletedTask;
        return WithCancellationAsync(token => Task.Run(() => OfflinePlanning.SaveLockAsync(path, lab.WorkspaceDirectory, value, token)));
    }

    private Task CheckKitAsync()
    {
        var path = source ?? SelectFile("project.checkKit", "ZIP (*.zip)|*.zip");
        if (path is null)
            return Task.CompletedTask;
        return WithCancellationAsync(async token =>
        {
            var check = await Task.Run(() => OfflinePlanning.CheckArchiveAsync(path, token));
            overview.Text = $"{check.Id} · {Text[check.SelfContained ? "trace.check.Passed" : "project.missing"]} · {MiB(check.VerifiedBytes)} MiB";
            items.ItemsSource = check.Dependencies.Select(disk =>
                $"{disk.Name} · {disk.Revision} · {Text[disk.Included ? "project.embedded" : "project.missing"]}").ToArray();
        });
    }

    private Task ImportAsync()
    {
        if (preview is not { Ready: true } plan || source is not { } path)
            return Task.CompletedTask;
        return WithCancellationAsync(async token =>
        {
            var imported = await Task.Run(() => lab.ImportProjectAsync(path, plan, token), token);
            var snapshot = await Task.Run(lab.ObserveAsync);
            new BlueprintEditorWindow(Text, lab, snapshot.Templates, imported) { Owner = this }.ShowDialog();
            preview = await Task.Run(() => lab.PreviewProject(path));
            Render();
        });
    }
}
