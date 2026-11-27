using System.IO;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class RecoveryPointChecks
{
    internal static async Task ResumeGroupRestore()
    {
        using var fixture = new TemporaryDirectory();
        var host = new FakeHypervisor();
        var lab = new LabService(fixture.Root, host, new FakeInbox());
        await lab.ImportTemplateAsync("Base", GuestFamily.Windows, true, fixture.File("base.vhdx", "base"));
        var blueprint = new LabBlueprint(1, "sample",
            [new("One", "Base", 2, 1, "seg"), new("Two", "Base", 2, 1, "seg")]);
        await lab.DeployBlueprintAsync(blueprint);
        var scope = new RecoveryPointScope(null, blueprint.Id);
        var current = host.Machines.ToDictionary(pair => pair.Key, pair => pair.Value.Disk);
        foreach (var path in current.Values)
            File.WriteAllText(path, "checkpoint");
        var point = await lab.CaptureRecoveryPointAsync(scope, "Baseline");
        Expect(point.Disks.Length == 2, "Both lab members must be captured.");

        foreach (var path in current.Values)
            File.WriteAllText(path, "latest");
        host.InterruptDiskRestore = true;
        await ErrorAsync("error.backend", () => lab.RestoreRecoveryPointAsync(point.Id));
        var firstApplied = host.Machines.Single(vm => vm.Value.Disk != current[vm.Key]);
        await ErrorAsync("error.recoveryRequired", () => lab.StartAsync(firstApplied.Key));
        await ErrorAsync("error.recoveryRequired", () => lab.PreviewLabOperationAsync(blueprint.Id, LabOperation.Start));
        lab = new LabService(fixture.Root, host, new FakeInbox());
        Expect((await lab.InspectRecoveryAsync()).Any(item => item.Kind == RecoveryKind.ResumeDiskRestore),
            "The restore journal must survive the loss of the application.");
        host.InterruptDiskRestore = false;
        await lab.RestoreRecoveryPointAsync(point.Id);
        Expect(host.Machines[firstApplied.Key].Disk == firstApplied.Value.Disk &&
            host.Machines.Values.All(vm => File.ReadAllText(vm.Disk) == "checkpoint") &&
            current.Values.All(path => File.ReadAllText(path) == "latest"),
            "Restoring did not resume the same plan or preserve the previous disks.");
        await lab.StartAsync(firstApplied.Key);
    }
}
