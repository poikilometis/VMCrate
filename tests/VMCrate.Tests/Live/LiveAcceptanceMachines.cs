using System.IO;
using System.Text.Json;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static partial class LiveAcceptance
{
    private static async Task CloneLifecycleAsync(LiveWorkspace workspace, MediaFixture windows, MediaFixture linux,
        WindowsGuestCredentials? credentials)
    {
        var win = await workspace.ImportAsync("Acceptance Windows", GuestFamily.Windows, true, windows.Vhdx);
        var kali = await workspace.ImportAsync("Acceptance Linux", GuestFamily.Linux, false, linux.Vhdx);
        var imported = workspace.Store.Load().Templates.ToDictionary(template => template.Id);

        var source = Path.Combine(workspace.Root, "probe.txt");
        await File.WriteAllTextAsync(source, "VMCrate live acceptance fixture");
        await BootCloneAsync(workspace, win, "Windows offline", source, credentials);
        await BootCloneAsync(workspace, kali, "Linux offline", source);
        await CheckOutboxAsync(workspace, kali);

        await workspace.Lab.CreateCloneAsync(kali, new("Disposable acceptance VM", 4, 2), disposable: true);
        var session = (await workspace.Lab.ObserveAsync()).Machines.Single(vm => vm.Disposable).Id;
        await workspace.Lab.StartAsync(session);
        await workspace.VerifyBootAsync(session);
        await workspace.Lab.PowerOffAsync(session);
        var refreshed = await workspace.Lab.RefreshAsync();
        Expect(refreshed.CleanupError is null && refreshed.Machines.All(vm => vm.Id != session),
            "The stopped disposable VM was not reclaimed.");
        Console.WriteLine("PASS disposable VM reclaimed");

        await CheckBlueprintAsync(workspace);
        foreach (var (id, disk) in new[] { (win, windows.Vhdx), (kali, linux.Vhdx) })
        {
            var current = await workspace.Lab.VerifyTemplateAsync(id);
            var original = await FileDigests.InspectAsync(disk);
            Expect(current.Matches == true && current.Sha256 == imported[id].Sha256 && original.Sha256 == current.Sha256,
                "A reset or cleanup changed a template disk.");
        }
        Console.WriteLine("PASS original and imported template SHA-256 digests unchanged");
    }

    private static async Task BootCloneAsync(LiveWorkspace workspace, Guid template, string name, string source,
        WindowsGuestCredentials? credentials = null)
    {
        await workspace.Lab.CreateCloneAsync(template, new(name, 4, 2));
        var vm = (await workspace.Lab.ObserveAsync()).Machines.Single(vm => vm.Name == name);
        var before = await workspace.ObserveAsync(vm.Id);
        Expect(before.Findings.Length == 0 && before.State == PowerState.Off && before.AttachedDisk is not null,
            $"{name} did not satisfy the Offline audit.");
        await workspace.Lab.StartAsync(vm.Id);
        await workspace.VerifyBootAsync(vm.Id);
        var isolated = await workspace.Lab.ObserveIsolationAsync(vm.Id);
        Expect(isolated is
        {
            Profile: NetworkMode.Offline,
            Host: { State: PowerState.Running, NetworkPolicyMatches: true, FileCopyEnabled: false,
                KeyValueExchangeEnabled: false, Switches.Length: 0 }
        }, "The runtime Hyper-V observation differs from the Offline profile.");

        await workspace.Lab.SendFilesAsync(vm.Id, [source]);
        var path = workspace.Store.Location(workspace.Store.Load(), vm);
        var inbox = Directory.GetFiles(path.InboxDirectory, "*.iso").Single();
        Expect(await workspace.Host.UsesMediaAsync(inbox) &&
            (await workspace.Lab.ObserveIsolationAsync(vm.Id)).Host is { MediaLayoutMatches: true, InboxAttached: true },
            "The read-only inbox DVD is missing.");
        if (credentials is not null)
        {
            var guest = await workspace.Lab.CheckWindowsIsolationAsync(vm.Id, credentials.User, credentials.Password);
            Expect(guest.Guest is { IPv4DefaultRoutes: 0, IPv6DefaultRoutes: 0, InboxReadable: true },
                "The Windows guest did not observe the read-only inbox and the expected Offline routes.");
            var inventory = await workspace.Lab.ReadWindowsInventoryAsync(vm.Id, credentials.User, credentials.Password);
            Expect(inventory.Ecosystem == "WindowsCBS" && inventory.Packages.Any(package => package.Name == "WindowsBuild"),
                "The Windows guest did not report its build through PowerShell Direct.");
            Console.WriteLine("PASS Windows guest inbox, Offline routes and CBS inventory");
        }
        var export = Path.Combine(workspace.Root, $"{vm.Id:N}.json");
        await workspace.Lab.ExportReportAsync(export, vm.Id, [source]);
        await using (var input = File.OpenRead(export))
        {
            var evidence = await JsonSerializer.DeserializeAsync<AnalysisReport>(input, JsonDefaults.Options);
            Expect(evidence?.Files.Single().Sha256 == (await FileDigests.InspectAsync(source)).Sha256 &&
                evidence.Machine is { Network: NetworkMode.Offline, Power: PowerState.Running }, "The evidence export differs.");
        }

        await workspace.Lab.EjectInboxAsync(vm.Id);
        Expect(!await workspace.Host.UsesMediaAsync(inbox) &&
            (await workspace.Lab.ObserveIsolationAsync(vm.Id)).Host.InboxAttached == false, "Inbox DVD remained attached.");
        await workspace.Lab.PowerOffAsync(vm.Id);
        if (vm.Guest == GuestFamily.Linux)
            await CheckWorkingDiskTransferAsync(workspace, vm.Id);
        await workspace.Lab.ResetDiskAsync(vm.Id);
        var reset = await workspace.ObserveAsync(vm.Id);
        Expect(reset.Findings.Length == 0 && reset.AttachedDisk is not null && reset.AttachedDisk != before.AttachedDisk,
            "Reset did not attach a fresh differencing disk.");
        await workspace.Lab.DeleteMachineAsync(vm.Id);
        Console.WriteLine($"PASS {name} clone, heartbeat, inbox, report and differencing disk reset");
    }

    private static async Task CheckWorkingDiskTransferAsync(LiveWorkspace workspace, Guid original)
    {
        var package = Guid.NewGuid();
        var directory = Path.Combine(workspace.Root, package.ToString("N"));
        var name = "Imported Linux acceptance copy";
        try
        {
            await workspace.Lab.ExportMachineAsync(original, directory);
            var restored = await workspace.Lab.ImportMachineAsync(Path.Combine(directory, MachineTransfer.ManifestName), name);
            Expect(restored != original && (await workspace.ObserveAsync(restored)) is { State: PowerState.Off, Findings.Length: 0 },
                "Transfer did not register a new isolated VM.");
            await workspace.Lab.StartAsync(restored);
            await workspace.VerifyBootAsync(restored);
            await workspace.Lab.PowerOffAsync(restored);
            await workspace.Lab.DeleteMachineAsync(restored);
            Console.WriteLine("PASS offline Linux system disk transfer and boot");
        }
        finally { LocalPaths.RemoveDirectory(workspace.Root, package); }
    }

    private static async Task CheckBlueprintAsync(LiveWorkspace workspace)
    {
        var blueprint = new LabBlueprint(1, "acceptance",
        [
            new("Windows private peer", "Acceptance Windows", 4, 2, "peers"),
            new("Linux private peer", "Acceptance Linux", 4, 2, "peers")
        ]);
        await workspace.Lab.DeployBlueprintAsync(blueprint);
        var preview = await workspace.Lab.PreviewBlueprintAsync(blueprint);
        Expect(preview.Machines.Count == 2 && preview.Machines.All(row => row.AlreadyCreated), "Blueprint reuse failed.");
        await workspace.Lab.DeployBlueprintAsync(blueprint);
        var remove = await workspace.Lab.PreviewLabOperationAsync(blueprint.Id, LabOperation.Remove);
        Expect(remove.Members.Count == 2 && remove.PrivateSegments.Count == 1, "The private segment topology differs.");
        var inventory = await workspace.Host.ObserveRecoveryAsync(workspace.Store.Load().WorkspaceId);
        Expect(inventory.Segments.Single(segment => segment.Id == remove.PrivateSegments[0]).InUse,
            "The owned private switch does not have live Hyper-V attachments.");
        foreach (var member in remove.Members)
        {
            var observation = await workspace.Lab.ObserveIsolationAsync(member.Id);
            Expect(observation.Host is
                { NetworkPolicyMatches: true, PrivateMembershipMatches: true, Switches: [{ Type: "Private" }] },
                "Private segment membership differs from the current Hyper-V attachments.");
        }
        await CheckGroupRecoveryAsync(workspace, blueprint.Id);
        await CheckOfflineRestoreAsync(workspace, blueprint.Id);
        await workspace.Lab.ApplyLabOperationAsync(remove);
        Console.WriteLine("PASS private blueprint deployment, reuse and group removal");
    }

    private static async Task CheckOutboxAsync(LiveWorkspace workspace, Guid kali)
    {
        await workspace.Lab.CreateCloneAsync(kali, new("Outbox source", 2, 2));
        await workspace.Lab.CreateCloneAsync(kali, new("Offline receiver", 2, 2));
        var machines = (await workspace.Lab.ObserveAsync()).Machines;
        var source = machines.Single(vm => vm.Name == "Outbox source");
        var receiver = machines.Single(vm => vm.Name == "Offline receiver");
        await workspace.Lab.StartAsync(source.Id);
        await workspace.VerifyBootAsync(source.Id);
        await workspace.Lab.PrepareOutboxAsync(source.Id, receiver.Id, 64);
        var pending = workspace.Lab.ReadOutboxes().Single();
        workspace.Reopen();
        var received = await workspace.Lab.ReceiveOutboxAsync(pending.Id);
        Expect(received is { Status: OutboxStatus.Received, Evidence.SizeBytes: > 0 } &&
            received.Receiver == receiver.Id && (await workspace.ObserveAsync(source.Id)).Findings.Length == 0 &&
            (await workspace.ObserveAsync(receiver.Id)).Findings.Length == 0, "The offline outbox handoff lost its audit.");
        await workspace.Lab.ArchiveOutboxAsync(received.Id);
        await workspace.Lab.PowerOffAsync(source.Id);
        await workspace.Lab.DeleteMachineAsync(source.Id);
        await workspace.Lab.DeleteMachineAsync(receiver.Id);
        Console.WriteLine("PASS outbox disk hot-attach, detached SHA-256, offline receiver and archive");
    }

    private static async Task CheckOfflineRestoreAsync(LiveWorkspace workspace, string blueprintId)
    {
        var package = Guid.NewGuid();
        var directory = Path.Combine(workspace.Root, package.ToString("N"));
        var task = new LocalJobQueue(workspace.Root).Enqueue(new(LocalJobKind.Backup, Blueprint: blueprintId, Destination: directory));
        var worker = new LocalJobWorker(workspace.Lab, new OfficialImages(new VMCrate.Windows.PublisherSignatureVerifier()));
        await worker.RunAsync();
        Expect(new LocalJobQueue(workspace.Root).Read().Single(item => item.Id == task.Id).Status == LocalJobStatus.Succeeded,
            "The durable backup job did not publish its backup.");
        var manifest = Path.Combine(directory, LabBackup.ManifestName);
        var result = await workspace.Lab.TestLabBackupAsync(manifest);
        Expect(result.Booted == 2 && workspace.Lab.ReadRestoreDrill(manifest) == result,
            "The offline restore did not boot both test guests or record evidence.");
        Console.WriteLine($"PASS verified backup and separate Offline restore with two heartbeats · {result.ElapsedMs / 1000d:N1} s");
        LocalPaths.RemoveDirectory(workspace.Root, package);
    }

    private static async Task CheckGroupRecoveryAsync(LiveWorkspace workspace, string blueprintId)
    {
        var point = await workspace.Lab.CaptureRecoveryPointAsync(new(null, blueprintId), "Live acceptance baseline");
        try
        {
            Expect(point.Disks.Length == 2, "The recovery point must capture both private lab members.");
            var superseded = new Dictionary<Guid, string>();
            foreach (var disk in point.Disks)
            {
                await workspace.Lab.ResetDiskAsync(disk.MachineId);
                var reset = await workspace.ObserveAsync(disk.MachineId);
                superseded.Add(disk.MachineId, reset.AttachedDisk ?? throw new InvalidOperationException("Reset disk missing."));
            }

            workspace.Reopen();
            await workspace.Lab.RestoreRecoveryPointAsync(point.Id);
            foreach (var disk in point.Disks)
            {
                var restored = await workspace.ObserveAsync(disk.MachineId);
                Expect(restored is { State: PowerState.Off, Findings.Length: 0, AttachedDisk: not null } &&
                    restored.AttachedDisk != superseded[disk.MachineId] && File.Exists(superseded[disk.MachineId]),
                    "Recovery did not attach a valid replacement and retain the superseded disk.");
            }
            Console.WriteLine("PASS private lab recovery point, group restore and original disk retention");
        }
        finally
        {
            if (workspace.Store.Load().DiskRestore is null)
                workspace.Lab.DeleteRecoveryPoint(point.Id);
        }
    }
}
