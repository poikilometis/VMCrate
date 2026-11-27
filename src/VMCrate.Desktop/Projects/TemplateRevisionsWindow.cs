using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class TemplateRevisionsWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly Guid selectedTemplate;
    private readonly ListBox revisions;
    private readonly TextBlock detail;
    private readonly Button build, verify, rollout, trial, prefer;

    internal TemplateRevisionsWindow(Labels text, LabService lab, Template template)
        : base(text, "tools.revisions", 740, 600)
    {
        this.lab = lab;
        selectedTemplate = template.Id;
        Note(Text["revision.hint"]);
        revisions = Field("tab.templates", new ListBox { Height = 195, DisplayMemberPath = "Name" });
        detail = Note("");
        var toolbar = Actions();
        trial = Action(toolbar, "revision.trial", TrialAsync, true);
        build = Action(toolbar, "revision.build", BuildAsync);
        verify = Action(toolbar, "revision.verify", VerifyAsync);
        prefer = Action(toolbar, "revision.prefer", PreferAsync);
        rollout = Action(toolbar, "revision.rollout", RolloutAsync);
        Action(toolbar, "action.refresh", RefreshAsync);
        revisions.SelectionChanged += (_, _) => Render();
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
        Render();
    }

    private TemplateRevision? Selected => (revisions.SelectedItem as Row)?.Item;

    private async Task RefreshAsync()
    {
        var selected = Selected?.Template.Id ?? selectedTemplate;
        var data = await Task.Run(() => lab.ReadRevisions(selectedTemplate));
        var rows = data.Select(item => new Row(item, $"{item.Template.Name} · {item.Template.Revision}" +
            (item.Template.Preferred ? " · " + Text["revision.preferred"] : ""))).ToArray();
        revisions.ItemsSource = rows;
        revisions.SelectedItem = rows.FirstOrDefault(row => row.Item.Template.Id == selected) ?? rows.FirstOrDefault();
        Render();
    }

    private void Render()
    {
        build.IsEnabled = verify.IsEnabled = trial.IsEnabled = Selected is not null;
        rollout.IsEnabled = Selected?.Template.BootVerifiedUtc is not null;
        prefer.IsEnabled = Selected?.Template is { BootVerifiedUtc: not null, Preferred: false };
        if (Selected is not { } item)
        {
            detail.Text = "";
            return;
        }
        var time = item.Template.BootVerifiedUtc?.ToLocalTime().ToString("g") ?? "—";
        var created = item.Template.CreatedUtc is { } built
            ? Text.Format("revision.age", built.ToLocalTime().ToString("g"), Math.Max(0, (int)(DateTimeOffset.UtcNow - built).TotalDays))
            : "—";
        var parent = revisions.Items.OfType<Row>().SingleOrDefault(row => row.Item.Template.Id == item.Template.Lineage?.PreviousId);
        detail.Text = created + Environment.NewLine + Text.Format("revision.verified", time) + Environment.NewLine +
            (parent is null ? "" : $"{parent.Name} → {item.Template.Name} · {item.Template.Sha256?[..12] ?? "—"}{Environment.NewLine}") +
            Text.Format("revision.dependents", item.Dependents.Length) + Environment.NewLine +
            string.Join(Environment.NewLine, item.Dependents);
    }

    private async Task BuildAsync()
    {
        if (Selected is { } item &&
            new NewTemplateBuildWindow(Text, lab, parent: item.Template) { Owner = this }.ShowDialog() == true)
        {
            new TemplateBuildWindow(Text, lab) { Owner = this }.ShowDialog();
            await RefreshAsync();
        }
    }

    private async Task RolloutAsync()
    {
        if (Selected is not { Template: { BootVerifiedUtc: not null } target })
            return;
        new RevisionRolloutWindow(Text, lab, target) { Owner = this }.ShowDialog();
        await RefreshAsync();
    }

    private async Task TrialAsync()
    {
        if (Selected is not { } item)
            return;
        var form = new RevisionTrialWindow(Text, lab, item.Template) { Owner = this };
        if (form.ShowDialog() != true || form.Queued is not { } job)
            return;
        new TemplateBuildWindow(Text, lab, job.Id) { Owner = this }.ShowDialog();
        await RefreshAsync();
        revisions.SelectedItem = revisions.Items.OfType<Row>().FirstOrDefault(row => row.Item.Template.Id == job.Id)
            ?? revisions.SelectedItem;
    }

    private Task PreferAsync()
    {
        if (Selected is not { } item || !Text.Confirm(this, "revision.confirmPrefer", item.Template.Name))
            return Task.CompletedTask;
        return WithCancellationAsync(async token =>
        {
            await Task.Run(() => lab.PreferRevisionAsync(item.Template.Id, token), token);
            await RefreshAsync();
        });
    }

    private Task VerifyAsync()
    {
        if (Selected is not { } item)
            return Task.CompletedTask;
        return WithCancellationAsync(async token =>
        {
            await Task.Run(() => lab.VerifyRevisionAsync(item.Template.Id, token), token);
            await RefreshAsync();
        });
    }

    private sealed record Row(TemplateRevision Item, string Name);
}
