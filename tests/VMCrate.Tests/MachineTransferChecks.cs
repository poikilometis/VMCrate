using System.IO;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class MachineTransferChecks
{
    internal static async Task CopyAndResume()
    {
        using var fixture = new TemporaryDirectory();
        var host = new FakeHypervisor();
        var source = new LabService(Path.Combine(fixture.Root, "source"), host, new FakeInbox());
        await source.ImportTemplateAsync("Linux base", GuestFamily.Linux, false, fixture.File("base.vhdx", "base"));
        var template = (await source.ObserveAsync()).Templates.Single();
        await source.CreateCloneAsync(template.Id, new("Original", 4, 2));
        var original = (await source.ObserveAsync()).Machines.Single();
        var contents = "working guest disk with the session data";
        File.WriteAllText(host.Machines[original.Id].Disk, contents);
        var package = Path.Combine(fixture.Root, "package");
        await source.StartAsync(original.Id);
        await ErrorAsync("error.transferState", () => source.ExportMachineAsync(original.Id, package));
        await source.PowerOffAsync(original.Id);
        var transfer = await source.ExportMachineAsync(original.Id, package);
        var manifest = Path.Combine(package, MachineTransfer.ManifestName);
        var disk = Path.Combine(package, MachineTransfer.DiskName);
        Expect(transfer == MachineTransfer.Read(manifest) && File.Exists(host.Machines[original.Id].Disk),
            "The transfer must retain the source and record its checked metadata.");

        var destination = Path.Combine(fixture.Root, "destination");
        var target = new LabService(destination, host, new FakeInbox());
        File.WriteAllText(disk, new string('x', contents.Length));
        await ErrorAsync("error.projectIntegrity", () => target.ImportMachineAsync(manifest, "Copy"));
        Expect((await target.ObserveAsync()).Machines.Count == 0, "A changed disk must leave no registration.");
        File.WriteAllText(disk, contents);
        host.FailAfterCreate = true;
        await ErrorAsync("error.backend", () => target.ImportMachineAsync(manifest, "Copy"));
        host.FailAfterCreate = false;

        target = new LabService(destination, host, new FakeInbox());
        var result = await target.ImportMachineAsync(manifest, "Copy");
        var imported = (await target.ObserveAsync()).Machines.Single();
        Expect(imported is { Network: NetworkMode.Offline, TemplateId: null, MemoryGiB: 4, CpuCount: 2 } &&
            imported.Id == result && imported.Id != original.Id && File.ReadAllText(host.Machines[result].Disk) == contents &&
            host.Machines.Count == 2, "Retry must acknowledge the same offline destination registration and preserve the old VM.");
    }
}
