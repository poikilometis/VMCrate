using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class TemplateBuildWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly ComboBox jobs;
    private readonly TextBlock detail;
    private readonly TextBlock transfer;
    private readonly ProgressBar progress;
    private readonly ListBox history;
    private readonly Button advance, start, console, powerOff, eject, updates, trace, forget;
    private LabSnapshot? snapshot;

    internal TemplateBuildWindow(Labels text, LabService lab) : base(text, "tools.builds")
    {
        this.lab = lab;
        jobs = Field("tools.builds", new ComboBox { DisplayMemberPath = "Name" });
        detail = Note("");
        var setup = Actions();
        Action(setup, "action.newBuild", NewAsync);
        Action(setup, "tools.images", CatalogAsync);
        Action(setup, "queue.title", async () =>
        {
            new BuildQueueWindow(Text, lab) { Owner = this }.ShowDialog();
            await RefreshAsync();
        });
        advance = Action(setup, "action.continue", AdvanceAsync, true);
        Action(setup, "action.refresh", RefreshAsync);
        forget = Action(setup, "action.forgetBuild", ForgetAsync);
        Action(setup, "ai.explain", () =>
        {
            if (Selected is { } job)
                AssistantWindow.Explain(this, Text, "Explain the recorded stages and latest template build result", DiagnosticFacts.Build(job));
            return Task.CompletedTask;
        });
        var vm = Actions();
        start = Action(vm, "action.start", () => UseBuilderAsync(lab.StartAsync));
        console = Action(vm, "action.console", ConsoleAsync);
        powerOff = Action(vm, "action.powerOff", PowerOffAsync);
        eject = Action(vm, "action.ejectInstaller", () => UseBuilderAsync(lab.EjectBuildMediaAsync));
        updates = Action(vm, "tools.updates", ShowUpdatesAsync);
        trace = Action(vm, "tools.proxyTrace", ShowTraceAsync);
        transfer = Note("");
        progress = new ProgressBar { Height = 8, Visibility = Visibility.Collapsed };
        Body.Children.Add(progress);
        history = Field("label.history", new ListBox { MaxHeight = 170 });
        jobs.SelectionChanged += (_, _) => Render();
        Render();
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    private TemplateBuild? Selected => (jobs.SelectedItem as JobItem)?.Job;
    private Machine? Builder => snapshot?.Machines.SingleOrDefault(vm => vm.Id == Selected?.Id);

    private async Task RefreshAsync()
    {
        var id = Selected?.Id;
        var data = await Task.Run(async () => (Jobs: lab.ReadBuilds(), Snapshot: await lab.ObserveAsync()));
        snapshot = data.Snapshot;
        var items = data.Jobs.Select(job => new JobItem(job, job.Options.Name)).ToArray();
        jobs.ItemsSource = items;
        jobs.SelectedItem = items.FirstOrDefault(item => item.Job.Id == id) ?? items.LastOrDefault();
        Render();
    }

    private void Render()
    {
        var stage = Selected?.Stage;
        var vm = Builder;
        var state = vm is null ? PowerState.Missing : snapshot!.StateOf(vm.Id);
        var install = stage is BuildStage.Install or BuildStage.Capture;
        detail.Text = stage is null ? "" : Text["build." + stage];
        history.ItemsSource = Selected?.History.Reverse().Select(item =>
            $"{item.TimeUtc.ToLocalTime():g} · {Text["build." + item.Stage]}" +
            (item.ErrorKey is null ? "" : " · " + Text[item.ErrorKey])).ToArray();
        advance.IsEnabled = stage switch
        {
            BuildStage.Verify => true,
            BuildStage.Provision or BuildStage.BootTest => snapshot?.Host is not null,
            BuildStage.Install => state == PowerState.Running,
            BuildStage.Capture => state == PowerState.Off,
            _ => false
        };
        forget.IsEnabled = stage is not null && (stage == BuildStage.Complete || vm is null);
        start.IsEnabled = stage == BuildStage.Install && state == PowerState.Off;
        console.IsEnabled = install && state == PowerState.Running;
        powerOff.IsEnabled = install && state is PowerState.Running or PowerState.Saved;
        eject.IsEnabled = stage == BuildStage.Install && state is PowerState.Off or PowerState.Running;
        updates.IsEnabled = install && state == PowerState.Off;
        trace.IsEnabled = vm?.Network == NetworkMode.Updates && state is PowerState.Off or PowerState.Running;
    }

    private async Task CatalogAsync()
    {
        var catalog = new ImageCatalogWindow(Text, lab) { Owner = this };
        if (catalog.ShowDialog() == true && catalog.Selection is { } choice &&
            new NewTemplateBuildWindow(Text, lab, choice) { Owner = this }.ShowDialog() == true)
            await RefreshAsync();
    }

    private async Task NewAsync()
    {
        if (new NewTemplateBuildWindow(Text, lab) { Owner = this }.ShowDialog() == true)
            await RefreshAsync();
    }

    private async Task ForgetAsync()
    {
        if (Selected is { } job && Text.Confirm(this, "confirm.forgetBuild", job.Options.Name))
        {
            await Task.Run(() => lab.ForgetBuild(job.Id));
            await RefreshAsync();
        }
    }

    private async Task AdvanceAsync()
    {
        if (Selected is not { } job)
            return;

        transfer.Text = "";
        try
        {
            if (job.Stage is BuildStage.Verify or BuildStage.Capture or BuildStage.BootTest)
                await WithCancellationAsync(token => AdvanceFileStageAsync(job.Id, token));
            else
                await Task.Run(() => lab.ContinueBuildAsync(job.Id));
        }
        finally { await RefreshAsync(); }
    }

    private async Task AdvanceFileStageAsync(Guid id, CancellationToken cancellation)
    {
        progress.IsIndeterminate = true;
        progress.Visibility = Visibility.Visible;
        var updates = new Progress<FileProgress>(update =>
        {
            if (progress.Visibility != Visibility.Visible)
                return;
            progress.IsIndeterminate = update.TotalBytes <= 0;
            if (update.TotalBytes > 0)
                progress.Value = Math.Min(100d, 100d * update.ProcessedBytes / update.TotalBytes);
            transfer.Text = Text.Format("build.bytes", MiB(update.ProcessedBytes), MiB(update.TotalBytes));
        });
        try { await Task.Run(() => lab.ContinueBuildAsync(id, updates, cancellation)); }
        finally
        {
            progress.Visibility = Visibility.Collapsed;
            transfer.Text = "";
        }
    }

    private async Task ConsoleAsync()
    {
        if (snapshot?.Host?.EnhancedSession != true || Text.Confirm(this, "confirm.console"))
            await UseBuilderAsync(lab.OpenConsoleAsync);
    }

    private Task PowerOffAsync() => Builder is { } vm && Text.Confirm(this, "confirm.powerOff", vm.Name)
        ? UseBuilderAsync(lab.PowerOffAsync) : Task.CompletedTask;

    private Task ShowTraceAsync()
    {
        if (Builder is { Network: NetworkMode.Updates } vm)
            new ProxyTraceWindow(Text, lab, vm) { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }

    private async Task ShowUpdatesAsync()
    {
        if (Builder is { } vm)
        {
            new UpdateProxyWindow(Text, lab, vm) { Owner = this }.ShowDialog();
            await RefreshAsync();
        }
    }

    private async Task UseBuilderAsync(Func<Guid, Task> operation)
    {
        if (Builder is { } vm)
        {
            await Task.Run(() => operation(vm.Id));
            await RefreshAsync();
        }
    }

    private sealed record JobItem(TemplateBuild Job, string Name);
}
