using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class DescriptionBlueprintWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly IReadOnlyList<Template> templates;
    private readonly TextBox description;
    private readonly ListBox machines;
    private readonly Button edit, preview;
    private LabBlueprint? blueprint;

    internal DescriptionBlueprintWindow(Labels text, LabService lab, IReadOnlyList<Template> templates)
        : base(text, "tools.aiBlueprint", 820, 625)
    {
        this.lab = lab;
        this.templates = templates;
        Note(Text["ai.blueprintHint"]);
        description = Field("ai.description", new TextBox
        {
            AcceptsReturn = true, Height = 115, TextWrapping = TextWrapping.Wrap, MaxLength = 1800,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        var actions = Actions();
        Action(actions, "ai.generate", GenerateAsync, true);
        edit = Action(actions, "tools.editor", EditAsync);
        preview = Action(actions, "action.preview", PreviewAsync);
        edit.IsEnabled = preview.IsEnabled = false;
        machines = Field("tab.machines", new ListBox { Height = 175 });
    }

    private Task GenerateAsync()
    {
        var work = new DescriptionBlueprint(templates);
        new AssistantWindow(Text, work.Request(description.Text), reply =>
        {
            var candidate = work.Parse(reply);
            lab.ValidateBlueprint(candidate);
            blueprint = candidate;
            return string.Join(Environment.NewLine, Rows(candidate));
        }) { Owner = this }.ShowDialog();
        if (blueprint is { } result)
        {
            machines.ItemsSource = Rows(result);
            edit.IsEnabled = preview.IsEnabled = true;
        }
        return Task.CompletedTask;
    }

    private string[] Rows(LabBlueprint plan) => plan.Machines.Select(machine =>
        $"{machine.Name} · {machine.Template} · {machine.MemoryGiB} GiB · {machine.CpuCount} vCPU · " +
        (machine.Segment ?? Text["network.Offline"])).ToArray();

    private async Task PreviewAsync()
    {
        if (blueprint is not { } plan)
            return;
        var ready = await Task.Run(() => lab.PreviewBlueprintAsync(plan));
        if (new BlueprintDialog(Text, ready) { Owner = this }.ShowDialog() == true)
            await Task.Run(() => lab.DeployBlueprintAsync(plan));
    }

    private Task EditAsync()
    {
        if (blueprint is not null)
            new BlueprintEditorWindow(Text, lab, templates, blueprint) { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }
}
