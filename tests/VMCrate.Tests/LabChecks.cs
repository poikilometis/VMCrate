using System.IO;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class LabChecks
{
    private static readonly MachineOptions Resources = new("Windows test", 4, 2);

    internal static async Task TemplateLifecycle()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = Lab(temp, backend);
        var source = temp.File("original.vhdx", "pristine image");
        await lab.ImportTemplateAsync("Example", GuestFamily.Windows, true, source);
        var template = (await lab.ObserveAsync()).Templates.Single();
        await lab.CreateCloneAsync(template.Id, Resources);

        var vm = (await lab.ObserveAsync()).Machines.Single();
        Expect(vm.TemplateId == template.Id && vm.Network == NetworkMode.Offline, "Expected an offline clone.");
        var baseDisk = backend.Machines[vm.Id].Location.BaseDisk!;
        Expect(File.GetAttributes(baseDisk).HasFlag(FileAttributes.ReadOnly), "Base disk must be read-only.");
        Expect(File.ReadAllText(baseDisk) == File.ReadAllText(source), "Import changed the source content.");
        await ErrorAsync("error.inUse", () => lab.DeleteTemplateAsync(template.Id));

        await lab.StartAsync(vm.Id);
        Expect((await lab.ObserveAsync()).StateOf(vm.Id) == PowerState.Running, "VM did not start.");
        await lab.PowerOffAsync(vm.Id);
        await lab.SetNetworkAsync(vm.Id, NetworkMode.Private);
        Expect((await lab.ObserveAsync()).Machines.Single().Network == NetworkMode.Private, "Network change was not stored.");

        var oldDisk = backend.Machines[vm.Id].Disk;
        await lab.ResetDiskAsync(vm.Id);
        Expect(!File.Exists(oldDisk) && File.Exists(backend.Machines[vm.Id].Disk), "Disk was not replaced.");
        Expect(File.ReadAllText(baseDisk) == "pristine image", "Reset changed the parent disk.");

        await lab.DeleteMachineAsync(vm.Id);
        await lab.DeleteTemplateAsync(template.Id);
        Expect(!File.Exists(baseDisk) && File.Exists(source), "Template cleanup touched the wrong file.");
    }

    internal static async Task InstallerCapture()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = Lab(temp, backend);
        var iso = temp.File("installer.iso", "test media");
        await lab.CreateInstallerAsync(new InstallerOptions(Resources, GuestFamily.Linux, false, iso, 32));
        var vm = (await lab.ObserveAsync()).Machines.Single();
        Expect(backend.Machines[vm.Id].Installer == iso, "Installer link is missing.");
        await lab.EjectInstallerAsync(vm.Id);
        Expect(backend.Machines[vm.Id].Installer is null, "The installer was not ejected.");
        await lab.CaptureTemplateAsync(vm.Id, "Installed Linux");
        var template = (await lab.ObserveAsync()).Templates.Single();
        Expect(template.Guest == GuestFamily.Linux && !template.SecureBoot, "Capture discarded firmware settings.");
        await lab.DeleteMachineAsync(vm.Id);
        Expect(File.Exists(iso), "VM removal deleted the installer source.");
    }

    internal static async Task FailedCreation()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor { FailCreate = true };
        var lab = Lab(temp, backend);
        var iso = temp.File("installer.iso", "test media");
        await ErrorAsync("error.backend", () => lab.CreateInstallerAsync(new InstallerOptions(Resources, GuestFamily.Windows, true, iso, 64)));
        var state = await lab.ObserveAsync();
        var failed = state.Machines.Single();
        Expect(state.StateOf(failed.Id) == PowerState.Missing, "Failed creation was not recoverable.");
        await lab.DeleteMachineAsync(failed.Id);
        Expect((await lab.ObserveAsync()).Machines.Count == 0, "Removing incomplete creation failed.");
    }

    internal static async Task FailedReset()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor { FailReset = true };
        var lab = Lab(temp, backend);
        var template = await Import(lab, temp);
        await lab.CreateCloneAsync(template.Id, Resources);
        var vm = (await lab.ObserveAsync()).Machines.Single();
        var previous = backend.Machines[vm.Id].Disk;
        await ErrorAsync("error.backend", () => lab.ResetDiskAsync(vm.Id));
        Expect(File.Exists(previous), "A failed reset destroyed the attached disk.");
        backend.FailReset = false;
        await lab.ResetDiskAsync(vm.Id);
        Expect(!File.Exists(previous), "Retry did not replace the disk.");
    }

    internal static async Task ConcurrentWrites()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor
        {
            CreateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            FinishCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var lab = Lab(temp, backend);
        var iso = temp.File("installer.iso", "test media");
        var pending = lab.CreateInstallerAsync(new InstallerOptions(Resources, GuestFamily.Linux, true, iso, 64));
        try
        {
            await backend.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ErrorAsync("error.busy", Lab(temp, backend).ObserveAsync);
        }
        finally
        {
            backend.FinishCreate.SetResult();
            await pending;
        }
        Expect((await lab.ObserveAsync()).Machines.Count == 1, "The catalog lost its pending write.");
    }

    internal static async Task InvalidInputs()
    {
        using var temp = new TemporaryDirectory();
        var lab = Lab(temp, new FakeHypervisor());
        var iso = temp.File("not-a-disk.iso", "fixture");
        await ErrorAsync("error.file", () => lab.ImportTemplateAsync("Bad", GuestFamily.Windows, true, iso));
        await ErrorAsync("error.name", () => lab.CreateInstallerAsync(new InstallerOptions(Resources with { Name = "\r" }, GuestFamily.Windows, true, iso, 64)));
        await ErrorAsync("error.diskSize", () => lab.CreateInstallerAsync(new InstallerOptions(Resources, GuestFamily.Windows, true, iso, 1)));
        await ErrorAsync("error.resources", () => lab.CreateInstallerAsync(new InstallerOptions(Resources with { CpuCount = 0 }, GuestFamily.Windows, true, iso, 64)));
        Expect((await lab.ObserveAsync()).Machines.Count == 0, "Invalid input added a VM.");
    }

    internal static async Task CorruptedState()
    {
        using var temp = new TemporaryDirectory();
        var lab = Lab(temp, new FakeHypervisor());
        var file = temp.File(Path.Combine("workspace", "state.json"), "{ broken content");
        await ErrorAsync("error.state", lab.ObserveAsync);
        Expect(File.ReadAllText(file) == "{ broken content", "The invalid catalog was modified.");
    }

    internal static async Task Inbox()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = Lab(temp, backend);
        var template = await Import(lab, temp);
        await lab.CreateCloneAsync(template.Id, Resources);
        var vm = (await lab.ObserveAsync()).Machines.Single();
        var source = temp.File(Path.Combine("one", "report.txt"), "sample");
        var duplicate = temp.File(Path.Combine("two", "REPORT.txt"), "different");
        await ErrorAsync("error.duplicateFiles", () => lab.SendFilesAsync(vm.Id, [source, duplicate]));
        await lab.SendFilesAsync(vm.Id, [source]);
        var inbox = backend.Machines[vm.Id].Inbox!;
        Expect(File.Exists(inbox) && File.ReadAllText(source) == "sample", "Inbox modified the original file.");
        Expect(!Directory.EnumerateDirectories(Path.GetDirectoryName(inbox)!).Any(), "File staging was leaked.");
        await lab.EjectInboxAsync(vm.Id);
        Expect(backend.Machines[vm.Id].Inbox is null, "Eject did not detach the inbox.");
    }

    internal static async Task UnavailableHost()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = Lab(temp, backend);
        await Import(lab, temp);
        backend.FailObserve = true;
        var snapshot = await lab.ObserveAsync();
        Expect(snapshot.Host is null && snapshot.HostError?.Key == "error.host" && snapshot.Templates.Count == 1,
            "Host failure hid persisted templates or the diagnostic.");
    }

    private static LabService Lab(TemporaryDirectory temp, FakeHypervisor backend) =>
        new(Path.Combine(temp.Root, "workspace"), backend, new FakeInbox());

    private static async Task<Template> Import(LabService lab, TemporaryDirectory temp)
    {
        await lab.ImportTemplateAsync("Example", GuestFamily.Windows, true, temp.File("source.vhdx", "base"));
        return (await lab.ObserveAsync()).Templates.Single();
    }
}
