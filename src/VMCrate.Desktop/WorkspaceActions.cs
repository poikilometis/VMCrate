using System.Windows;
using Microsoft.Win32;
using VMCrate.Core;

namespace VMCrate.Desktop;

public partial class WorkspaceWindow
{
    private async void OnNewInstaller(object sender, RoutedEventArgs e)
    {
        var form = ShowSetup(SetupKind.Installer);
        if (form is not null)
        {
            var options = new InstallerOptions(form.Options, form.Guest, form.SecureBoot, form.MediaPath, form.DiskGiB);
            await RunAsync(() => lab.CreateInstallerAsync(options));
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var form = ShowSetup(SetupKind.Import);
        if (form is not null)
        {
            var (name, guest, secureBoot, path) = (form.Options.Name, form.Guest, form.SecureBoot, form.MediaPath);
            await RunAsync(() => lab.ImportTemplateAsync(name, guest, secureBoot, path));
        }
    }

    private async void OnClone(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is { } template && ShowSetup(SetupKind.Clone) is { } form)
            await RunAsync(() => lab.CreateCloneAsync(template.Id, form.Options));
    }

    private async void OnCapture(object sender, RoutedEventArgs e)
    {
        if (SelectedMachine is { } machine && ShowSetup(SetupKind.Capture) is { } form)
            await RunAsync(() => lab.CaptureTemplateAsync(machine.Id, form.Options.Name));
    }

    private async void OnStart(object sender, RoutedEventArgs e) => await RunForMachineAsync(lab.StartAsync);

    private async void OnConsole(object sender, RoutedEventArgs e)
    {
        if (snapshot?.Host?.EnhancedSession == true && !labels.Confirm(this, "confirm.console"))
            return;
        await RunForMachineAsync(lab.OpenConsoleAsync);
    }

    private async void OnPowerOff(object sender, RoutedEventArgs e)
    {
        if (SelectedMachine?.Disposable == true)
            await RunConfirmedAsync("confirm.endSession", lab.EndDisposableAsync);
        else
            await RunConfirmedAsync("confirm.powerOff", lab.PowerOffAsync);
    }

    private async void OnReset(object sender, RoutedEventArgs e) =>
        await RunConfirmedAsync("confirm.reset", lab.ResetDiskAsync, "status.resetDone");

    private async void OnRemoveMachine(object sender, RoutedEventArgs e) =>
        await RunConfirmedAsync("confirm.removeVm", lab.DeleteMachineAsync);

    private async void OnRemoveTemplate(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is { } template && labels.Confirm(this, "confirm.removeTemplate", template.Name))
            await RunAsync(() => lab.DeleteTemplateAsync(template.Id));
    }

    private async void OnSetNetwork(object sender, RoutedEventArgs e)
    {
        if (NetworkPicker.SelectedValue is NetworkMode mode)
            await RunForMachineAsync(id => lab.SetNetworkAsync(id, mode));
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = labels["action.send"], Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(this) == true)
            await RunForMachineAsync(id => lab.SendFilesAsync(id, picker.FileNames));
    }

    private async void OnEjectInbox(object sender, RoutedEventArgs e) => await RunForMachineAsync(lab.EjectInboxAsync);
    private async void OnEjectInstaller(object sender, RoutedEventArgs e) => await RunForMachineAsync(lab.EjectInstallerAsync);

    private SetupDialog? ShowSetup(SetupKind kind)
    {
        var dialog = new SetupDialog(labels, kind) { Owner = this };
        return dialog.ShowDialog() == true ? dialog : null;
    }

    private Task RunForMachineAsync(Func<Guid, Task> action) =>
        SelectedMachine is { } machine ? RunAsync(() => action(machine.Id)) : Task.CompletedTask;

    private Task RunConfirmedAsync(string key, Func<Guid, Task> action, string successStatus = "status.done") =>
        SelectedMachine is { } machine && labels.Confirm(this, key, machine.Name)
            ? RunAsync(() => action(machine.Id), successStatus: successStatus)
            : Task.CompletedTask;
}
