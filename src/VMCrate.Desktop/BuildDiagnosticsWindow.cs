using System.Windows.Controls;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class BuildDiagnosticsWindow : WorkflowWindow
{
    private readonly LabService lab;
    private readonly Guid id;
    private readonly TextBlock baseline;
    private readonly ListBox history;
    private readonly Button hostCheck, guestCheck, next;
    private TemplateBuild? build;
    private Machine? builder;

    internal BuildDiagnosticsWindow(Labels text, LabService lab, Guid id) : base(text, "build.checkHistory")
    {
        this.lab = lab;
        this.id = id;
        baseline = Note("");
        var actions = Actions();
        hostCheck = Action(actions, "trace.checkHost", async () =>
        {
            await Task.Run(() => lab.TraceHostProxyAsync(id));
            await RefreshAsync();
        });
        guestCheck = Action(actions, "tools.proxyTrace", async () =>
        {
            if (builder is not null)
                new ProxyTraceWindow(Text, lab, builder) { Owner = this }.ShowDialog();
            await RefreshAsync();
        });
        next = Action(actions, "tools.help", async () =>
        {
            if (build?.History.LastOrDefault()?.ErrorKey is not { } code)
                return;
            var route = FailureRouting.From(code);
            var snapshot = await Task.Run(lab.ObserveAsync);
            var dialog = RecoveryActions.Create(route, Text, lab, snapshot, id);
            dialog.Owner = this;
            dialog.ShowDialog();
            await RefreshAsync();
        });
        Action(actions, "ai.explain", () =>
        {
            if (build is not null)
                AssistantWindow.Explain(this, Text,
                    "Compare the recorded VMCrate build and proxy checks. Explain the observed differences and likely failure stage. " +
                    "Cite readings and suggest one follow-up check in VMCrate. Distinguish missing readings from failures.",
                    DiagnosticFacts.BuildFailure(build));
            return Task.CompletedTask;
        });
        history = Field("label.history", new ListBox { MaxHeight = 300 });
        Loaded += async (_, _) => await RunAsync(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        var data = await Task.Run(async () => (Build: lab.ReadBuilds().Single(item => item.Id == id), Snapshot: await lab.ObserveAsync()));
        build = data.Build;
        builder = data.Snapshot.Machines.SingleOrDefault(item => item.Id == id && item.Network == NetworkMode.Updates);
        hostCheck.IsEnabled = guestCheck.IsEnabled = builder is not null;
        var failure = build.History.LastOrDefault()?.ErrorKey;
        next.IsEnabled = failure is not null;
        next.Content = Text[RecoveryActions.Label(FailureRouting.From(failure))];
        history.ItemsSource = build.ProxyChecks.Reverse().Select(item => Summary(Text, item)).ToArray();
        var comparison = BuildProxyComparison.From(build);
        baseline.Text = comparison is not null
            ? Text.Format("trace.baseline", Summary(Text, comparison.Baseline))
            : Text["trace.noBaseline"];
        if (comparison is { RouteChanged: true })
            baseline.Text += Environment.NewLine + Text.Format("trace.routeChanged", comparison.Baseline.AtUtc.ToLocalTime().ToString("g"));
    }

    internal static string Summary(Labels text, BuildProxyObservation check)
    {
        var source = check.Source switch
        {
            BuildProxySource.Start => "action.start",
            BuildProxySource.Continue => "action.continue",
            BuildProxySource.Guest => "trace.repository",
            _ => "trace.checkHost"
        };
        var heading = $"{check.AtUtc.ToLocalTime():g} · {text[source]}";
        var result = check.Guest is { } probe ? text["trace.result." + probe.Result]
            : text.Format("trace.hostTcp", text["trace.check." + check.Tcp]);
        if (check.Guest?.KiBPerSecond is double speed)
            result += " · " + text.Format("trace.speed", speed, check.Guest.DownloadBytes);
        if (check.Guest is null && check.Route == ProbeCheck.Failed)
            result += " · " + text["diag.routeMissing"];
        if (check.PolicyMatches == false)
            result += " · " + text["error.updateAcl"];
        var route = check.Tunnel == true ? " · " + text["diag.tunnel"] : "";
        var conflict = check.RouteConflict ? " · " + text["trace.routeConflict"] : "";
        return $"{heading} · {result}{route}{conflict} · {check.ElapsedMs / 1000d:N1} s";
    }
}
