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
}
