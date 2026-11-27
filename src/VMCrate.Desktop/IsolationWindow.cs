using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class IsolationWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly ComboBox targets;
    private readonly TextBlock host;
    private readonly TextBlock guest;
    private readonly Button checkGuest;

    internal IsolationWindow(Labels text, LabService lab, IReadOnlyList<Machine> machines, Guid? selected)
        : base(text, "tools.isolation", 760, 690)
    {
        this.lab = lab;
        var choices = machines.Select(vm => new Target(vm.Name, vm))
            .Append(new Target("Windows Sandbox", null)).ToArray();
        targets = Field("report.machine", new ComboBox { DisplayMemberPath = nameof(Target.Name), ItemsSource = choices });
        targets.SelectedItem = choices.FirstOrDefault(item => item.Machine?.Id == selected && item.Machine is not null) ?? choices[0];
        var actions = Actions();
        Action(actions, "action.refresh", RefreshAsync, primary: true);
        checkGuest = Action(actions, "action.guestProbe", CheckGuestAsync);
        checkGuest.IsEnabled = false;
        host = Note("");
        guest = Note("");
        AutomationProperties.SetLiveSetting(host, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(guest, AutomationLiveSetting.Polite);

        Loaded += async (_, _) => await RunAsync(RefreshAsync);
        targets.SelectionChanged += async (_, _) => await RunAsync(RefreshAsync);
    }

    private Machine? SelectedMachine => (targets.SelectedItem as Target)?.Machine;

    private async Task RefreshAsync()
    {
        checkGuest.IsEnabled = false;
        host.Text = Status(null);
        guest.Text = "";
        if (SelectedMachine is not { } vm)
        {
            host.Text = Text["hint.sandbox"];
            guest.Text = Row(Text["tools.isolation"], Status(null));
            return;
        }

        Render(await Task.Run(() => lab.ObserveIsolationAsync(vm.Id)), vm);
    }

    private Task CheckGuestAsync()
    {
        if (SelectedMachine is not { } vm)
            return Task.CompletedTask;
        if (vm.Guest == GuestFamily.Linux)
        {
            new LinuxIsolationDialog(Text, lab, vm.Id, observation => Render(observation, vm)) { Owner = this }.ShowDialog();
            return Task.CompletedTask;
        }
        new IsolationGuestDialog(Text, async (user, password) =>
        {
            guest.Text = Status(null);
            var observation = await Task.Run(() => lab.CheckWindowsIsolationAsync(vm.Id, user, password));
            Render(observation, vm);
        }) { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }

    private void Render(IsolationObservation observation, Machine vm)
    {
        var measured = observation.Host;
        var connected = measured.Switches.Length > 0
            ? string.Join(", ", measured.Switches.Select(sw => sw.Type is null ? sw.Name : $"{sw.Name} ({sw.Type})"))
            : measured.NetworkPolicyMatches is null ? Status(null) : Text["isolation.disconnected"];
        var profile = Text["network." + observation.Profile];
        if (observation.Proxy is { } proxy)
            profile += $" ({proxy.Address}:{proxy.Port})";

        var rows = new List<string>
        {
            Row("Hyper-V", observation.ObservedUtc.ToLocalTime().ToString("g")),
            Row(Text["label.network"], profile),
            Row(Text["isolation.power"], Text["power." + measured.State]),
            Row(Text["isolation.network"], Status(measured.NetworkPolicyMatches)),
            Row(Text["isolation.switch"], connected)
        };
        if (observation.Profile == NetworkMode.Private)
            rows.Add(Row(Text["isolation.membership"], Status(measured.PrivateMembershipMatches)));

        rows.Add(Row(Text["isolation.fileCopy"], Status(Disabled(measured.FileCopyEnabled))));
        rows.Add(Row(Text["isolation.kvp"], Status(Disabled(measured.KeyValueExchangeEnabled))));
        rows.Add(Row(Text["isolation.redirect"], Status(measured.EnhancedSessionAllowed == false ? true : null)));
        rows.Add(Row(Text["isolation.media"], Status(measured.MediaLayoutMatches)));
        rows.Add(Row(Text["isolation.inbox"], Present(measured.InboxAttached)));
        host.Text = string.Join(Environment.NewLine, rows);
        RenderGuest(observation);
        checkGuest.IsEnabled = observation.Profile != NetworkMode.Updates &&
                               measured is { State: PowerState.Running, NetworkPolicyMatches: true };
    }

    private void RenderGuest(IsolationObservation observation)
    {
        guest.Text = observation.Guest is { } measured && observation.GuestObservedUtc is { } time
            ? string.Join(Environment.NewLine,
                Row(observation.GuestReported ? Text["isolation.guestReport"] : "PowerShell Direct", time.ToLocalTime().ToString("g")),
                Text.Format("isolation.routes", measured.ActiveAdapters, Count(measured.IPv4DefaultRoutes),
                    Count(measured.IPv6DefaultRoutes), Count(measured.DnsServers)),
                Row(Text["isolation.network"], Status(observation.DefaultRouteCheck)),
                Row(Text["isolation.inbox"], Present(measured.InboxReadable)))
            : Row(Text["action.guestProbe"], Status(null));
    }

    private string Count(int? count) => count?.ToString() ?? Status(null);
    private string Present(bool? present) => present is null
        ? Status(null) : Text[present == true ? "isolation.present" : "isolation.absent"];

    private string Status(bool? passes) =>
        Text["trace.check." + (passes is true ? ProbeCheck.Passed : passes is false ? ProbeCheck.Failed : ProbeCheck.Unknown)];

    private static bool? Disabled(bool? enabled) => enabled is bool value ? !value : null;
    private static string Row(string label, string value) => $"{label} · {value}";
    private sealed record Target(string Name, Machine? Machine);
}

internal sealed class IsolationGuestDialog : WorkflowWindow
{
    internal IsolationGuestDialog(Labels text, Func<string, string, Task> check, string action = "action.guestProbe",
        string hint = "isolation.credentials")
        : base(text, action, 520, 420)
    {
        Note(text[hint]);
        var user = Field("label.username", new TextBox());
        var password = Field("label.password", new PasswordBox { MinHeight = 34 });
        var submit = Action(Actions(), action, async () =>
        {
            var secret = password.Password;
            password.Clear();
            await check(user.Text, secret);
            FinishDialog();
        }, primary: true);
        submit.IsDefault = true;
        Loaded += (_, _) => user.Focus();
    }
}
