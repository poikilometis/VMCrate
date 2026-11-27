using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

public partial class WorkspaceWindow
{
    private void Render()
    {
        var host = snapshot?.Host;
        HostSummary.Text = labels[host is null ? "host.unavailable" : host.EnhancedSession ? "host.enhanced" : "host.ready"];
        var hostIssue = catalogIssue ?? snapshot?.HostError ?? snapshot?.CleanupError;
        if (snapshot?.CleanupError is { } cleanup)
            HostSummary.Text += " " + labels[cleanup.Key];
        HostSummary.Foreground = (Brush)FindResource(host is null || host.EnhancedSession || hostIssue is not null ? "Notice" : "Accent");
        HostDetailsButton.Visibility = hostIssue is null ? Visibility.Collapsed : Visibility.Visible;
        RecoveryShortcut.Visibility = catalogIssue is not null || snapshot?.RecoverySuggested == true
            ? Visibility.Visible : Visibility.Collapsed;
        PreviewBlueprintButton.IsEnabled = host is not null;
        OpenSandboxButton.IsEnabled = OfflineSandbox.CanLaunch;
        CreateInstallerButton.IsEnabled = host is not null;
        ImportTemplateButton.IsEnabled = host is not null;

        var machines = snapshot?.Machines ?? [];
        var templates = snapshot?.Templates ?? [];
        SetRows(MachineList, machines.Select(vm => new LabRow(vm.Id, vm.Name, MachineLabel(vm), vm)));
        SetRows(TemplateList, templates.Select(template => new LabRow(template.Id, template.Name, GuestLabel(template.Guest), template)));
        EmptyMachines.Visibility = machines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTemplates.Visibility = templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderMachine();
        RenderTemplate();
        SetStatus(status);
        SetBusyControls();
    }

    private void RenderMachine()
    {
        var machine = SelectedMachine;
        MachineActions.Visibility = machine is null ? Visibility.Collapsed : Visibility.Visible;
        MachineTitle.Text = machine?.Name ?? labels["detail.machine"];
        MachineSummary.Text = machine is null ? "" : labels.Format("detail.resources",
            MachineLabel(machine), machine.MemoryGiB, machine.CpuCount);
        if (machine is null)
            return;

        var host = snapshot?.Host;
        var state = snapshot?.StateOf(machine.Id) ?? PowerState.Unavailable;
        var observation = host?.Machines.FirstOrDefault(vm => vm.Id == machine.Id);
        var findings = observation?.Findings ?? ["audit.unavailable"];
        var known = host is not null && state is not (PowerState.Missing or PowerState.Unavailable);
        var off = state == PowerState.Off;
        var interactive = known && state is (PowerState.Off or PowerState.Running);
        var builder = machine.TemplateId is null;

        if (machine.BlueprintId is not null)
            MachineSummary.Text += Environment.NewLine + labels.Format("blueprint.member", machine.BlueprintId);
        var updates = machine.Network == NetworkMode.Updates;
        NetworkPicker.ItemsSource = Enum.GetValues<NetworkMode>().Where(mode => updates || mode != NetworkMode.Updates)
            .Select(mode => new NetworkChoice(mode, NetworkLabel(mode))).ToArray();
        NetworkPicker.SelectedValue = machine.Network;
        NetworkPicker.IsEnabled = known && off && !updates && !machine.Disposable && machine.BlueprintId is null;
        ApplyNetworkButton.IsEnabled = known && off && !updates && !machine.Disposable;
        DiskRecoveryButton.Visibility = machine is { TemplateId: not null, Disposable: false }
            ? Visibility.Visible : Visibility.Collapsed;
        ProxyTraceButton.Visibility = updates ? Visibility.Visible : Visibility.Collapsed;
        ProxyTraceButton.IsEnabled = known;
        UpdateProxyButton.Visibility = builder ? Visibility.Visible : Visibility.Collapsed;
        UpdateProxyButton.IsEnabled = known && off && builder;
        AuditSummary.Text = findings.Length == 0
            ? labels["audit.passed"]
            : string.Join(Environment.NewLine, findings.Select(key => labels[key]));
        AuditSummary.Foreground = (Brush)FindResource(findings.Length == 0 ? "Accent" : "Notice");

        StartButton.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = known && !machine.SessionStarted && findings.Length == 0;
        ConsoleButton.Visibility = known && state == PowerState.Running ? Visibility.Visible : Visibility.Collapsed;
        PowerButton.Content = labels[machine.Disposable ? "action.endSession" : "action.powerOff"];
        PowerButton.IsEnabled = known && !off;
        SendButton.IsEnabled = interactive;
        EjectInboxButton.IsEnabled = interactive;
        EjectInstallerButton.Visibility = builder ? Visibility.Visible : Visibility.Collapsed;
        EjectInstallerButton.IsEnabled = interactive;
        CaptureButton.Visibility = builder ? Visibility.Visible : Visibility.Collapsed;
        CaptureButton.IsEnabled = known && off && findings.Length == 0;
        ResetButton.Visibility = builder || machine.Disposable ? Visibility.Collapsed : Visibility.Visible;
        ResetButton.IsEnabled = known && off && findings.Length == 0;
        RemoveMachineButton.IsEnabled = host is not null && (off || state == PowerState.Missing);
    }

    private void RenderTemplate()
    {
        var template = SelectedTemplate;
        TemplateActions.Visibility = template is null ? Visibility.Collapsed : Visibility.Visible;
        TemplateTitle.Text = template?.Name ?? labels["detail.template"];
        TemplateSummary.Text = template is null ? "" : GuestLabel(template.Guest) + " · " +
            labels[template.SecureBoot ? "label.secureBoot" : "label.secureBootOff"];
        if (template?.Media is { } media)
            TemplateSummary.Text += Environment.NewLine + labels["media." + media.Verification] +
                (media.Publisher is null ? "" : $" · {media.Publisher}");
        CloneButton.IsEnabled = snapshot?.Host is not null;
        DisposableButton.IsEnabled = CloneButton.IsEnabled;
        RemoveTemplateButton.IsEnabled = template is not null &&
            snapshot?.Machines.All(machine => machine.TemplateId != template.Id) == true;
    }

    private static void SetRows(ListBox list, IEnumerable<LabRow> available)
    {
        var id = (list.SelectedItem as LabRow)?.Id;
        var rows = available.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(row => row.Id == id) ?? rows.FirstOrDefault();
    }

    private string MachineLabel(Machine vm)
    {
        var power = snapshot?.StateOf(vm.Id) ?? PowerState.Unavailable;
        var details = $"{GuestLabel(vm.Guest)} · {labels["power." + power]} · {NetworkLabel(vm.Network)}";
        return vm.Disposable ? details + " · " + labels["session.disposable"] : details;
    }

    private string GuestLabel(GuestFamily guest) => labels["guest." + guest];
    private string NetworkLabel(NetworkMode mode) => labels["network." + mode];
}
