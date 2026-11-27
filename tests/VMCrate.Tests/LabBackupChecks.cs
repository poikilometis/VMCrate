using System.IO;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class LabBackupChecks
{
    internal static async Task Restore()
    {
        using var temp = new TemporaryDirectory();
        var sourceHost = new FakeHypervisor();
        var source = new LabService(Path.Combine(temp.Root, "source"), sourceHost, new FakeInbox());
        await source.ImportTemplateAsync("Base", GuestFamily.Linux, false, temp.File("base.vhdx", "original base"));
        var blueprint = new LabBlueprint(1, "private-lab", [new("First", "Base", 4, 2, "lan"), new("Second", "Base", 4, 2, "lan")]);
        await source.DeployBlueprintAsync(blueprint);
        foreach (var vm in sourceHost.Machines.Values)
            File.WriteAllText(vm.Disk, vm.Location.Machine.Name + " work");
        var folder = Path.Combine(temp.Root, "backup");
        var backup = await source.BackupLabAsync(blueprint.Id, folder);
        Expect(backup.Templates.Length == 1 && backup.Machines.Length == 2, "The backup lost dependencies.");

        var host = new FakeHypervisor();
        var target = new LabService(Path.Combine(temp.Root, "target"), host, new FakeInbox());
        var manifest = Path.Combine(folder, LabBackup.ManifestName);
        var plan = await target.PreviewLabBackupAsync(manifest);
        Expect(plan.RequiredBytes > 0 && plan.AlreadyRestored == 0, "Preview did not calculate required files.");
        var file = LabBackup.MachinePath(folder, backup.Machines[1].Id);
        var saved = File.ReadAllBytes(file);
        File.WriteAllBytes(file, new byte[saved.Length]);
        await ErrorAsync("error.projectIntegrity", () => target.RestoreLabBackupAsync(manifest, plan));
        Expect((await target.ObserveAsync()) is { Templates.Count: 0, Machines.Count: 0 }, "Corrupt media caused a partial restore.");
        File.WriteAllBytes(file, saved);

        host.FailAfterCreate = true;
        await ErrorAsync("error.backend", () => target.RestoreLabBackupAsync(manifest, plan));
        var first = host.Machines.Values.Single();
        host.FailAfterCreate = false;
        await target.RestoreLabBackupAsync(manifest, plan);
        var complete = await target.ObserveAsync();
        var disks = host.Machines.Values.ToArray();
        Expect(complete.Templates.Count == 1 && complete.Machines.Count == 2 &&
            complete.Machines.Select(vm => vm.PrivateSegment).Distinct().Single() is Guid segment &&
            segment != backup.Machines[0].PrivateSegment &&
            disks.All(vm => vm is { Power: PowerState.Off, Location.Machine.Network: NetworkMode.Private } &&
                vm.Location.Machine.BlueprintId == blueprint.Id && vm.Location.BaseDisk is not null &&
                File.ReadAllText(vm.Disk) == vm.Location.Machine.Name + " work"), "The restore lost VM state, boot mode, or topology.");
        Expect(host.Machines[first.Location.Machine.Id].Disk == first.Disk, "Retry replaced a healthy registered disk.");
        var repeated = await target.PreviewLabBackupAsync(manifest);
        Expect(repeated.AlreadyRestored == 2 && repeated.RequiredBytes == 0, "Completed restore is not recognized.");
        await target.RestoreLabBackupAsync(manifest, repeated);
        Expect(host.Machines.Count == 2, "A repeat restore duplicated a VM.");
    }
}
