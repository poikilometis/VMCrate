using System.Globalization;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class OutboxWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly ComboBox sources, receivers;
    private readonly ListBox transfers;
    private readonly TextBox capacity;
    private readonly TextBox claims;
    private readonly TextBlock details;
    private readonly Button prepare, receive, archive;
    private OutboxTransfer? Selected => (transfers.SelectedItem as TransferRow)?.Transfer;

    internal OutboxWindow(Labels text, LabService lab, Guid? source = null) : base(text, "outbox.title", 720, 645)
    {
        this.lab = lab;
        Note(Text["outbox.hint"]);
        sources = Field("outbox.source", new ComboBox { DisplayMemberPath = nameof(Machine.Name) });
        receivers = Field("outbox.receiver", new ComboBox { DisplayMemberPath = nameof(Machine.Name) });
        capacity = Field("outbox.capacity", new TextBox { Text = "256", MaxLength = 4 });
        prepare = Action(Actions(), "outbox.prepare", PrepareAsync, primary: true);
        transfers = Field("outbox.title", new ListBox { DisplayMemberPath = nameof(TransferRow.Label), Height = 122 });
        details = Note("");
        claims = Field("outbox.claims", new TextBox { AcceptsReturn = true, AcceptsTab = true, MaxLength = 42000,
            Height = 54, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var actions = Actions();
        receive = Action(actions, "outbox.receive", ReceiveAsync);
        archive = Action(actions, "outbox.archive", ArchiveAsync);
        Action(actions, "action.refresh", () => RefreshAsync());
        transfers.SelectionChanged += (_, _) => { claims.Clear(); Render(); };
        sources.SelectionChanged += (_, _) => Render();
        receivers.SelectionChanged += (_, _) => Render();
        Loaded += async (_, _) => await RunAsync(() => RefreshAsync(source));
        Render();
    }

    private async Task RefreshAsync(Guid? source = null)
    {
        var sourceId = source ?? (sources.SelectedItem as Machine)?.Id;
        var receiverId = (receivers.SelectedItem as Machine)?.Id;
        var selected = Selected?.Id;
        var snapshot = await Task.Run(lab.ObserveAsync);
        var machines = snapshot.Machines.Where(vm => vm.Network == NetworkMode.Offline && vm.TemplateId is not null &&
            snapshot.StateOf(vm.Id) is PowerState.Off or PowerState.Running).ToArray();
        sources.ItemsSource = machines;
        sources.SelectedItem = machines.FirstOrDefault(vm => vm.Id == sourceId);
        var eligible = machines.Where(vm => !vm.Disposable).ToArray();
        receivers.ItemsSource = eligible;
        receivers.SelectedItem = eligible.FirstOrDefault(vm => vm.Id == receiverId);
        var rows = (await Task.Run(lab.ReadOutboxes)).OrderByDescending(item => item.CreatedUtc)
            .Select(item => new TransferRow(item, $"{item.SourceName} → {item.ReceiverName} · {Text["outbox." + item.Status]}")).ToArray();
        transfers.ItemsSource = rows;
        transfers.SelectedItem = rows.FirstOrDefault(row => row.Transfer.Id == selected) ?? rows.FirstOrDefault();
        Render();
    }

    private void Render()
    {
        prepare.IsEnabled = sources.SelectedItem is Machine source && receivers.SelectedItem is Machine target && source.Id != target.Id;
        var item = Selected;
        receive.IsEnabled = item?.Status is OutboxStatus.Writing or OutboxStatus.Sealed;
        archive.IsEnabled = item is not null && item.Status != OutboxStatus.Archived;
        claims.IsEnabled = item?.Status == OutboxStatus.Writing;
        details.Text = item?.Evidence is { } evidence ? $"SHA-256  {evidence.Sha256}\n{MiB(evidence.SizeBytes)} MiB" : "";
        if (item?.DeclaredFiles is { Length: > 0 } files)
            details.Text += Environment.NewLine + Text["isolation.guestReport"] + Environment.NewLine +
                string.Join(Environment.NewLine, files.Select(file => $"{file.Name} · {file.SizeBytes:N0} B · {file.Sha256}"));
    }

    private async Task PrepareAsync()
    {
        if (sources.SelectedItem is Machine source && receivers.SelectedItem is Machine receiver)
        {
            var size = Integer(capacity.Text);
            await Task.Run(() => lab.PrepareOutboxAsync(source.Id, receiver.Id, size));
            await RefreshAsync();
        }
    }

    private Task ReceiveAsync()
    {
        if (Selected is not { } item || !Text.Confirm(this, "outbox.confirmReceive", item.SourceName, item.ReceiverName))
            return Task.CompletedTask;
        var files = item.Status == OutboxStatus.Writing ? claims.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line)).Select(ParseClaim).ToArray() : null;
        return WithCancellationAsync(async token =>
        {
            await Task.Run(() => lab.ReceiveOutboxAsync(item.Id, token, files), token);
            await RefreshAsync();
        });
    }

    private static FileEvidence ParseClaim(string line) =>
        line.TrimEnd('\r').Split('\t') is [var name, var size, var hash] &&
        long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            ? new(name, bytes, hash) : throw new LabException("error.outboxClaims");

    private async Task ArchiveAsync()
    {
        if (Selected is not { } item || !Text.Confirm(this, "outbox.confirmArchive"))
            return;
        await Task.Run(() => lab.ArchiveOutboxAsync(item.Id));
        await RefreshAsync();
    }

    private sealed record TransferRow(OutboxTransfer Transfer, string Label);
}
