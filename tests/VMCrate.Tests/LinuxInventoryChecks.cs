using System.IO;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class LinuxInventoryChecks
{
    internal static async Task OfflineCollector()
    {
        using var fixture = new TemporaryDirectory();
        var host = new FakeHypervisor();
        var lab = new LabService(fixture.Root, host, new FakeInbox());
        await lab.ImportTemplateAsync("Linux base", GuestFamily.Linux, false, fixture.File("base.vhdx", "base"));
        var template = (await lab.ObserveAsync()).Templates.Single();
        await lab.CreateCloneAsync(template.Id, new("Inventory clone", 4, 2));
        var machine = (await lab.ObserveAsync()).Machines.Single();
        await ErrorAsync("error.linuxInventory", () => lab.MountLinuxInventoryCollectorAsync(Guid.NewGuid(), machine.Id));
        await lab.MountLinuxInventoryCollectorAsync(template.Id, machine.Id);
        Expect(host.Machines[machine.Id].Inbox is not null, "The offline Linux collector was not mounted.");
        await lab.SetNetworkAsync(machine.Id, NetworkMode.Private);
        await ErrorAsync("error.linuxInventory", () => lab.MountLinuxInventoryCollectorAsync(template.Id, machine.Id));

        var script = Path.Combine(fixture.Root, "collector.sh");
        LabService.SaveLinuxInventoryCollector(script);
        var bytes = File.ReadAllText(script);
        Expect(bytes.StartsWith("#!/bin/sh\n") && !bytes.Contains('\r'), "Guest scripts must use LF line endings.");
        var local = new PackageInventory(1, "LinuxLocal:kali", [new("pkg", "1.1-1")]);
        await ErrorAsync("inventory.localHint", () => new AdvisoryClient().CheckAsync(local));
        Expect(InventoryComparison.Compare(local, local with { Packages = [new("pkg", "1.2-1")] }).Count == 1,
            "An unsupported advisory ecosystem must still have a local diff.");
    }
}
