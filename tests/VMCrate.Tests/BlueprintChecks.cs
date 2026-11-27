using System.IO;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class BlueprintChecks
{
    internal static async Task Topology()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        await lab.ImportTemplateAsync("Windows base", GuestFamily.Windows, true, temp.File("source.vhdx", "baseline"));
        var blueprint = new LabBlueprint(1, "qa",
        [
            new("Client", "Windows base", 4, 2, "pair"),
            new("Peer", "Windows base", 4, 2, "pair"),
            new("Separate", "Windows base", 4, 2, "other"),
            new("Offline", "Windows base", 4, 2)
        ]);
        var preview = await lab.PreviewBlueprintAsync(blueprint);
        Expect(preview.Machines.All(vm => !vm.AlreadyCreated) && backend.Machines.Count == 0,
            "Preview mutated Hyper-V or returned unexpected existing VMs.");
        await lab.DeployBlueprintAsync(blueprint);
        await lab.DeployBlueprintAsync(blueprint);
        var snapshot = await lab.ObserveAsync();
        var machines = snapshot.Machines.ToDictionary(vm => vm.Name);
        Expect(machines.Count == 4 && machines.Values.All(item => snapshot.StateOf(item.Id) == PowerState.Off),
            "Deploy must leave new VMs off and keep existing ones.");
        Expect(machines["Client"].PrivateSegment is not null &&
            machines["Client"].PrivateSegment == machines["Peer"].PrivateSegment &&
            machines["Separate"].PrivateSegment != machines["Peer"].PrivateSegment,
            "Declared private segments were not kept separate.");
        Expect(machines["Offline"].Network == NetworkMode.Offline, "A VM without a segment must be offline.");
        await ErrorAsync("error.blueprintConflict", () => lab.SetNetworkAsync(machines["Client"].Id, NetworkMode.Offline));

        await lab.DeployBlueprintAsync(new LabBlueprint(1, "second-lab", [new("Other client", "Windows base", 4, 2, "pair")]));
        var other = (await lab.ObserveAsync()).Machines.Single(vm => vm.Name == "Other client");
        Expect(other.PrivateSegment != machines["Client"].PrivateSegment, "A segment was shared across two labs.");
    }

    internal static async Task Retry()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor { FailCreateName = "Second VM" };
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        await lab.ImportTemplateAsync("Linux base", GuestFamily.Linux, true, temp.File("base.vhdx", "baseline"));
        var blueprint = new LabBlueprint(1, "retry",
            [new("First VM", "Linux base", 4, 2), new("Second VM", "Linux base", 4, 2)]);

        await ErrorAsync("error.backend", () => lab.DeployBlueprintAsync(blueprint));
        var snapshot = await lab.ObserveAsync();
        var originalIds = snapshot.Machines.Select(vm => vm.Id).ToHashSet();
        Expect(backend.Machines.Count == 1 && originalIds.Count == 2, "Deployment did not preserve interrupted progress.");

        backend.FailCreateName = null;
        var preview = await lab.PreviewBlueprintAsync(blueprint);
        Expect(preview.Machines.Count(vm => vm.AlreadyCreated) == 1, "Preview missed the deployment already created.");
        await lab.DeployBlueprintAsync(blueprint);
        Expect(originalIds.SetEquals(backend.Machines.Keys), "Retry changed VM identity.");
        var incompatible = blueprint with { Machines = [blueprint.Machines[0] with { MemoryGiB = 8 }] };
        await ErrorAsync("error.blueprintConflict", () => lab.DeployBlueprintAsync(incompatible));
        Expect(originalIds.SetEquals(backend.Machines.Keys), "Conflict resolution touched existing VMs.");
    }

    internal static async Task Validation()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        var invalid = temp.File("unknown.json", """{"version":1,"id":"qa","machines":[],"command":"example"}""");
        var duplicate = temp.File("duplicate.json", """{"version":1,"id":"one","id":"two","machines":[]}""");
        await ErrorAsync("error.blueprint", () => Task.FromResult(LabBlueprint.Read(invalid)));
        await ErrorAsync("error.blueprint", () => Task.FromResult(LabBlueprint.Read(duplicate)));

        var plan = LabBlueprint.Read(temp.File("valid.json",
            """{"version":1,"id":"qa","machines":[{"name":"VM","template":"missing","memoryGiB":4,"cpuCount":2}]}"""));
        await ErrorAsync("error.template", () => lab.DeployBlueprintAsync(plan));
        await ErrorAsync("error.blueprint", () => lab.DeployBlueprintAsync(plan with { Id = "../outside" }));
        Expect(backend.Machines.Count == 0 && !File.Exists(Path.Combine(temp.Root, "state.json")),
            "Invalid blueprint created resources.");
    }
}
