using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class DiskRecoveryWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly RecoveryPointScope scope;
    private readonly TextBox name;
    private readonly ListBox points;
    private readonly Button restore, remove;

    internal DiskRecoveryWindow(Labels text, LabService lab, RecoveryPointScope scope)
        : base(text, "points.title", 690, 520)
    {
        this.lab = lab;
        this.scope = scope;
        Note(Text["points.hint"]);
        name = Field("label.name", new TextBox { MaxLength = 64 });
        Action(Actions(), "points.capture", CaptureAsync);
        points = Field("points.title", new ListBox { DisplayMemberPath = nameof(Row.Caption), MinHeight = 120, MaxHeight = 250 });
        var buttons = Actions();
        restore = Action(buttons, "points.restore", RestoreAsync, true);
        remove = Action(buttons, "points.remove", RemoveAsync);
        points.SelectionChanged += (_, _) => SetButtons();
        SetButtons();
        Loaded += async (_, _) => await RunAsync(() => RefreshAsync());
    }

    private DiskRecoveryPoint? Selected => (points.SelectedItem as Row)?.Point;
    private void SetButtons() => restore.IsEnabled = remove.IsEnabled = Selected is not null;

    private async Task RefreshAsync(Guid? select = null)
    {
        var values = await Task.Run(() => lab.ReadRecoveryPoints(scope));
        var rows = values.Select(point => new Row(point,
            $"{point.Name} · {point.CreatedUtc.ToLocalTime():g} · {point.Disks.Length} VM · {MiB(point.Bytes)} MiB")).ToArray();
        points.ItemsSource = rows;
        points.SelectedItem = rows.FirstOrDefault(row => row.Point.Id == select) ?? rows.FirstOrDefault();
        SetButtons();
    }

    private Task CaptureAsync()
    {
        var label = name.Text;
        return WithCancellationAsync(async token =>
        {
            var point = await Task.Run(() => lab.CaptureRecoveryPointAsync(scope, label, token));
            name.Clear();
            await RefreshAsync(point.Id);
        });
    }

    private Task RestoreAsync() => Selected is { } point &&
        Text.Confirm(this, "points.confirm", point.Name, point.Disks.Length)
            ? WithCancellationAsync(token => Task.Run(() => lab.RestoreRecoveryPointAsync(point.Id, token)))
            : Task.CompletedTask;

    private async Task RemoveAsync()
    {
        if (Selected is not { } point || !Text.Confirm(this, "points.confirmRemove", point.Name))
            return;
        await Task.Run(() => lab.DeleteRecoveryPoint(point.Id));
        await RefreshAsync();
    }

    private sealed record Row(DiskRecoveryPoint Point, string Caption);
}
