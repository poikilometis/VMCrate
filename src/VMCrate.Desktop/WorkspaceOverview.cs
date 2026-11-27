using System.Windows;
using VMCrate.Core;

namespace VMCrate.Desktop;

public partial class WorkspaceWindow
{
    private void RenderOverview()
    {
        var next = NextWork();
        ResumeTitle.Text = next.Title;
        ResumeDetail.Text = next.Detail;
        ResumeAction.Content = labels[next.Label];
        ExplainFailure.Visibility = FailureFacts().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentOperations.ItemsSource = snapshot?.RecentOperations.Take(6).Select(entry =>
            $"{entry.TimeUtc.ToLocalTime():g} · {entry.Operation}" +
            (entry.Code is null ? "" : $" · {labels[entry.Code]}")).ToArray() ?? [];
    }

    private AssistantFact[] FailureFacts()
    {
        if (lastFailure is { } failure)
            return [new("result", $"Operation error code={failure.Code}"),
                new("follow-up", $"VMCrate action={FailureRouting.From(failure.Code)}")];
        if (snapshot?.Builds.LastOrDefault(item => item.Stage != BuildStage.Complete) is { } build &&
            build.History.LastOrDefault()?.ErrorKey is not null)
            return DiagnosticFacts.BuildFailure(build);
        if ((catalogIssue ?? snapshot?.HostError) is { } issue)
            return [new("startup", $"Recorded availability fault={issue.Key}")];
        return [];
    }

    private void OnExplainFailure(object sender, RoutedEventArgs e)
    {
        var evidence = FailureFacts();
        if (evidence.Length > 0)
            AssistantWindow.Explain(this, labels, "Explain the observed VMCrate failure. Reference only the supplied facts and propose one check.", evidence);
    }

    private (string Title, string Detail, string Label, Func<Task> Run) NextWork()
    {
        if (catalogIssue is not null)
            return (labels["tools.recovery"], labels[catalogIssue.Key], "tools.recovery",
                () => ShowWorkflowAsync(new RecoveryWindow(labels, lab)));
        if (lastFailure is { } failure)
        {
            var route = FailureRouting.From(failure.Code);
            var label = RecoveryActions.Label(route);
            return (labels[label], labels[failure.Code], label, () => ShowWorkflowAsync(
                RecoveryActions.Create(route, labels, lab, snapshot, failure.Id)));
        }
        if (snapshot?.Host is not { } host)
            return (labels["host.unavailable"], labels["home.hostHint"], "tools.firstRun", ShowFirstMachineAsync);

        var unfinished = snapshot.Builds.LastOrDefault(job => job.Stage != BuildStage.Complete);
        if (unfinished is not null)
        {
            var detail = labels["build." + unfinished.Stage];
            if (unfinished.History.LastOrDefault()?.ErrorKey is { } error)
                return (unfinished.Options.Name, detail + Environment.NewLine + labels[error], "build.checkHistory",
                    () => ShowWorkflowAsync(new BuildDiagnosticsWindow(labels, lab, unfinished.Id)));
            return (unfinished.Options.Name, detail, "action.continue",
                () => ShowWorkflowAsync(new TemplateBuildWindow(labels, lab, unfinished.Id)));
        }

        var latest = snapshot.RecentOperations.Select(entry => snapshot.Machines.FirstOrDefault(vm => vm.Id == entry.Resource))
            .OfType<Machine>().FirstOrDefault() ?? snapshot.Machines.LastOrDefault();
        if (latest is not null)
        {
            var observed = host.Machines.FirstOrDefault(vm => vm.Id == latest.Id);
            var detail = MachineLabel(latest);
            if (observed is { State: PowerState.Running, Findings.Length: 0 })
                return (latest.Name, detail, "action.console", async () =>
                {
                    if (!host.EnhancedSession || labels.Confirm(this, "confirm.console"))
                        await RunAsync(() => lab.OpenConsoleAsync(latest.Id));
                });
            if (observed is { State: PowerState.Off, Findings.Length: 0 } && !latest.SessionStarted)
                return (latest.Name, detail, "action.start", () => RunAsync(() => lab.StartAsync(latest.Id), resource: latest.Id));
            return (latest.Name, detail, "tools.isolation", () => ShowIsolationAsync(latest.Id));
        }

        return (labels["first.title"], labels["first.needTemplate"], "first.title", ShowFirstMachineAsync);
    }

    private async void OnResumeWork(object sender, RoutedEventArgs e) => await NextWork().Run();
    private async void OnLocalJobs(object sender, RoutedEventArgs e) => await ShowWorkflowAsync(new LocalJobsWindow(labels, lab));
    private async void OnResourcePlan(object sender, RoutedEventArgs e) => await ShowWorkflowAsync(new ResourcePlanWindow(labels, lab));
    private async void OnVerifyInBackground(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template)
            return;
        await RunAsync(() =>
        {
            LocalJobProcess.Enqueue(lab, new(LocalJobKind.Verify, template.Id));
            return Task.CompletedTask;
        });
        await ShowWorkflowAsync(new LocalJobsWindow(labels, lab));
    }
}
