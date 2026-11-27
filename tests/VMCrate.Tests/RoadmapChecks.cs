using System.IO;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class RoadmapChecks
{
    internal static async Task OfflineKitAndPins()
    {
        using var fixture = new TemporaryDirectory();
        var source = Path.Combine(fixture.Root, "source");
        var lab = new LabService(source, new FakeHypervisor(), new FakeInbox());
        await lab.ImportTemplateAsync("Windows Base", GuestFamily.Windows, true, fixture.File("original.vhdx", "verified fixture"));
        var template = (await lab.ObserveAsync()).Templates.Single();
        lab.SaveSessionProfile(template.Id, new("Saved Offline", 4, 2));
        var recipe = new LabBlueprint(1, "offline-kit", [new("Test VM", template.Name, 4, 2)]);
        var lockPath = Path.Combine(fixture.Root, "test.lock.json");
        await OfflinePlanning.SaveLockAsync(lockPath, source, recipe);
        var pinned = OfflinePlanning.ReadLock(lockPath);
        Expect((await OfflinePlanning.PlanAsync(source, recipe, pinned)).Ready, "Pinned dependencies could not be resolved.");
        await ErrorAsync("error.projectChanged", () => OfflinePlanning.PlanAsync(source,
            recipe with { Machines = [new("Other", template.Name, 4, 2)] }, pinned));

        var destination = Path.Combine(fixture.Root, "copied library");
        await lab.CopyLibraryAsync(lab.PreviewLibraryCopy(destination));
        var manifest = await OfflineLibraryManifest.VerifyAsync(destination);
        Expect(manifest.Files.Length == 5 && File.Exists(Path.Combine(destination, "operations.md")),
            "The portable library is missing dependencies or local documentation.");
        var copy = new LabService(destination, new FakeHypervisor(), new FakeInbox());
        Expect(copy.ReadSessionProfiles().Single().TemplateId == template.Id &&
            (await OfflinePlanning.PlanAsync(destination, recipe, pinned)).Ready,
            "A destination library did not reuse the pinned template.");
        File.AppendAllText(Path.Combine(destination, "operations.md"), "changed");
        await ErrorAsync("error.projectIntegrity", () => OfflineLibraryManifest.VerifyAsync(destination));
    }

    internal static async Task InstallerPreparation()
    {
        using var fixture = new TemporaryDirectory();
        var host = new FakeHypervisor();
        var lab = new LabService(fixture.Root, host, new FakeInbox());
        var installer = fixture.File("installer.iso", "installer");
        var bootSeed = fixture.File("seed.iso", "boot seed fixture");
        var hash = await lab.CalculateLocalIsoDigestAsync(installer);
        var seedHash = await lab.CalculateLocalIsoDigestAsync(bootSeed);
        var job = lab.QueueBuild(new("Linux Setup", "r1", new(new("Linux", 4, 2), GuestFamily.Linux, false,
            installer, 32), hash, bootSeed, BootSeedHash: seedHash, VerifyCloneOnPublish: true));

        var blocked = await lab.PrepareNextQueuedBuildAsync(new(2, 8, 256L << 30));
        Expect(blocked?.BlockedBy == "error.resources" && host.Machines.Count == 0, "The queue exceeded its memory budget.");
        var limit = new BuildQueueLimits(8, 8, 256L << 30);
        Expect((await lab.PrepareNextQueuedBuildAsync(limit))?.Stage == BuildStage.Provision, "ISO verification did not advance.");
        Expect((await lab.PrepareNextQueuedBuildAsync(limit))?.Stage == BuildStage.Install &&
            host.Machines[job.Id] is { Inbox: not null, Power: PowerState.Off }, "Preparation failed to attach the verified seed.");
        await lab.EjectBuildMediaAsync(job.Id);
        Expect(host.Machines[job.Id] is { Inbox: null, Installer: null }, "Both prepared media attachments must be ejected.");
        host.BootVerified = true;
        await lab.StartAsync(job.Id);
        await lab.ContinueBuildAsync(job.Id);
        await lab.PowerOffAsync(job.Id);
        await lab.ContinueBuildAsync(job.Id);
        Expect(lab.ReadBuilds().Single().Stage == BuildStage.BootTest && (await lab.ObserveAsync()).Templates.Count == 0,
            "The captured image was published before an Offline test boot.");
        await ErrorAsync("error.buildBoot", () => lab.CreateCloneAsync(job.Id, new("Premature", 2, 1)));
        host.FailStart = true;
        await ErrorAsync("error.backend", () => lab.ContinueBuildAsync(job.Id));
        Expect((await lab.ObserveAsync()).Templates.Count == 0, "A failed boot published the captured template.");
        host.FailStart = false;
        await lab.ContinueBuildAsync(job.Id);
        Expect((await lab.ObserveAsync()).Templates.Single() is { Published: true, BootVerifiedUtc: not null },
            "A verified build did not publish its template.");
    }

    internal static async Task PrivateAddressChecks()
    {
        using var fixture = new TemporaryDirectory();
        var host = new FakeHypervisor();
        var lab = new LabService(fixture.Root, host, new FakeInbox());
        await lab.ImportTemplateAsync("Base", GuestFamily.Windows, true, fixture.File("base.vhdx", "fixture"));
        await lab.DeployBlueprintAsync(new LabBlueprint(1, "address-lab",
            [new("Windows", "Base", 4, 2, "private"), new("Second", "Base", 4, 2, "other")]));
        var view = await lab.ReadNetworkTopologyAsync("address-lab");
        var vm = view.Machines.Single(item => item.Name == "Windows");
        Expect(vm.ProposedIPv4 is not null &&
            view.Machines.Select(item => item.ProposedIPv4!.Split('/')[0]).Distinct().Count() == 2,
            "A private address plan reused an address across segments.");
        await lab.StartAsync(vm.Id);
        await ErrorAsync("error.labChanged", () => lab.ApplyPrivateWindowsAddressAsync("address-lab", vm.Id,
            "10.214.7.3/24", "fixture", "secret"));
        await lab.ApplyPrivateWindowsAddressAsync("address-lab", vm.Id, vm.ProposedIPv4!, "fixture", "secret");
        Expect(host.LastPrivateAddress == vm.ProposedIPv4!.Split('/')[0], "The adapter did not receive the reviewed address.");
        await ErrorAsync("error.guestProbe", () => lab.ReadWindowsInventoryAsync(vm.Id, "fixture", "secret"));
        var template = (await lab.ObserveAsync()).Templates.Single();
        await lab.CreateCloneAsync(template.Id, new("Trusted", 4, 2));
        var trusted = (await lab.ObserveAsync()).Machines.Single(item => item.Name == "Trusted");
        await lab.StartAsync(trusted.Id);
        Expect((await lab.ReadWindowsInventoryAsync(trusted.Id, "fixture", "secret")).Ecosystem == "WindowsCBS",
            "The Windows inventory did not follow the Offline clone path.");
    }

    internal static async Task ExpiredProxyAndDiff()
    {
        using var fixture = new TemporaryDirectory();
        var host = new FakeHypervisor();
        var lab = new LabService(fixture.Root, host, new FakeInbox());
        await lab.CreateInstallerAsync(new(new("Builder", 4, 2), GuestFamily.Windows, true,
            fixture.File("build.iso", "fixture"), 32));
        var id = (await lab.ObserveAsync()).Machines.Single().Id;
        var proxy = UpdateProxy.Create(Guid.NewGuid(), "203.0.113.15", 8080, DateTimeOffset.UtcNow.AddMinutes(30));
        await lab.SetUpdateProxyAsync(id, proxy);
        await lab.StartAsync(id);
        await lab.ExpireUpdateLeaseAsync(id);
        Expect(host.Machines[id].Location.Machine.Network == NetworkMode.Updates, "An active lease was closed early.");

        var store = new StateStore(fixture.Root);
        var state = store.Load();
        state.Machines[0] = state.Machines[0] with { Proxy = proxy with { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) } };
        store.Save(state);
        await lab.ExpireUpdateLeaseAsync(id);
        Expect(host.Machines[id] is { Power: PowerState.Running, Location.Machine.Network: NetworkMode.Offline } &&
            (await lab.ObserveAsync()).Machines.Single().Proxy is null, "Expiry changed power or left the proxy attached.");

        var before = new PackageInventory(1, "Debian:13", [new("a", "1"), new("x", "1")]);
        var after = new PackageInventory(1, "Debian:13", [new("a", "2"), new("x", "1"), new("y", "1")]);
        var changes = InventoryComparison.Compare(before, after);
        Expect(changes.Select(item => item.Name).SequenceEqual(["a", "y"]) && changes[1].Before.Length == 0,
            "Package comparison missed an addition or included an unchanged dependency.");
    }
}
