using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class GroupWorkflowChecks
{
    internal static async Task Lifecycle()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        await lab.ImportTemplateAsync("Base", GuestFamily.Linux, true, temp.File("base.vhdx", "base"));
        var spec = new LabBlueprint(1, "qa", [new("Alpha", "Base", 2, 1, "peer")]);
        await lab.DeployBlueprintAsync(spec);
        var stale = await lab.PreviewLabOperationAsync("qa", LabOperation.Remove);
        await lab.DeployBlueprintAsync(spec with { Machines = [.. spec.Machines, new("Beta", "Base", 2, 1, "peer")] });
        await ErrorAsync("error.labChanged", () => lab.ApplyLabOperationAsync(stale));
        Expect(backend.Machines.Count == 2, "A stale removal plan changed the group.");

        await lab.ApplyLabOperationAsync(await lab.PreviewLabOperationAsync("qa", LabOperation.Start));
        await ErrorAsync("error.labState", () => lab.PreviewLabOperationAsync("qa", LabOperation.Remove));
        await lab.ApplyLabOperationAsync(await lab.PreviewLabOperationAsync("qa", LabOperation.PowerOff));
        await lab.ApplyLabOperationAsync(await lab.PreviewLabOperationAsync("qa", LabOperation.Reset));
        var removal = await lab.PreviewLabOperationAsync("qa", LabOperation.Remove);
        Expect(removal.Members.All(vm => vm.Bytes > 0) && removal.PrivateSegments.Count == 1,
            "Removal preview did not enumerate the affected resources.");
        await lab.ApplyLabOperationAsync(removal);
        Expect(backend.Machines.Count == 0 && backend.RemovedSegments.Count == 1 && (await lab.ObserveAsync()).Templates.Count == 1,
            "Lab removal affected unrelated resources or failed to remove the empty segment.");
    }
}
