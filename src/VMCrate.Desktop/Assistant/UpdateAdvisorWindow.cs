using System.Text.Json;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class UpdateAdvisorWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly ComboBox templates;
    private readonly TextBlock scope;
    private readonly ListBox results;
    private readonly Button explain, save, revisions, collect;
    private UpdateAdvice? advice;

    internal UpdateAdvisorWindow(Labels text, LabService lab, IReadOnlyList<Template> available, Guid? trial = null)
        : base(text, "tools.advisor", 820, 620)
    {
        this.lab = lab;
        Note(Text["advisor.hint"]);
        templates = Field("label.template", new ComboBox { DisplayMemberPath = "Name", ItemsSource = available });
        templates.SelectedIndex = available.Count > 0 ? 0 : -1;
        var toolbar = Actions();
        Action(toolbar, "advisor.check", CheckAsync, true).IsEnabled = available.Count != 0;
        Action(toolbar, "inventory.compare", () => CompareAsync(advisories: false));
        Action(toolbar, "inventory.compareAdvisories", () => CompareAsync(advisories: true));
        explain = Action(toolbar, "ai.explain", ExplainAsync);
        save = Action(toolbar, "action.save", SaveAsync);
        revisions = Action(toolbar, "tools.revisions", ShowRevisionsAsync);
        collect = Action(toolbar, "inventory.collect", () =>
        {
            if (templates.SelectedItem is Template template)
            {
                WorkflowWindow dialog = template.Guest == GuestFamily.Windows
                    ? new WindowsInventoryWindow(Text, lab, template, trial)
                    : new LinuxInventoryWindow(Text, lab, template, trial);
                dialog.Owner = this;
                dialog.ShowDialog();
            }
            return Task.CompletedTask;
        });
        scope = Note("");
        results = Field("advisor.results", new ListBox { Height = 230 });
        templates.SelectionChanged += (_, _) => Clear();
        Clear();
    }

    private void Clear()
    {
        advice = null;
        scope.Text = "";
        results.ItemsSource = null;
        explain.IsEnabled = save.IsEnabled = false;
        revisions.IsEnabled = templates.SelectedItem is Template;
        collect.IsEnabled = templates.SelectedItem is Template;
        collect.Content = Text[templates.SelectedItem is Template { Guest: GuestFamily.Linux }
            ? "inventory.linuxTitle" : "inventory.collect"];
    }

    private Task CheckAsync()
    {
        if (templates.SelectedItem is not VMCrate.Core.Template || SelectFile("advisor.check", "JSON (*.json)|*.json") is not { } path)
            return Task.CompletedTask;
        var inventory = PackageInventory.Read(path);
        if (!inventory.SupportsOsv)
            throw new LabException("inventory.localHint");
        if (!Text.Confirm(this, "advisor.confirm", inventory.Packages.Length, inventory.Ecosystem))
            return Task.CompletedTask;

        return WithCancellationAsync(async token =>
        {
            Clear();
            advice = await Task.Run(() => new AdvisoryClient().CheckAsync(inventory, token), token);
            scope.Text = $"{advice.Ecosystem} · {advice.CheckedUtc.ToLocalTime():g}" + Environment.NewLine +
                Text.Format("advisor.counts", advice.CheckedPackages, advice.Findings.Length, advice.DetailsChecked, advice.TotalAdvisories) +
                (advice.KevAvailable ? "" : Environment.NewLine + Text["advisor.kevUnavailable"]);
            results.ItemsSource = advice.Findings.Select(item =>
                $"{item.Package} {item.Installed} · {item.Advisory}" + (item.KnownExploited ? " · CISA KEV" : "") +
                (item.PublishedFixes.Length > 0 ? " · " + string.Join(", ", item.PublishedFixes) : "")).ToArray();
            explain.IsEnabled = save.IsEnabled = true;
        });
    }

    private async Task CompareAsync(bool advisories)
    {
        if (SelectFile("inventory.before", "JSON (*.json)|*.json") is not { } before ||
            SelectFile("inventory.after", "JSON (*.json)|*.json") is not { } after)
            return;
        var inventories = await Task.Run(() => (Before: PackageInventory.Read(before), After: PackageInventory.Read(after)));
        var changes = InventoryComparison.Compare(inventories.Before, inventories.After)
            .Select(item => new ReportChange(item.Name, string.Join(", ", item.Before), string.Join(", ", item.After))).ToList();
        if (advisories)
        {
            if (!inventories.Before.SupportsOsv || !inventories.After.SupportsOsv)
                throw new LabException("inventory.localHint");
            if (!Text.Confirm(this, "advisor.confirm", inventories.Before.Packages.Length + inventories.After.Packages.Length,
                inventories.Before.Ecosystem))
                return;
            await WithCancellationAsync(async token =>
            {
                var client = new AdvisoryClient();
                var previous = await client.CheckAsync(inventories.Before, token);
                var current = await client.CheckAsync(inventories.After, token);
                changes.AddRange(CompareAdvisoryMatches(previous, current));
                ShowComparison(changes, "inventory.compareAdvisories");
            });
            return;
        }
        ShowComparison(changes, "inventory.compare");
    }

    private void ShowComparison(IReadOnlyList<ReportChange> changes, string title) =>
        new ReportComparisonWindow(Text, changes, title)
            { Owner = this }.ShowDialog();

    private static IEnumerable<ReportChange> CompareAdvisoryMatches(UpdateAdvice before, UpdateAdvice after)
    {
        static Dictionary<(string Package, string Advisory), string> Matches(UpdateAdvice advice) => advice.Findings
            .GroupBy(item => (item.Package, item.Advisory))
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(item => item.Installed).Distinct().Order()));

        var previous = Matches(before);
        var current = Matches(after);
        return previous.Keys.Union(current.Keys).OrderBy(item => item.Package).ThenBy(item => item.Advisory)
            .Select(key => new ReportChange($"{key.Package} · {key.Advisory}", previous.GetValueOrDefault(key, ""),
                current.GetValueOrDefault(key, ""))).Where(change => change.Before != change.After);
    }

    private Task ExplainAsync()
    {
        if (advice is not null)
            AssistantWindow.Explain(this, Text, "Prioritize vendor-published package updates for a new template revision", advice.Facts());
        return Task.CompletedTask;
    }

    private async Task SaveAsync()
    {
        if (advice is null || templates.SelectedItem is not Template selected ||
            SaveFile("tools.advisor", "vmcrate-advisories.json") is not { } path)
            return;
        var document = new { template = selected.Name, selected.Sha256, advice };
        await Task.Run(() => OutputFile.WriteNew(path, ".json", output => JsonSerializer.Serialize(output, document, JsonDefaults.Options)));
    }

    private Task ShowRevisionsAsync()
    {
        if (templates.SelectedItem is Template item)
            new TemplateRevisionsWindow(Text, lab, item) { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }
}
