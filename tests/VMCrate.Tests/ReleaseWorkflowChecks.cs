using System.IO;
using VMCrate.Core;
using VMCrate.Desktop;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class ReleaseWorkflowChecks
{
    internal static async Task FirstRunAndBackup()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Root, "workbench");
        var settings = new WorkspaceRegistration(Path.Combine(temp.Root, "profile"));
        Expect(settings.Read() is null, "A fresh profile unexpectedly has a workspace.");
        Expect(settings.Save(root) == root && settings.Read() == root, "The selected workspace was not remembered.");
        Expect(WorkspaceVolume.Inspect(root) is { FreeBytes: > 0, ExistingCatalog: false }, "Workspace volume check failed.");
        await ErrorAsync("error.workspaceFolder", Run(() => WorkspaceVolume.Prepare(Path.GetPathRoot(root)!)));
        Expect(!Directory.EnumerateFileSystemEntries(root).Any(), "The write check left a file in the new workspace.");

        var backend = new FakeHypervisor();
        var lab = new LabService(root, backend, new FakeInbox());
        var disk = temp.File("source.vhdx", "fixture");
        await lab.ImportTemplateAsync("Previous", GuestFamily.Linux, false, disk);
        await lab.ImportTemplateAsync("Newer", GuestFamily.Linux, false, disk);
        Expect(WorkspaceVolume.Inspect(root).ExistingCatalog, "The first-run dialog should detect this catalog.");

        var path = Path.Combine(root, "state.json");
        File.WriteAllText(path, "{damaged");
        var restore = (await lab.InspectRecoveryAsync()).Single();
        Expect(restore.Kind == RecoveryKind.RestoreCatalog, "A readable backup was missed.");
        await ErrorAsync("error.recoveryChanged", Run(() => lab.RestoreCatalogBackup(Guid.NewGuid())));
        lab.RestoreCatalogBackup(restore.Id);
        Expect((await lab.ObserveAsync()).Templates.Single().Name == "Previous", "The prior catalog was not restored.");
        var preserved = Directory.GetFiles(root, "state.failed-*.json").Single();
        Expect(File.ReadAllText(preserved) == "{damaged", "The damaged catalog was not preserved.");
        var untracked = await lab.InspectRecoveryAsync();
        Expect(untracked.Any(item => item.Kind == RecoveryKind.UntrackedDirectory), "Newer data disappeared from recovery inventory.");
        var current = File.ReadAllText(path);
        File.WriteAllText(path, current.Replace($"\"version\": {LabState.CurrentVersion}", "\"version\": 99", StringComparison.Ordinal));
        Expect((await lab.InspectRecoveryAsync()).Single().Kind == RecoveryKind.CatalogUnreadable,
            "The wizard offered to downgrade a future catalog.");
    }

    internal static async Task ProvisionAndIsolation()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor { FailCreate = true };
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        var iso = temp.File("local.iso", "test ISO");
        var options = new InstallerOptions(new("First", 4, 2), GuestFamily.Linux, false, iso, 32);
        await ErrorAsync("error.backend", () => lab.CreateInstallerAsync(options));
        var pending = (await lab.InspectRecoveryAsync()).Single(item => item.Kind == RecoveryKind.ResumeProvision);
        await ErrorAsync("error.recoveryRequired", () => lab.StartAsync(pending.Id));
        backend.FailCreate = false;
        await lab.ResumeProvisionAsync(pending.Id);
        Expect(backend.Machines[pending.Id].Installer == iso, "Retry lost the installer or used a different identity.");
        Expect((await lab.InspectRecoveryAsync()).Any(item => item.Kind == RecoveryKind.OtherWorkingDisk),
            "The interrupted disk should remain available for review.");

        backend.FailAfterCreate = true;
        await ErrorAsync("error.backend", () => lab.CreateInstallerAsync(options with { Machine = new("Second", 4, 2) }));
        var lostReply = (await lab.InspectRecoveryAsync()).Single(item => item.Kind == RecoveryKind.ResumeProvision);
        var attached = backend.Machines[lostReply.Id].Disk;
        await lab.ResumeProvisionAsync(lostReply.Id);
        Expect(backend.Machines[lostReply.Id].Disk == attached, "Recovery replaced a healthy registered disk.");

        var segment = new OwnedSegment(Guid.NewGuid(), false);
        var busy = new OwnedSegment(Guid.NewGuid(), true);
        backend.Segments = [segment, busy];
        Expect((await lab.InspectRecoveryAsync()).Single(item => item.Kind == RecoveryKind.EmptySegment).Id == segment.Id,
            "Recovery must show only unused owned switches for removal.");
        await ErrorAsync("error.recoveryChanged", () => lab.RemoveUnusedPrivateSegmentAsync(busy.Id));
        await lab.RemoveUnusedPrivateSegmentAsync(segment.Id);
        Expect(backend.RemovedSegments.SequenceEqual([segment.Id]), "Wrong switch removed.");

        await lab.SetUpdateProxyAsync(pending.Id, UpdateProxy.Create(Guid.NewGuid(), "203.0.113.42", 8080));
        backend.NetworkDrift = true;
        Expect((await lab.InspectRecoveryAsync()).Any(item => item.Kind == RecoveryKind.RestoreIsolation),
            "The drifted proxy policy was missed.");
        await lab.RestoreIsolationAsync(pending.Id);
        var restored = (await lab.ObserveAsync()).Machines.Single(vm => vm.Id == pending.Id);
        Expect(restored.Network == NetworkMode.Offline && restored.Proxy is null, "Recovery re-enabled an update route.");
    }

    internal static async Task ProxyTrace()
    {
        using var temp = new TemporaryDirectory();
        var lab = new LabService(temp.Root, new FakeHypervisor(), new FakeInbox());
        var options = new InstallerOptions(new("Builder", 4, 2), GuestFamily.Windows, true, temp.File("media.iso", "ISO"), 64);
        await lab.CreateInstallerAsync(options);
        var vm = (await lab.ObserveAsync()).Machines.Single();
        await ErrorAsync("error.proxyBuilder", () => lab.TraceHostProxyAsync(vm.Id));
        await lab.SetUpdateProxyAsync(vm.Id, UpdateProxy.Create(Guid.NewGuid(), "203.0.113.7", 8080));
        var host = await lab.TraceHostProxyAsync(vm.Id);
        Expect(host.ProxyTcp == ProbeCheck.Passed, "TCP result was lost.");
        var drift = host with { Network = host.Network! with { Interface = "different route", IsTunnel = false } };
        Expect(drift.RouteDiffersFrom(host) && !host.RouteDiffersFrom(host) &&
            !host.RouteDiffersFrom(host with { Network = null }), "Route comparison treated missing data as a route change.");
        var target = new ProxyTraceOptions(ProxyTarget.WindowsUpdate);
        await ErrorAsync("error.proxyBuilder", () => lab.TraceWindowsProxyAsync(vm.Id, target, "guest", "pwd", "", ""));
        await lab.StartAsync(vm.Id);
        var trace = await lab.TraceWindowsProxyAsync(vm.Id, target, "guest", "pwd", "", "");
        Expect(trace.Result == ProxyResult.GatewayUnresolved, "A failed gateway was obscured by CONNECT 502.");
        Expect(ProxyGuestTrace.Parse("VMCRATE_PROXY1 1 1 1 1 200 000 35").Result == ProxyResult.TlsOrTunnelFailure,
            "CONNECT 200 without HTTPS should fail.");
        Expect(ProxyGuestTrace.Parse("VMCRATE_PROXY1 1 1 1 0 407 000 56").Result == ProxyResult.ProxyAuthentication,
            "A proxy auth response was confused with a repository error.");
        Expect(ProxyGuestTrace.Parse("VMCRATE_PROXY1 1 1 1 0 200 503 0").Result == ProxyResult.RepositoryStatus,
            "A repository error was confused with a proxy error.");
        await ErrorAsync("error.proxyResult", Run(() => ProxyGuestTrace.Parse("VMCRATE_PROXY1 1 1 1 6 200 000 35")));
        await ErrorAsync("error.proxyBuilder", () => lab.AttachLinuxProxyProbeAsync(vm.Id, target));
    }
}
