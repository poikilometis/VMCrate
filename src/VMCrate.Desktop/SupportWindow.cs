using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

internal sealed class SupportWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly ComboBox targets;
    private readonly TextBox preview;
    private readonly Button export;
    private SupportPackage? current;

    internal SupportWindow(Labels text, LabService lab, IReadOnlyList<Machine> machines)
        : base(text, "tools.support", 740, 710)
    {
        this.lab = lab;
        Note(Text["support.contains"]);
        targets = Field("report.machine", new ComboBox { DisplayMemberPath = nameof(Target.Name) });
        targets.ItemsSource = machines.Select(vm => new Target(vm.Name, vm.Id))
            .Prepend(new Target(Text["support.workspace"], null)).ToArray();
        targets.SelectedIndex = 0;
        var actions = Actions();
        Action(actions, "action.refresh", PrepareAsync, primary: true);
        export = Action(actions, "action.vmReport", ExportAsync);
        Action(actions, "support.bugDraft", () =>
        {
            if (current is { } report)
                new BugDraftWindow(Text, report) { Owner = this }.ShowDialog();
            return Task.CompletedTask;
        });
        Action(actions, "support.disclosure", () =>
        {
            var path = LocalPaths.RequireFile(Path.Combine(AppContext.BaseDirectory, "SECURITY.md"), ".md");
            using var reader = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        export.IsEnabled = false;
        preview = Field("support.preview", new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 340, FlowDirection = FlowDirection.LeftToRight
        });
        targets.SelectionChanged += (_, _) => Clear();
        Loaded += async (_, _) => await RunAsync(PrepareAsync);
    }

    private void Clear()
    {
        export.IsEnabled = false;
        current = null;
        preview.Clear();
    }

    private async Task PrepareAsync()
    {
        Clear();
        var id = (targets.SelectedItem as Target)?.Id;
        current = await Task.Run(async () =>
        {
            var readiness = await new PowerShellHypervisor().ReadHostReadinessAsync();
            return await lab.BuildSupportPackageAsync(readiness, id);
        });
        preview.Text = current.Preview();
        export.IsEnabled = true;
    }

    private Task ExportAsync()
    {
        var path = SaveFile("tools.support", $"vmcrate-support-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip", ".zip");
        return path is null || current is not { } report ? Task.CompletedTask : Task.Run(() => report.Export(path));
    }

    private sealed record Target(string Name, Guid? Id);
}
