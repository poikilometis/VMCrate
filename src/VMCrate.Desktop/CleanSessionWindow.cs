using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class CleanSessionWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly ComboBox templates, sessions;
    private readonly TextBox name, memory, cpu;
    private readonly TextBlock filesLabel, details;
    private readonly Button prepare, start, console, report, end, shutdown, disconnect;
    private readonly CheckBox keep;
    private readonly Dictionary<Guid, string> exported = [];
    private IReadOnlyList<string> files = [];
    private LabSnapshot? snapshot;

    internal CleanSessionWindow(Labels text, LabService lab, string workspace,
        SessionProfile? initialProfile = null, IReadOnlyList<string>? initialFiles = null) : base(text, "tools.session", 710, 660)
    {
        this.lab = lab;
        Note(Text["hint.session"]);
        templates = Field("label.template", new ComboBox { DisplayMemberPath = "Name" });
        name = Field("label.name", new TextBox { MaxLength = 64 });
        memory = Field("label.memory", new TextBox { Text = "4" });
        cpu = Field("label.cpu", new TextBox { Text = "2" });
        keep = new CheckBox { Content = Text["session.keep"], Margin = new Thickness(0, 6, 0, 10) };
        Body.Children.Add(keep);
        Action(Actions(), "profiles.title", () => OpenProfilesAsync(workspace));
        filesLabel = Note("");
        SetFiles(initialFiles ?? []);
        var creation = Actions();
        Action(creation, "report.addFiles", SelectFilesAsync);
        Action(creation, "report.clearFiles", () => { SetFiles([]); return Task.CompletedTask; });
        prepare = Action(creation, "action.disposable", PrepareAsync, true);
        keep.Checked += (_, _) => prepare.Content = Text["action.clone"];
        keep.Unchecked += (_, _) => prepare.Content = Text["action.disposable"];

        sessions = Field("tools.session", new ComboBox { DisplayMemberPath = "Name" });
        details = Note("");
        var lifecycle = Actions();
        start = Action(lifecycle, "action.start", () => OperateAsync(lab.StartAsync));
        console = Action(lifecycle, "action.console", ConsoleAsync);
        disconnect = Action(lifecycle, "session.closeConsole", () => OperateAsync(lab.CloseConsoleAsync));
        shutdown = Action(lifecycle, "session.shutdown", ShutdownAsync);
        report = Action(lifecycle, "action.vmReport", ReportAsync);
        Action(lifecycle, "outbox.title", () =>
        {
            new OutboxWindow(Text, lab, (sessions.SelectedItem as Machine)?.Id) { Owner = this }.ShowDialog();
            return RefreshAsync();
        });
        Action(lifecycle, "tools.sessionReport", () =>
        {
            new SessionReportWindow(Text, lab, (sessions.SelectedItem as Machine)?.Id) { Owner = this }.ShowDialog();
            return Task.CompletedTask;
        });
        end = Action(lifecycle, "action.endSession", EndAsync);
        Action(lifecycle, "action.refresh", () => RefreshAsync());
        sessions.SelectionChanged += (_, _) => Render();
        Render();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += async (_, _) =>
        {
            if (snapshot?.Machines.Any(vm => vm.Disposable && vm.SessionStarted) == true)
                await RunAsync(() => RefreshAsync(), quiet: true);
        };
        Loaded += async (_, _) =>
        {
            await RunAsync(async () =>
            {
                await RefreshAsync();
                if (initialProfile is not null)
                    ApplyProfile(initialProfile);
            });
            timer.Start();
        };
        Closed += (_, _) => timer.Stop();
    }

    private async Task RefreshAsync(Guid? select = null)
    {
        var previousTemplate = (templates.SelectedItem as Template)?.Id;
        var previousSession = select ?? (sessions.SelectedItem as Machine)?.Id;
        snapshot = await Task.Run(lab.RefreshAsync);
        templates.ItemsSource = snapshot.Templates;
        templates.SelectedItem = snapshot.Templates.FirstOrDefault(item => item.Id == previousTemplate)
            ?? snapshot.Templates.FirstOrDefault();
        var active = snapshot.Machines.Where(vm => vm.Disposable || vm.SessionFiles is not null && vm.TemplateId is not null).ToArray();
        sessions.ItemsSource = active;
        sessions.SelectedItem = active.FirstOrDefault(vm => vm.Id == previousSession) ?? active.FirstOrDefault();
        Render();
    }

    private void Render()
    {
        var vm = sessions.SelectedItem as Machine;
        var state = vm is null ? PowerState.Missing : snapshot!.StateOf(vm.Id);
        details.Text = vm is null ? "" : SessionDetail(vm, state);
        prepare.IsEnabled = snapshot?.Host is not null && templates.Items.Count > 0;
        start.IsEnabled = state == PowerState.Off && vm?.SessionStarted == false;
        console.IsEnabled = state == PowerState.Running;
        disconnect.IsEnabled = console.IsEnabled;
        shutdown.IsEnabled = vm is { Disposable: false } && state == PowerState.Running;
        end.IsEnabled = vm is { Disposable: true } && state is PowerState.Running or PowerState.Off or PowerState.Saved;
        report.IsEnabled = vm is not null;
    }

    private string SessionDetail(Machine vm, PowerState state)
    {
        var template = snapshot?.Templates.FirstOrDefault(item => item.Id == vm.TemplateId);
        var revision = template is null ? "" : $" · {template.Name} {template.Revision}".TrimEnd();
        var reportPath = exported.GetValueOrDefault(vm.Id);
        return Text["power." + state] + revision + Environment.NewLine +
               Text[vm.Disposable ? "session.discard" : "session.persist"] + Environment.NewLine +
               (reportPath is null ? Text["session.noReport"] : Text.Format("session.reportAt", reportPath));
    }

    private Task SelectFilesAsync()
    {
        if (SelectFiles("report.addFiles") is { } selected)
            SetFiles(selected);
        return Task.CompletedTask;
    }

    private void SetFiles(IReadOnlyList<string> selected)
    {
        files = selected;
        filesLabel.Text = Text.Format("report.files", files.Count);
        if (files.Count > 0)
            filesLabel.Text += Environment.NewLine + string.Join(", ", files.Take(8).Select(Path.GetFileName)) +
                (files.Count > 8 ? " …" : "");
    }

    private Task OpenProfilesAsync(string workspace)
    {
        var options = new MachineOptions(name.Text, Integer(memory.Text), Integer(cpu.Text));
        var dialog = new SessionProfilesWindow(Text, lab, workspace, (templates.SelectedItem as Template)?.Id, options) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Selected is { } profile)
            ApplyProfile(profile);
        return Task.CompletedTask;
    }

    private void ApplyProfile(SessionProfile profile)
    {
        var template = snapshot?.Templates.SingleOrDefault(item => item.Id == profile.TemplateId)
            ?? throw new LabException("error.template");
        templates.SelectedItem = template;
        name.Text = snapshot!.Machines.Any(vm => vm.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
            ? $"{profile.Name[..Math.Min(54, profile.Name.Length)]} {Guid.NewGuid().ToString("N")[..8]}"
            : profile.Name;
        memory.Text = profile.MemoryGiB.ToString();
        cpu.Text = profile.CpuCount.ToString();
    }

    private async Task PrepareAsync()
    {
        if (templates.SelectedItem is Template template)
        {
            var options = new MachineOptions(name.Text, Integer(memory.Text), Integer(cpu.Text));
            var selected = files;
            var disposable = keep.IsChecked != true;
            var id = await Task.Run(() => lab.PrepareCleanSessionAsync(template.Id, options, selected, disposable));
            await RefreshAsync(id);
            SetFiles([]);
        }
    }

    private Task ConsoleAsync() => snapshot?.Host?.EnhancedSession != true || Text.Confirm(this, "confirm.console")
        ? OperateAsync(lab.OpenConsoleAsync) : Task.CompletedTask;

    private Task EndAsync() => sessions.SelectedItem is Machine { Disposable: true } vm &&
        Text.Confirm(this, "session.confirmDelete", vm.Name, exported.GetValueOrDefault(vm.Id) ?? Text["session.noReport"])
            ? OperateAsync(lab.EndDisposableAsync) : Task.CompletedTask;

    private Task ShutdownAsync() => sessions.SelectedItem is Machine { Disposable: false } vm &&
        Text.Confirm(this, "session.confirmShutdown", vm.Name)
            ? OperateAsync(lab.ShutDownAsync) : Task.CompletedTask;

    private async Task ReportAsync()
    {
        if (sessions.SelectedItem is not Machine vm)
            return;
        var path = SaveFile("tools.report", $"vmcrate-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        if (path is not null)
        {
            await Task.Run(() => lab.ExportReportAsync(path, vm.Id, []));
            exported[vm.Id] = path;
            Render();
        }
    }

    private async Task OperateAsync(Func<Guid, Task> action)
    {
        if (sessions.SelectedItem is Machine vm)
        {
            await Task.Run(() => action(vm.Id));
            await RefreshAsync(vm.Id);
        }
    }
}
