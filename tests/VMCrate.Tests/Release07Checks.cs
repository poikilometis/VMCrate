using System.IO;
using System.Text.Json;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class Release07Checks
{
    internal static async Task QueueReplay()
    {
        using var temp = new TemporaryDirectory();
        var lab = new LabService(temp.Root, new FakeHypervisor(), new FakeInbox());
        var source = temp.File("fixture.iso", "installer ISO fixture");
        var hash = (await FileDigests.InspectAsync(source)).Sha256;
        var build = lab.QueueBuild(new("Local jobs", "r1",
            new(new("Installer", 4, 2), GuestFamily.Windows, true, source, 64), hash, null));
        var queue = new LocalJobQueue(temp.Root);
        var request = new LocalJobRequest(LocalJobKind.Build, build.Id, BuildStage.Verify);
        var interrupted = queue.Enqueue(request);
        Expect(queue.Enqueue(request).Id == interrupted.Id, "Work was not deduplicated.");
        using (queue.TryOwnWorker())
        {
            Expect(queue.TryOwnWorker() is null, "Two local workers owned the same workspace.");
            Expect(queue.Claim()?.Id == interrupted.Id, "Work was not claimed.");
            await lab.ContinueBuildAsync(build.Id);
        }
        await new LocalJobWorker(lab, new OfficialImages()).RunAsync();
        Expect(queue.Read().Single() is { Attempts: 2, Status: LocalJobStatus.Succeeded, ElapsedMs: >= 0 } &&
            lab.ReadBuilds().Single().Stage == BuildStage.Provision, "Lost reply re-ran a later build stage.");
        var cancelled = queue.Enqueue(request with { Stage = BuildStage.Provision });
        queue.Cancel(cancelled.Id);
        Expect(queue.Claim() is null, "Cancelled work was executed.");
        queue.Retry(cancelled.Id);
        Expect(queue.Claim()?.Id == cancelled.Id, "Retry did not resume the same job.");
    }

    internal static async Task RouteDrift()
    {
        using var temp = new TemporaryDirectory();
        var host = new FakeHypervisor();
        var lab = new LabService(temp.Root, host, new FakeInbox());
        var iso = temp.File("test.iso", "fixture");
        await lab.CreateInstallerAsync(new(new("Builder", 4, 2), GuestFamily.Linux, false, iso, 48));
        var machine = (await lab.ObserveAsync()).Machines.Single();
        var sw = Guid.NewGuid();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(30);
        var proxy = UpdateProxy.Create(sw, "203.0.113.7", 8080, expiry);
        host.ProxyRoute = host.ProxyRoute with { Switches = [new(sw, "fixture", "Internal", ["192.0.2.1/24"])],
            InterfaceIndex = 5, NextHop = "192.0.2.254", NatPrefixes = ["192.0.2.0/24"] };
        await lab.SetUpdateProxyAsync(machine.Id, proxy with { ReviewedRoute = proxy.RouteFingerprint(host.ProxyRoute) });
        await lab.StartAsync(machine.Id);
        Expect(!await lab.WatchUpdateRouteAsync(machine.Id, expiry), "A stable route closed the lease.");
        host.ProxyRoute = host.ProxyRoute with { NextHop = "192.0.2.253" };
        using (new StateStore(temp.Root).Lock())
        {
            await ErrorAsync("error.busy", () => lab.WatchUpdateRouteAsync(machine.Id, expiry));
            Expect(host.Machines[machine.Id] is { Power: PowerState.Running, Location.Machine.Network: NetworkMode.Offline },
                "A busy catalog held the network open.");
        }
        Expect(await lab.WatchUpdateRouteAsync(machine.Id, expiry) &&
            (await lab.ObserveAsync()).Machines.Single() is { Network: NetworkMode.Offline, Proxy: null } &&
            lab.ReadOperations(machine.Id).Last().Code == "error.proxyDrift" &&
            FailureRouting.From("error.proxyDrift") == RecoveryRoute.Proxy,
            "The catalog did not reconcile the network closure.");
    }

    internal static async Task OutboxAndGuestReadings()
    {
        using var temp = new TemporaryDirectory();
        var host = new FakeHypervisor();
        var lab = new LabService(temp.Root, host, new FakeInbox());
        await lab.ImportTemplateAsync("Offline Linux", GuestFamily.Linux, false, temp.File("base.vhdx", "fixture disk"));
        var template = (await lab.ObserveAsync()).Templates.Single();
        var from = await lab.PrepareCleanSessionAsync(template.Id, new("Source", 2, 1), []);
        var to = await lab.PrepareCleanSessionAsync(template.Id, new("Receiver", 2, 1), [], disposable: false);
        await lab.StartAsync(from);
        await lab.MountLinuxIsolationProbeAsync(from);
        var evidence = await lab.ReadLinuxIsolationAsync(from, "VMCRATE_ISOLATION1 1 0 0 1");
        Expect(evidence is { GuestReported: true, Guest.DefaultRoutes: 0, Guest.DnsServers: 1 }, "Guest counters changed.");
        await ErrorAsync("error.guestProbe", () => lab.ReadLinuxIsolationAsync(from, "VMCRATE_ISOLATION1 1 0 0 1 extra"));

        await lab.PrepareOutboxAsync(from, to, 64);
        await lab.PrepareOutboxAsync(from, to, 64);
        var transfer = lab.ReadOutboxes().Single();
        Expect(host.Machines[from].Location.OutboxDisk is not null && host.Machines[to].Location.OutboxDisk is null,
            "The outbox was attached to multiple guests.");
        await ErrorAsync("error.outbox", () => lab.SetNetworkAsync(to, NetworkMode.Private));
        await ErrorAsync("error.outboxClaims", () => lab.ReceiveOutboxAsync(transfer.Id, declaredFiles: [new("file", -1, new string('1', 64))]));
        var declared = new FileEvidence("sample.txt", 10, new string('1', 64));
        var received = await lab.ReceiveOutboxAsync(transfer.Id, declaredFiles: [declared]);
        Expect(received is { Status: OutboxStatus.Received, Evidence.Sha256.Length: 64 } &&
            received.DeclaredFiles?.Single() == declared && host.Machines[from].Location.OutboxDisk is null &&
            host.Machines[to].Location.OutboxDisk is not null, "Outbox provenance or attachment order was lost.");
        await lab.ArchiveOutboxAsync(transfer.Id);
        Expect(host.Machines[to].Location.OutboxDisk is null && lab.ReadOutboxes().Single().Status == OutboxStatus.Archived,
            "Outbox archiving left a guest attachment.");
    }

    internal static async Task SchemaRollback()
    {
        using var temp = new TemporaryDirectory();
        var store = new StateStore(temp.Root);
        var before = new LabState(Guid.NewGuid(), 8, [], []);
        store.Save(before);
        var statePath = Path.Combine(temp.Root, "state.json");
        var original = File.ReadAllBytes(statePath);
        var ticket = Guid.NewGuid();
        Directory.CreateDirectory(WorkspaceSchemaUpdate.DirectoryFor(temp.Root, ticket));
        WorkspaceSchemaUpdate.Prepare(temp.Root, ticket);
        using (var current = JsonDocument.Parse(File.ReadAllBytes(statePath)))
            Expect(current.RootElement.GetProperty("version").GetInt32() == LabState.CurrentVersion, "Schema was not migrated.");
        WorkspaceSchemaUpdate.Rollback(temp.Root, ticket);
        Expect(original.SequenceEqual(File.ReadAllBytes(statePath)), "Rollback did not restore the previous catalog bytes.");
        WorkspaceSchemaUpdate.Prepare(temp.Root, ticket);
        store.Save(store.Load() with { Templates = [new(Guid.NewGuid(), "New content", GuestFamily.Linux, false)] });
        await ErrorAsync("error.appUpdateChanged", () =>
        {
            WorkspaceSchemaUpdate.Rollback(temp.Root, ticket);
            return Task.CompletedTask;
        });
        Expect(store.Load().Templates.Single().Name == "New content", "Rollback replaced changes made since migration.");
    }
}
