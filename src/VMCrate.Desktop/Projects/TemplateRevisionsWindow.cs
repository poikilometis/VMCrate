using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class TemplateRevisionsWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly Guid selectedTemplate;
    private readonly ListBox revisions;
    private readonly TextBlock detail;
    private readonly Button build, verify, rollout;

    internal TemplateRevisionsWindow(Labels text, LabService lab, Template template)
        : base(text, "tools.revisions", 740, 600)
    {
        this.lab = lab;
        selectedTemplate = template.Id;
        Note(Text["revision.hint"]);
        revisions = Field("tab.templates", new ListBox { Height = 195, DisplayMemberPath = "Name" });
        detail = Note("");
        var toolbar = Actions();
        build = Action(toolbar, "revision.build", BuildAsync, true);
        verify = Action(toolbar, "revision.verify", VerifyAsync);
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
        var rows = data.Select(item => new Row(item, $"{item.Template.Name} · {item.Template.Revision}")).ToArray();
        revisions.ItemsSource = rows;
        revisions.SelectedItem = rows.FirstOrDefault(row => row.Item.Template.Id == selected) ?? rows.FirstOrDefault();
        Render();
    }

    private void Render()
    {
        build.IsEnabled = verify.IsEnabled = Selected is not null;
        rollout.IsEnabled = Selected?.Template.BootVerifiedUtc is not null;
        if (Selected is not { } item)
        {
            detail.Text = "";
            return;
        }
        var time = item.Template.BootVerifiedUtc?.ToLocalTime().ToString("g") ?? "—";
        detail.Text = Text.Format("revision.verified", time) + Environment.NewLine +
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
