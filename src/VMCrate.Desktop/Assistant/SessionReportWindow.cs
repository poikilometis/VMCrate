using System.Text.Json;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class SessionReportWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly ComboBox choices;
    private readonly TextBlock overview;
    private readonly ListBox events, files;
    private readonly Button explain, save;
    private AnalysisReport? report;
    private AssistantExplanation? interpretation;

    internal SessionReportWindow(Labels text, LabService lab, Guid? session = null) : base(text, "tools.sessionReport", 820, 675)
    {
        this.lab = lab;
        choices = Field("tools.session", new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id" });
        overview = Note("");
        var toolbar = Actions();
        Action(toolbar, "action.refresh", RefreshAsync);
        explain = Action(toolbar, "ai.explain", SummarizeAsync);
        save = Action(toolbar, "action.save", SaveAsync);
        Action(toolbar, "report.compare", CompareAsync);
        events = Field("label.history", new ListBox { Height = 145 });
        files = Field("label.files", new ListBox { Height = 125 });
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            var items = await Task.Run(lab.ReadSessionReportChoices);
            choices.ItemsSource = items;
            choices.SelectedValue = session;
            if (choices.SelectedIndex < 0)
                choices.SelectedIndex = items.Count > 0 ? 0 : -1;
            await RefreshAsync();
        });
        choices.SelectionChanged += (_, _) => Clear();
        Clear();
    }

    private void Clear()
    {
        report = null;
        interpretation = null;
        overview.Text = "";
        events.ItemsSource = files.ItemsSource = null;
        explain.IsEnabled = save.IsEnabled = false;
    }

    private async Task RefreshAsync()
    {
        Clear();
        if (choices.SelectedValue is not Guid id)
            return;
        report = await Task.Run(() => lab.BuildReportAsync(id, []));
        overview.Text = report.Machine is { } vm
            ? $"{vm.Name} · {Text["guest." + vm.Guest]} · {Text["network." + vm.Network]} · {Text["power." + vm.Power]}" : "";
        events.ItemsSource = (report.Events ?? []).Select(entry =>
            $"{entry.TimeUtc.ToLocalTime():g} · {entry.Operation}" + (entry.Code is null ? "" : $" · {Text[entry.Code]}")).ToArray();
        files.ItemsSource = report.Files.Select(file => $"{file.Name} · SHA-256 {file.Sha256}").ToArray();
        explain.IsEnabled = save.IsEnabled = true;
    }

    private Task SummarizeAsync()
    {
        if (report is not null)
            interpretation = AssistantWindow.Explain(this, Text, "Summarize the session timeline and available audit evidence", SessionFacts.Build(report));
        return Task.CompletedTask;
    }

    private async Task CompareAsync()
    {
        if (report is not { } current || SelectFile("report.compare", "JSON (*.json)|*.json") is not { } path)
            return;
        var changes = await Task.Run(() => ReportComparison.Compare(ReportComparison.Read(path), current));
        new ReportComparisonWindow(Text, changes) { Owner = this }.ShowDialog();
    }

    private async Task SaveAsync()
    {
        if (report is null || SaveFile("tools.sessionReport", "vmcrate-session.json") is not { } path)
            return;
        var document = new { report, interpretation };
        await Task.Run(() => OutputFile.WriteNew(path, ".json", output => JsonSerializer.Serialize(output, document, JsonDefaults.Options)));
    }
}
