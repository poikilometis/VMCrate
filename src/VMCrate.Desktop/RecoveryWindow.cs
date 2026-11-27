using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class RecoveryWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly ListBox findings;
    private readonly TextBlock detail;
    private readonly Button repair;

    internal RecoveryWindow(Labels text, LabService lab) : base(text, "tools.recovery")
    {
        this.lab = lab;
        Note(Text["hint.recovery"]);
        findings = Field("recovery.findings", new ListBox { DisplayMemberPath = "Caption", Height = 228 });
        detail = Note("");
        var buttons = Actions();
        Action(buttons, "action.refresh", RefreshAsync);
        repair = Action(buttons, "recovery.apply", ApplyAsync, true);
        findings.SelectionChanged += (_, _) => Render();
        Render();
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    private RecoveryItem? Selected => (findings.SelectedItem as Row)?.Item;

    private void Render()
    {
        var selected = Selected;
        repair.IsEnabled = selected?.Kind is RecoveryKind.RestoreCatalog or RecoveryKind.ResumeProvision or
            RecoveryKind.RecreateMissingClone or RecoveryKind.RestoreIsolation or RecoveryKind.EmptySegment or
            RecoveryKind.ResumeBuild or RecoveryKind.ResumeDiskRestore;
        detail.Text = selected is null ? "" : string.Join(Environment.NewLine, new[]
        {
            selected.Name,
            selected.Detail?.StartsWith("audit.", StringComparison.Ordinal) == true ||
            selected.Detail?.StartsWith("build.", StringComparison.Ordinal) == true ||
            selected.Detail?.StartsWith("error.", StringComparison.Ordinal) == true
                ? Text[selected.Detail] : selected.Detail
        }.Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private async Task RefreshAsync()
    {
        var previous = Selected;
        var items = await Task.Run(lab.InspectRecoveryAsync);
        var rows = items.Select(item => new Row(item, Text["recovery." + item.Kind] +
            (item.Name is null ? "" : $" · {item.Name}"))).ToArray();
        findings.ItemsSource = rows;
        findings.SelectedItem = rows.FirstOrDefault(row => row.Item == previous) ?? rows.FirstOrDefault();
        Render();
        if (rows.Length == 0)
            detail.Text = Text["recovery.clear"];
    }

    private async Task ApplyAsync()
    {
        if (Selected is not { } selected ||
            !Text.Confirm(this, "confirm.recovery", Text["recovery." + selected.Kind], selected.Name ?? "", detail.Text))
            return;

        try
        {
            await Task.Run(() => selected.Kind switch
            {
                RecoveryKind.RestoreCatalog => RunRestoreAsync(selected.Id),
                RecoveryKind.ResumeProvision => lab.ResumeProvisionAsync(selected.Id),
                RecoveryKind.RecreateMissingClone => lab.RecreateMissingCloneAsync(selected.Id),
                RecoveryKind.ResumeBuild => lab.ContinueBuildAsync(selected.Id),
                RecoveryKind.ResumeDiskRestore => lab.RestoreRecoveryPointAsync(selected.Id),
                RecoveryKind.RestoreIsolation => lab.RestoreIsolationAsync(selected.Id),
                RecoveryKind.EmptySegment => lab.RemoveUnusedPrivateSegmentAsync(selected.Id),
                _ => Task.CompletedTask
            });
        }
        finally { await RefreshAsync(); }
    }

    private Task RunRestoreAsync(Guid workspace)
    {
        lab.RestoreCatalogBackup(workspace);
        return Task.CompletedTask;
    }

    private sealed record Row(RecoveryItem Item, string Caption);
}
