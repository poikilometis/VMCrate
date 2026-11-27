using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class OperationJournalWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly ListBox history;

    internal OperationJournalWindow(Labels text, LabService lab) : base(text, "tools.journal", 750, 570)
    {
        this.lab = lab;
        Action(Actions(), "action.refresh", RefreshAsync, true);
        history = Field("label.history", new ListBox { Height = 330 });
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var data = await Task.Run(async () => (Entries: lab.ReadOperations(), Catalog: await lab.ObserveAsync(),
            Builds: lab.ReadBuilds(), Sessions: lab.ReadSessionReportChoices()));
        var names = data.Catalog.Machines.Select(item => (item.Id, item.Name))
            .Concat(data.Catalog.Templates.Select(item => (item.Id, item.Name)))
            .Concat(data.Builds.Select(item => (item.Id, item.Options.Name)))
            .Concat(data.Sessions.Select(item => (item.Id, item.Name)))
            .DistinctBy(item => item.Id).ToDictionary(item => item.Id, item => item.Name);
        history.ItemsSource = data.Entries.Reverse().Select(item =>
            $"{item.TimeUtc.ToLocalTime():g} · {names.GetValueOrDefault(item.Resource, item.Resource.ToString("N")[..8])} · " +
            $"{item.Operation}" + (item.Code is null ? "" : $" · {Text[item.Code]}") +
            (item.ElapsedMs is long elapsed ? $" · {elapsed / 1000d:N1} s" : "")).ToArray();
    }
}
