using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class RevisionRolloutWindow : WorkflowWindow
{
    internal RevisionRolloutWindow(Labels text, LabService lab, Template target)
        : base(text, "revision.rollout", 715, 500)
    {
        Note(target.Name);
        Note(Text["revision.rolloutHint"]);
        var list = Field("tab.machines", new ListBox
        {
            SelectionMode = SelectionMode.Extended, DisplayMemberPath = nameof(Row.Caption), MinHeight = 145
        });
        var apply = Action(Actions(), "action.create", async () =>
        {
            var selected = list.SelectedItems.OfType<Row>().Select(item => item.Candidate.SourceId).ToArray();
            await WithCancellationAsync(token => Task.Run(() => lab.ApplyRevisionRolloutAsync(target.Id, selected, token)));
        }, primary: true);
        apply.IsEnabled = false;
        list.SelectionChanged += (_, _) => apply.IsEnabled = target.BootVerifiedUtc is not null && list.SelectedItems.Count > 0;
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            var candidates = await Task.Run(() => lab.ReadRevisionCandidates(target.Id));
            list.ItemsSource = candidates.Select(item => new Row(item,
                $"{item.SourceName} ({item.OriginalTemplate}) → {item.TrialName}")).ToArray();
        });
    }

    private sealed record Row(RevisionCandidate Candidate, string Caption);
}
