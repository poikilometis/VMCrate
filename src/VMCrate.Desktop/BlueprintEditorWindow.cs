using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class BlueprintEditorWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly IReadOnlyList<Template> templates;
    private readonly ObservableCollection<NodeRow> rows = [];
    private readonly DataGrid grid;
    private readonly TextBox id;

    internal BlueprintEditorWindow(Labels text, LabService lab, IReadOnlyList<Template> templates, LabBlueprint? initial = null)
        : base(text, "tools.editor", 920, 610)
    {
        this.lab = lab;
        this.templates = templates;
        id = Field("label.lab", new TextBox { MaxLength = 32, FlowDirection = FlowDirection.LeftToRight });
        grid = Field("tab.machines", new DataGrid
        {
            Height = 300, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single, ItemsSource = rows
        });
        AddColumn("label.name", "Name", new DataGridLength(1.6, DataGridLengthUnitType.Star));
        grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = Text["label.template"],
            ItemsSource = templates.Select(item => item.Name).ToArray(),
            SelectedItemBinding = new Binding("Template"),
            Width = new DataGridLength(1.6, DataGridLengthUnitType.Star)
        });
        AddColumn("label.memory", "Memory", 100);
        AddColumn("label.cpu", "Cpu", 116);
        AddColumn("label.segment", "Segment", new DataGridLength(1, DataGridLengthUnitType.Star));
        var edits = Actions();
        Action(edits, "action.openBlueprint", OpenAsync);
        Action(edits, "action.addNode", AddAsync);
        Action(edits, "action.removeNode", RemoveAsync);
        Action(edits, "action.save", SaveAsync, true);
        if (initial is not null)
            LoadRows(initial);
    }

    private void AddColumn(string key, string property, DataGridLength width) =>
        grid.Columns.Add(new DataGridTextColumn { Header = Text[key], Binding = new Binding(property), Width = width });

    private Task AddAsync()
    {
        rows.Add(new NodeRow { Template = templates.FirstOrDefault()?.Name ?? "" });
        return Task.CompletedTask;
    }

    private Task RemoveAsync()
    {
        if (grid.SelectedItem is NodeRow item)
            rows.Remove(item);
        return Task.CompletedTask;
    }

    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog { Title = Text["action.openBlueprint"], Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true)
            return;
        LoadRows(await Task.Run(() => LabBlueprint.Read(dialog.FileName)));
    }

    private void LoadRows(LabBlueprint plan)
    {
        id.Text = plan.Id;
        rows.Clear();
        foreach (var vm in plan.Machines)
            rows.Add(new NodeRow { Name = vm.Name, Template = vm.Template, Memory = vm.MemoryGiB.ToString(),
                Cpu = vm.CpuCount.ToString(), Segment = vm.Segment ?? "" });
    }

    private async Task SaveAsync()
    {
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        var blueprint = new LabBlueprint(1, id.Text.Trim(),
            rows.Select(row => new BlueprintMachine(row.Name, row.Template, Integer(row.Memory),
                Integer(row.Cpu), string.IsNullOrWhiteSpace(row.Segment) ? null : row.Segment.Trim())).ToArray());
        await Task.Run(() => lab.ValidateBlueprint(blueprint));
        var path = SaveFile("tools.editor", blueprint.Id + ".json");
        if (path is not null)
            await Task.Run(() => OutputFile.WriteNew(path, ".json", stream =>
                JsonSerializer.Serialize(stream, blueprint, JsonDefaults.Options)));
    }

    private sealed class NodeRow
    {
        public string Name { get; set; } = "";
        public string Template { get; set; } = "";
        public string Memory { get; set; } = "4";
        public string Cpu { get; set; } = "2";
        public string Segment { get; set; } = "";
    }
}
