namespace VMCrate.Core;

public static class DiagnosticFacts
{
    public static AssistantFact[] Proxy(ProxyHostTrace? host, ProxyGuestTrace? guest)
    {
        var facts = new List<AssistantFact>();
        if (host is not null)
        {
            facts.Add(new("host-vm", $"VM state={host.State}, Hyper-V proxy policy matches={host.PolicyMatches}"));
            facts.Add(new("host-tcp", $"Host TCP to the configured update proxy={host.ProxyTcp}"));
            if (host.Network is { } network)
                facts.Add(new("host-route", $"Host route found={network.RouteKnown}, tunnel={network.IsTunnel}, overlapping routes={network.RouteConflicts}"));
        }
        if (guest is not null)
        {
            facts.Add(new("guest-path", $"Guest route={guest.Route}, gateway={guest.Gateway}, upstream TCP={guest.ProxyTcp}, local relay TCP={guest.RelayTcp}"));
            facts.Add(new("guest-http", $"CONNECT={guest.Connect:000}, HTTPS={guest.Http:000}, curl exit={guest.CurlExit}"));
            facts.Add(new("guest-result", $"VMCrate result={guest.Result}"));
        }
        return facts.ToArray();
    }

    public static AssistantFact[] Build(TemplateBuild build) => build.History.TakeLast(30)
        .Select((entry, index) => new AssistantFact($"build-{index + 1}",
            $"{entry.TimeUtc:u} stage={entry.Stage}; result={entry.ErrorKey ?? "recorded"}; elapsedMs={entry.ElapsedMs}"))
        .Prepend(new AssistantFact("build-stage", $"Guest family={build.Options.Installer.Guest}; current stage={build.Stage}"))
        .ToArray();

    public static AssistantFact[] BuildFailure(TemplateBuild build)
    {
        var facts = Build(build).ToList();
        if (BuildProxyComparison.From(build) is { } comparison)
        {
            facts.Add(new("proxy-before", DescribeCheck(comparison.Baseline)));
            facts.Add(new("proxy-now", DescribeCheck(comparison.Current)));
            facts.Add(new("proxy-change", $"Observed Windows route changed={comparison.RouteChanged?.ToString() ?? "unknown"}"));
        }
        else if (build.ProxyChecks.LastOrDefault() is { } latest)
            facts.Add(new("proxy-now", DescribeCheck(latest)));
        return facts.ToArray();
    }

    private static string DescribeCheck(BuildProxyObservation check)
    {
        var text = $"{check.AtUtc:u}, stage={check.Stage}, source={check.Source}, route={check.Route}, " +
            $"VPN tunnel={check.Tunnel}, policy={check.PolicyMatches}, proxy TCP={check.Tcp}, route conflict={check.RouteConflict}, " +
            $"elapsedMs={check.ElapsedMs}";
        return check.Guest is not { } guest ? text : text + $", repository={check.Target}, " +
            $"guest route={guest.Route}, gateway={guest.Gateway}, relay={guest.RelayTcp}, CONNECT={guest.Connect}, " +
            $"HTTPS={guest.Http}, result={guest.Result}, bytes={guest.DownloadBytes}";
    }
}
