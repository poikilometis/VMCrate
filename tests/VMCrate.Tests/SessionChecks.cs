using System.IO;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class SessionChecks
{
    internal static async Task Lifecycle()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        var baseImage = temp.File("base.vhdx", "baseline");
        await lab.ImportTemplateAsync("Template", GuestFamily.Windows, true, baseImage);
        var template = (await lab.ObserveAsync()).Templates.Single();
        await lab.CreateCloneAsync(template.Id, new MachineOptions("Use once", 4, 2), disposable: true);
        await lab.CreateCloneAsync(template.Id, new MachineOptions("Keep", 4, 2));

        var ready = await lab.RefreshAsync();
        var disposable = ready.Machines.Single(vm => vm.Disposable);
        var keep = ready.Machines.Single(vm => !vm.Disposable);
        Expect(!disposable.SessionStarted && ready.Machines.Count == 2, "Prepared session was deleted before first start.");
        await ErrorAsync("error.sessionOffline", () => lab.SetNetworkAsync(disposable.Id, NetworkMode.Private));
        await ErrorAsync("error.sessionOnly", () => lab.EndDisposableAsync(keep.Id));
        await lab.StartAsync(disposable.Id);
        Expect((await lab.RefreshAsync()).Machines.Any(vm => vm.Id == disposable.Id && vm.SessionStarted),
            "A running disposable was removed.");
        await ErrorAsync("error.sessionEnded", () => lab.StartAsync(disposable.Id));

        var disk = backend.Machines[disposable.Id].Disk;
        var parent = backend.Machines[disposable.Id].Location.BaseDisk!;
        await lab.PowerOffAsync(disposable.Id);
        Expect((await lab.ObserveAsync()).Machines.Count == 2, "Read-only observation collected a session.");
        var cleaned = await lab.RefreshAsync();
        Expect(cleaned.Machines.Single().Id == keep.Id && !File.Exists(disk) && File.Exists(parent) && File.Exists(baseImage),
            "Cleanup touched the template or retained a stopped session's disk.");
        Expect(cleaned.Host!.Machines.All(vm => vm.Id != disposable.Id), "A removed VM remains in the host snapshot.");

        await lab.CreateCloneAsync(template.Id, new MachineOptions("End manually", 4, 2), disposable: true);
        var manual = (await lab.ObserveAsync()).Machines.Single(vm => vm.Disposable);
        await lab.StartAsync(manual.Id);
        await lab.EndDisposableAsync(manual.Id);
        Expect((await lab.ObserveAsync()).Machines.Single().Id == keep.Id, "Ending a session did not remove the VM.");
    }

    internal static async Task Recovery()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor { FailStart = true };
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        await lab.ImportTemplateAsync("Template", GuestFamily.Linux, true, temp.File("base.vhdx", "baseline"));
        await lab.CreateCloneAsync((await lab.ObserveAsync()).Templates.Single().Id,
            new MachineOptions("Retry session", 4, 2), disposable: true);

        var id = (await lab.ObserveAsync()).Machines.Single().Id;
        await ErrorAsync("error.backend", () => lab.StartAsync(id));
        Expect(!(await lab.RefreshAsync()).Machines.Single().SessionStarted, "A failed boot armed a disposable.");

        backend.FailStart = false;
        await lab.StartAsync(id);
        await lab.PowerOffAsync(id);
        backend.FailObserve = true;
        Expect((await lab.RefreshAsync()).Machines.Count == 1, "Cleanup ran while the host was unobservable.");
        backend.FailObserve = false;
        backend.FailDelete = true;
        var failed = await lab.RefreshAsync();
        Expect(failed.CleanupError?.Key == "error.sessionCleanup" && failed.Machines.Count == 1,
            "Cleanup failure discarded the VM's record.");
        backend.FailDelete = false;
        Expect((await lab.RefreshAsync()).Machines.Count == 0, "Cleanup could not resume.");
    }
}
