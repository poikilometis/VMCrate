using System.Windows.Controls;
using System.Windows.Threading;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class LocalJobsWindow : WorkflowWindow
{
    private readonly ListBox rows;
    private readonly TextBlock details;
    private readonly Button cancel, retry, resolve, explain;
    private readonly LocalJobQueue queue;
    private LocalJob? Selected => (rows.SelectedItem as JobRow)?.Job;

    internal LocalJobsWindow(Labels text, LabService lab) : base(text, "jobs.title", 720, 600)
    {
        queue = new LocalJobQueue(lab.WorkspaceDirectory);
        Note(Text["jobs.hint"]);
        rows = Field("jobs.title", new ListBox { DisplayMemberPath = nameof(JobRow.Summary), MinHeight = 120, MaxHeight = 220 });
        details = Note("");
        var actions = Actions();
        cancel = Action(actions, "action.cancel", async () =>
        {
            if (Selected is { } job)
                await Task.Run(() => queue.Cancel(job.Id));
            await RefreshAsync();
        });
        retry = Action(actions, "action.continue", async () =>
        {
            if (Selected is { } job)
                await Task.Run(() => { queue.Retry(job.Id); LocalJobProcess.Wake(lab.WorkspaceDirectory); });
            await RefreshAsync();
        }, primary: true);
        resolve = Action(actions, "tools.help", async () =>
        {
            if (Selected is not { Failure: { } code } job)
                return;
            var route = FailureRouting.From(code);
            var catalog = route is RecoveryRoute.Proxy or RecoveryRoute.Backup or RecoveryRoute.Revisions
                ? await Task.Run(lab.ObserveAsync) : null;
            var window = RecoveryActions.Create(route, Text, lab, catalog, job.Request.Resource);
            window.Owner = this;
            window.ShowDialog();
        });
        explain = Action(actions, "ai.explain", () =>
        {
            if (Selected is { Failure: not null } job)
                AssistantWindow.Explain(this, Text, "Explain the recorded failure and propose a check in VMCrate using the evidence.", [
                    new("job", $"Kind {job.Request.Kind}, status {job.Status}, attempt {job.Attempts}, stage {job.Request.Stage}"),
                    new("failure", $"Recorded failure {job.Failure}, bytes processed {job.ProcessedBytes}, total bytes {job.TotalBytes}")]);
            return Task.CompletedTask;
        });
        rows.SelectionChanged += (_, _) => Render();
        Render();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await RunAsync(RefreshAsync, quiet: true);
        Loaded += async (_, _) => { await RunAsync(RefreshAsync); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }

    private async Task RefreshAsync()
    {
        var selected = Selected?.Id;
        var current = (await Task.Run(queue.Read)).OrderByDescending(job => job.UpdatedUtc).Select(job => new JobRow(job,
            $"{Text[KindLabel(job.Request.Kind)]} · {Text[job.Status == LocalJobStatus.Cancelled ? "status.cancelled" : "remote." + job.Status]} · " +
            $"{job.UpdatedUtc.ToLocalTime():g}")).ToArray();
        rows.ItemsSource = current;
        rows.SelectedItem = current.FirstOrDefault(row => row.Job.Id == selected) ?? current.FirstOrDefault();
        Render();
    }

    private void Render()
    {
        var job = Selected;
        cancel.IsEnabled = job is { Status: LocalJobStatus.Queued or LocalJobStatus.Running, CancelRequested: false };
        retry.IsEnabled = job?.Status is LocalJobStatus.Failed or LocalJobStatus.Cancelled;
        var route = FailureRouting.From(job?.Failure);
        resolve.IsEnabled = job?.Failure is not null && route != RecoveryRoute.Jobs;
        resolve.Content = Text[route == RecoveryRoute.Jobs ? "tools.help" : RecoveryActions.Label(route)];
        explain.IsEnabled = job?.Failure is not null;
        details.Text = job is null ? "" : (job.Request.Blueprint ?? job.Request.Publisher ?? job.Request.Stage?.ToString() ?? "");
        if (job?.ProcessedBytes is long bytes)
        {
            details.Text += Environment.NewLine + Text.Format("images.received", MiB(bytes), job.TotalBytes is long total ? MiB(total) : "—");
            var elapsed = Elapsed(job);
            if (elapsed > 1)
                details.Text += $" · {MiB((long)(bytes / elapsed))} MiB/s";
        }
        if (job?.StartedUtc is not null)
            details.Text += $" · {Elapsed(job):N1} s";
        if (job?.Failure is { } code)
            details.Text += Environment.NewLine + Text[code];
    }

    private static double Elapsed(LocalJob job) => job.ElapsedMs / 1000d ??
        (job.StartedUtc is { } start ? Math.Max(0, (DateTimeOffset.UtcNow - start).TotalSeconds) : 0);

    private static string KindLabel(LocalJobKind kind) => kind switch
    {
        LocalJobKind.Build => "tools.builds",
        LocalJobKind.Verify => "action.verifyTemplate",
        LocalJobKind.Backup => "backup.save",
        LocalJobKind.RestoreDrill => "backup.drill",
        _ => "images.download"
    };

    private sealed record JobRow(LocalJob Job, string Summary);
}
