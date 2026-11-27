using System.Windows;
using System.Windows.Controls;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

internal sealed class ProxyTraceWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly Machine vm;
    private readonly ComboBox target;
    private readonly TextBox relay;
    private readonly CheckBox measure;
    private readonly TextBlock host, guest;
    private ProxyHostTrace? lastHost;
    private DateTimeOffset? lastHostAt;
    private ProxyGuestTrace? lastGuest;
    private ProxyTarget? mountedTarget;

    internal ProxyTraceWindow(Labels text, LabService lab, Machine vm) : base(text, "tools.proxyTrace", 740, 760)
    {
        this.lab = lab;
        this.vm = vm;
        Note(Text.Format("trace.vm", vm.Name, $"{vm.Proxy?.Address}:{vm.Proxy?.Port}"));
        target = Field("trace.repository", new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Target" });
        target.ItemsSource = Enum.GetValues<ProxyTarget>().Select(key =>
            new TargetChoice(key, $"{key} · {new ProxyTraceOptions(key).Repository.Host}")).ToArray();
        target.SelectedValue = vm.Guest == GuestFamily.Windows ? ProxyTarget.WindowsUpdate : ProxyTarget.Kali;
        relay = Field("trace.relayPort", new TextBox { MaxLength = 5, FlowDirection = FlowDirection.LeftToRight });
        measure = new CheckBox { Content = Text["trace.measure"], Margin = new Thickness(0, 6, 0, 6) };
        Body.Children.Add(measure);
        measure.IsEnabled = Options().CanMeasureDownload;
        target.SelectionChanged += (_, _) => measure.IsEnabled = Options().CanMeasureDownload;
        Action(Actions(), "trace.checkHost", CheckHostAsync);
        host = Note("");
        guest = vm.Guest == GuestFamily.Windows ? AddWindowsProbe() : AddLinuxProbe();
        Action(Actions(), "ai.explain", () =>
        {
            AssistantWindow.Explain(this, Text, "Explain the proxy path and identify the observed stage of the problem",
                DiagnosticFacts.Proxy(lastHost, lastGuest));
            return Task.CompletedTask;
        });
    }

    private ProxyTraceOptions Options() => new((ProxyTarget)target.SelectedValue,
        string.IsNullOrWhiteSpace(relay.Text) ? null : Integer(relay.Text), measure.IsEnabled && measure.IsChecked == true);

    private async Task CheckHostAsync()
    {
        var check = await Task.Run(() => lab.TraceHostProxyAsync(vm.Id));
        var previous = lastHostAt;
        var changed = check.RouteDiffersFrom(lastHost);
        lastHost = check;
        lastHostAt = DateTimeOffset.Now;
        lastGuest = null;
        guest.Text = "";
        var network = check.Network;
        var route = network?.RouteKnown == true
            ? Text.Format("diag.route", network.Interface ?? "—", network.SourceAddress ?? "—") +
                (network.IsTunnel == true ? " · " + Text["diag.tunnel"] : "")
            : Text["diag.routeMissing"];
        var networkName = check.InternalSwitch is { } internalSwitch
            ? $"{internalSwitch.Name} · {string.Join(", ", internalSwitch.IPv4Networks)}"
            : Text["trace.switchUnknown"];
        host.Text = string.Join(Environment.NewLine, $"Hyper-V · {lastHostAt.Value:g}", route, networkName,
            Text.Format("trace.policy", Check(check.PolicyMatches ? ProbeCheck.Passed : ProbeCheck.Failed),
                Text["power." + check.State]),
            Text.Format("trace.hostTcp", Check(check.ProxyTcp)));
        if (network?.OverlappingNats == true || network?.RouteConflicts > 0)
            host.Text += Environment.NewLine + Text["trace.routeConflict"];
        if (changed && previous is { } time)
            host.Text += Environment.NewLine + Text.Format("trace.routeChanged", time.ToString("g"));
    }

    private TextBlock AddWindowsProbe()
    {
        var user = Field("label.username", new TextBox());
        var password = Field("label.password", new PasswordBox { MinHeight = 34 });
        var proxyUser = Field("trace.proxyUser", new TextBox());
        var proxyPassword = Field("trace.proxyPassword", new PasswordBox { MinHeight = 34 });
        var save = new CheckBox { Content = Text["proxy.saveCredential"], Margin = new Thickness(0, 8, 0, 8) };
        Body.Children.Add(save);
        Action(Actions(), "proxy.loadCredential", () =>
        {
            if (vm.Proxy is not { } configured)
                return Task.CompletedTask;
            var secret = ProxyCredentialStore.Read(configured);
            proxyUser.Text = secret?.User ?? "";
            proxyPassword.Password = secret?.Password ?? "";
            return Task.CompletedTask;
        });
        Action(Actions(), "trace.checkGuest", async () =>
        {
            var options = Options();
            var credentials = (user.Text, GuestPassword: password.Password, ProxyUser: proxyUser.Text,
                ProxyPassword: proxyPassword.Password);
            password.Clear();
            proxyPassword.Clear();
            await CheckHostAsync();
            var result = await Task.Run(() => lab.TraceWindowsProxyAsync(vm.Id, options,
                credentials.Text, credentials.GuestPassword, credentials.ProxyUser, credentials.ProxyPassword, vm.Proxy));
            if (save.IsChecked == true && result.Result == ProxyResult.Reachable && vm.Proxy is { } configured)
                await Task.Run(() => ProxyCredentialStore.Write(configured, new(credentials.ProxyUser, credentials.ProxyPassword)));
            ShowResult(result);
        }, true);
        return Note("");
    }

    private TextBlock AddLinuxProbe()
    {
        Note(Text["trace.linux"]);
        var actions = Actions();
        Action(actions, "trace.mountProbe", async () =>
        {
            if (!Text.Confirm(this, "confirm.mountProbe"))
                return;
            var options = Options();
            await CheckHostAsync();
            await Task.Run(() => lab.AttachLinuxProxyProbeAsync(vm.Id, options));
            mountedTarget = options.Target;
            guest.Text = Text["trace.probeMounted"];
        }, true);
        Action(actions, "action.console", async () =>
        {
            var snapshot = await Task.Run(lab.ObserveAsync);
            if (snapshot.Host?.EnhancedSession != true || Text.Confirm(this, "confirm.console"))
                await Task.Run(() => lab.OpenConsoleAsync(vm.Id));
        });
        var result = Field("trace.resultCode", new TextBox { MaxLength = 128, FlowDirection = FlowDirection.LeftToRight });
        Action(Actions(), "trace.readResult", async () =>
        {
            var trace = ProxyGuestTrace.Parse(result.Text);
            await Task.Run(() => lab.RecordGuestProxy(vm.Id, trace, mountedTarget));
            ShowResult(trace);
        });
        return Note("");
    }

    private void ShowResult(ProxyGuestTrace result)
    {
        lastGuest = result;
        guest.Text = string.Join(Environment.NewLine, Text["trace.result." + result.Result],
        Text.Format("trace.guestPath", Check(result.Route), Check(result.Gateway), Check(result.ProxyTcp), Check(result.RelayTcp)),
        Text.Format("trace.codes", result.Connect, result.Http, result.CurlExit));
        if (result.KiBPerSecond is double speed)
            guest.Text += Environment.NewLine + Text.Format("trace.speed", speed, result.DownloadBytes);
    }

    private string Check(ProbeCheck check) => Text["trace.check." + check];
    private sealed record TargetChoice(ProxyTarget Target, string Name);
}
