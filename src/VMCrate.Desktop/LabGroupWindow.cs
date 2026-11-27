using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class LabGroupWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly ComboBox groups, operations;
    private readonly ListBox members;
    private readonly TextBlock summary;
    private readonly Button previewButton, applyButton;
    private LabSnapshot? snapshot;
    private LabOperationPreview? preview;

    internal LabGroupWindow(Labels text, LabService lab) : base(text, "tools.labs", 830)
    {
        this.lab = lab;
        var heading = Actions();
        Action(heading, "tools.editor", EditAsync);
        Action(heading, "tools.projects", ProjectAsync);
        Action(heading, "points.title", RecoveryPointsAsync);
        Action(heading, "tools.topology", TopologyAsync);
        Action(heading, "action.refresh", RefreshAsync);
        groups = Field("label.lab", new ComboBox());
        operations = Field("label.operation", new ComboBox { DisplayMemberPath = "Name" });
        operations.ItemsSource = new[]
        {
            new OperationItem(LabOperation.Start, Text["action.start"]),
            new OperationItem(LabOperation.PowerOff, Text["action.powerOff"]),
            new OperationItem(LabOperation.Reset, Text["action.reset"]),
            new OperationItem(LabOperation.Remove, Text["action.removeVm"])
        };
        operations.SelectedIndex = 0;
        summary = Note("");
        members = Field("tab.machines", new ListBox { Height = 220 });
        var actions = Actions();
        previewButton = Action(actions, "action.preview", PreviewAsync);
        applyButton = Action(actions, "action.apply", ApplyAsync, true);
        groups.SelectionChanged += (_, _) => Render();
        operations.SelectionChanged += (_, _) => Render();
        Render();
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var selected = groups.SelectedItem as string;
        snapshot = await Task.Run(lab.ObserveAsync);
        var ids = snapshot.Machines.Select(vm => vm.BlueprintId).OfType<string>().Distinct().Order().ToArray();
        groups.ItemsSource = ids;
        groups.SelectedItem = ids.Contains(selected) ? selected : ids.FirstOrDefault();
        Render();
    }

    private void Render()
    {
        preview = null;
        applyButton.IsEnabled = false;
        previewButton.IsEnabled = snapshot?.Host is not null && groups.SelectedItem is string;
        var entries = snapshot?.Machines.Where(vm => vm.BlueprintId == groups.SelectedItem as string && vm.BlueprintId is not null)
            .ToArray() ?? [];
        summary.Text = Text.Format("group.summary", entries.Length, entries.Sum(vm => vm.MemoryGiB), entries.Sum(vm => vm.CpuCount));
        members.ItemsSource = entries.Select(vm => FormatMember(vm.Name, vm.PrivateSegment,
            Text.Format("detail.resources", Text["power." + snapshot!.StateOf(vm.Id)], vm.MemoryGiB, vm.CpuCount))).ToArray();
    }

    private async Task PreviewAsync()
    {
        if (groups.SelectedItem is string id && operations.SelectedItem is OperationItem operation)
        {
            preview = await Task.Run(() => lab.PreviewLabOperationAsync(id, operation.Kind));
            summary.Text = Text.Format("group.preview", preview.Members.Count, preview.PrivateSegments.Count,
                MiB(preview.Members.Sum(vm => vm.Bytes)));
            members.ItemsSource = preview.Members.Select(vm => FormatMember(vm.Name, vm.Segment,
                Text["power." + vm.State] + " · " + MiB(vm.Bytes) + " MiB")).ToArray();
            applyButton.IsEnabled = true;
        }
    }

    private async Task ApplyAsync()
    {
        if (preview is not { } plan)
            return;
        if (plan.Operation != LabOperation.Start &&
            !Text.Confirm(this, "confirm.group", plan.Members.Count, plan.BlueprintId))
            return;
        try { await Task.Run(() => lab.ApplyLabOperationAsync(plan)); }
        finally { await RefreshAsync(); }
    }

    private Task TopologyAsync()
    {
        if (groups.SelectedItem is string id)
            new NetworkTopologyWindow(Text, lab, id) { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }

    private async Task RecoveryPointsAsync()
    {
        if (groups.SelectedItem is string id)
        {
            new DiskRecoveryWindow(Text, lab, new RecoveryPointScope(null, id)) { Owner = this }.ShowDialog();
            await RefreshAsync();
        }
    }

    private async Task ProjectAsync()
    {
        var plan = groups.SelectedItem is string id ? await Task.Run(() => lab.ReadDeployedBlueprint(id)) : null;
        new ProjectWindow(Text, lab, plan) { Owner = this }.ShowDialog();
    }

    private Task EditAsync()
    {
        new BlueprintEditorWindow(Text, lab, snapshot?.Templates ?? []) { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }

    private string FormatMember(string name, Guid? segment, string detail) =>
        $"{name} · " + (segment is Guid value ? Text.Format("blueprint.segment", value.ToString("N")[..8]) :
            Text["network.Offline"]) + " · " + detail;

    private sealed record OperationItem(LabOperation Kind, string Name);
}
