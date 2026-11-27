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
    private readonly Button prepare, start, console, report, end;
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
        Action(Actions(), "profiles.title", () => OpenProfilesAsync(workspace));
        filesLabel = Note("");
        SetFiles(initialFiles ?? []);
        var creation = Actions();
        Action(creation, "report.addFiles", SelectFilesAsync);
        Action(creation, "report.clearFiles", () => { SetFiles([]); return Task.CompletedTask; });
        prepare = Action(creation, "action.disposable", PrepareAsync, true);

        sessions = Field("tools.session", new ComboBox { DisplayMemberPath = "Name" });
        details = Note("");
        var lifecycle = Actions();
        start = Action(lifecycle, "action.start", () => OperateAsync(lab.StartAsync));
        console = Action(lifecycle, "action.console", ConsoleAsync);
        report = Action(lifecycle, "action.vmReport", ReportAsync);
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
        var active = snapshot.Machines.Where(vm => vm.Disposable).ToArray();
        sessions.ItemsSource = active;
        sessions.SelectedItem = active.FirstOrDefault(vm => vm.Id == previousSession) ?? active.FirstOrDefault();
        Render();
    }

    private void Render()
    {
        var vm = sessions.SelectedItem as Machine;
        var state = vm is null ? PowerState.Missing : snapshot!.StateOf(vm.Id);
        details.Text = vm is null ? "" : Text["power." + state];
        prepare.IsEnabled = snapshot?.Host is not null && templates.Items.Count > 0;
        start.IsEnabled = state == PowerState.Off && vm?.SessionStarted == false;
        console.IsEnabled = state == PowerState.Running;
        end.IsEnabled = state is PowerState.Running or PowerState.Off or PowerState.Saved;
        report.IsEnabled = vm is not null;
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
            var id = await Task.Run(() => lab.PrepareCleanSessionAsync(template.Id, options, selected));
            await RefreshAsync(id);
            SetFiles([]);
        }
    }

    private Task ConsoleAsync() => snapshot?.Host?.EnhancedSession != true || Text.Confirm(this, "confirm.console")
        ? OperateAsync(lab.OpenConsoleAsync) : Task.CompletedTask;

    private Task EndAsync() => sessions.SelectedItem is Machine vm && Text.Confirm(this, "confirm.endSession", vm.Name)
        ? OperateAsync(lab.EndDisposableAsync) : Task.CompletedTask;

    private async Task ReportAsync()
    {
        if (sessions.SelectedItem is not Machine vm)
            return;
        var path = SaveFile("tools.report", $"vmcrate-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        if (path is not null)
            await Task.Run(() => lab.ExportReportAsync(path, vm.Id, []));
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
