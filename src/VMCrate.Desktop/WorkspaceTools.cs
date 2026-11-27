using System.Windows;
using Microsoft.Win32;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

public partial class WorkspaceWindow
{
    private async void OnRecovery(object sender, RoutedEventArgs e) => await ShowWorkflowAsync(new RecoveryWindow(labels, lab));

    private async void OnFirstRun(object sender, RoutedEventArgs e) => await ShowWorkflowAsync(
        new FirstRunWindow(labels, preferences.Root, new PowerShellHypervisor().ReadHostReadinessAsync, null));

    private async void OnTroubleshooting(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new TroubleshootingWindow(labels, lab));

    private async void OnSupport(object sender, RoutedEventArgs e) =>
        await ShowWorkflowAsync(new SupportWindow(labels, lab, snapshot?.Machines ?? []));

    private async void OnConsoleTools(object sender, RoutedEventArgs e)
    {
        if (SelectedMachine is { } machine)
            await ShowWorkflowAsync(new ConsoleToolsWindow(labels, lab, machine));
    }

    private async void OnDiskRecovery(object sender, RoutedEventArgs e)
    {
        if (SelectedMachine is { TemplateId: not null, Disposable: false } vm)
            await ShowWorkflowAsync(new DiskRecoveryWindow(labels, lab, new RecoveryPointScope(vm.Id)));
    }

    private async void OnIsolation(object sender, RoutedEventArgs e) => await ShowIsolationAsync(null);

    private async void OnMachineIsolation(object sender, RoutedEventArgs e) => await ShowIsolationAsync(SelectedMachine?.Id);

    private Task ShowIsolationAsync(Guid? selected) =>
        ShowWorkflowAsync(new IsolationWindow(labels, lab, snapshot?.Machines ?? [], selected));

    private async void OnDiagnostics(object sender, RoutedEventArgs e) => await ShowWorkflowAsync(new HostDiagnosticsWindow(labels, lab));
    private async void OnBuilds(object sender, RoutedEventArgs e) => await ShowWorkflowAsync(new TemplateBuildWindow(labels, lab));
    private async void OnLabs(object sender, RoutedEventArgs e) => await ShowWorkflowAsync(new LabGroupWindow(labels, lab));
    private async void OnStorage(object sender, RoutedEventArgs e) => await ShowWorkflowAsync(new StorageWindow(labels, lab));
    private async void OnCleanSession(object sender, RoutedEventArgs e) => await ShowWorkflowAsync(new CleanSessionWindow(labels, lab, preferences.Root));

    private async void OnProxyTrace(object sender, RoutedEventArgs e)
    {
        if (SelectedMachine is { Network: NetworkMode.Updates } machine)
            await ShowWorkflowAsync(new ProxyTraceWindow(labels, lab, machine));
    }

    private async void OnUpdateProxy(object sender, RoutedEventArgs e)
    {
        if (SelectedMachine is { } machine)
            await ShowWorkflowAsync(new UpdateProxyWindow(labels, lab, machine));
    }

    private async Task ShowWorkflowAsync(Window window)
    {
        workflowOpen = true;
        try
        {
            window.Owner = this;
            window.ShowDialog();
        }
        finally
        {
            workflowOpen = false;
        }
        await RunAsync();
    }

    private async void OnBlueprint(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = labels["action.previewBlueprint"],
            Filter = "JSON (*.json)|*.json",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true)
            return;

        LabBlueprint? blueprint = null;
        BlueprintPreview? preview = null;
        await RunAsync(async () =>
        {
            blueprint = LabBlueprint.Read(picker.FileName);
            preview = await lab.PreviewBlueprintAsync(blueprint);
        });
        if (blueprint is not null && preview is not null &&
            new BlueprintDialog(labels, preview) { Owner = this }.ShowDialog() == true)
        {
            var plan = blueprint;
            await RunAsync(() => lab.DeployBlueprintAsync(plan));
        }
    }

    private async void OnSandbox(object sender, RoutedEventArgs e) =>
        await RunAsync(() =>
        {
            new OfflineSandbox(preferences.Root).Launch();
            return Task.CompletedTask;
        });

    private async void OnExportSandbox(object sender, RoutedEventArgs e)
    {
        var picker = SavePicker("action.exportSandbox", "VMCrate-Offline.wsb", "WSB");
        if (picker.ShowDialog(this) == true)
            await RunAsync(() =>
            {
                OfflineSandbox.Export(picker.FileName);
                return Task.CompletedTask;
            });
    }

    private async void OnDisposable(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is { } template && ShowSetup(SetupKind.Disposable) is { } form)
            await RunAsync(() => lab.CreateCloneAsync(template.Id, form.Options, disposable: true));
    }

    private async void OnVerifyTemplate(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } template)
            return;

        TemplateIntegrity? result = null;
        await RunAsync(async () => result = await lab.VerifyTemplateAsync(template.Id));
        if (result is null)
            return;
        var key = result.Matches switch
        {
            true => "integrity.matches",
            false => "integrity.mismatch",
            null => "integrity.recorded"
        };
        MessageBox.Show(this, labels.Format(key, result.Sha256), labels["action.verifyTemplate"],
            MessageBoxButton.OK, result.Matches == false ? MessageBoxImage.Warning : MessageBoxImage.Information,
            MessageBoxResult.OK, labels.MessageOptions);
    }

    private async void OnReport(object sender, RoutedEventArgs e) => await ExportReportAsync(null);
    private async void OnMachineReport(object sender, RoutedEventArgs e) => await ExportReportAsync(SelectedMachine?.Id);

    private async Task ExportReportAsync(Guid? machine)
    {
        var form = new ReportDialog(labels, snapshot?.Machines ?? [], machine) { Owner = this };
        if (form.ShowDialog() != true)
            return;

        var picker = SavePicker("tools.report", $"vmcrate-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json", "JSON");
        if (picker.ShowDialog(this) == true)
        {
            var (machineId, files, destination) = (form.MachineId, form.Files, picker.FileName);
            await RunAsync(() => lab.ExportReportAsync(destination, machineId, files));
        }
    }

    private SaveFileDialog SavePicker(string title, string filename, string fileType)
    {
        var extension = fileType.ToLowerInvariant();
        return new SaveFileDialog
        {
            Title = labels[title],
            FileName = filename,
            Filter = $"{fileType} (*.{extension})|*.{extension}",
            DefaultExt = $".{extension}",
            AddExtension = true,
            OverwritePrompt = false
        };
    }
}
