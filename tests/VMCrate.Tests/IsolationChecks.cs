using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class IsolationChecks
{
    internal static async Task CurrentEvidence()
    {
        using var temp = new TemporaryDirectory();
        var hypervisor = new FakeHypervisor();
        var lab = new LabService(temp.Root, hypervisor, new FakeInbox());
        await lab.ImportTemplateAsync("Fixture", GuestFamily.Windows, true, temp.File("base.vhdx", "fixture"));
        await lab.DeployBlueprintAsync(new LabBlueprint(1, "isolation",
        [
            new("Origin", "Fixture", 4, 2, "one"),
            new("Peer", "Fixture", 4, 2, "one"),
            new("Other", "Fixture", 4, 2, "two")
        ]));
        var machines = (await lab.ObserveAsync()).Machines.ToDictionary(vm => vm.Name, vm => vm.Id);
        var origin = machines["Origin"];
        var hostOnly = await lab.ObserveIsolationAsync(origin);
        Expect(hostOnly is { Profile: NetworkMode.Private, Host.PrivateMembershipMatches: true } &&
            hostOnly.Guest is null && hostOnly.GuestObservedUtc is null, "Host configuration was used as guest evidence.");
        Expect(hypervisor.IsolationPeers.ToHashSet().SetEquals([origin, machines["Peer"]]),
            "The selected segment included a different segment.");

        await lab.StartAsync(origin);
        var running = await lab.CheckWindowsIsolationAsync(origin, "fixture", "fixture");
        Expect(running is { Guest.IPv4DefaultRoutes: 0, Guest.IPv6DefaultRoutes: 0, GuestObservedUtc: not null },
            "The guest sample was discarded despite verified host observations.");

        hypervisor.GuestNetworkObserved = () => hypervisor.NetworkDrift = true;
        var drift = await lab.CheckWindowsIsolationAsync(origin, "fixture", "fixture");
        Expect(drift is { Host.NetworkPolicyMatches: false, Guest: null }, "Evidence survived a policy change.");
        var calls = hypervisor.GuestNetworkCalls;
        await lab.CheckWindowsIsolationAsync(origin, "fixture", "fixture");
        Expect(hypervisor.GuestNetworkCalls == calls, "A remoting call ran while the VM policy differed.");
    }
}
