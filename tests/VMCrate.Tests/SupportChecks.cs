using System.IO;
using System.IO.Compression;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class SupportChecks
{
    internal static async Task PrivacyAndArchive()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        await lab.CreateInstallerAsync(new(new("Private VM label", 4, 2), GuestFamily.Windows, true,
            temp.File("private-name.iso", "test media"), 64));
        var id = (await lab.ObserveAsync()).Machines.Single().Id;
        await lab.SetUpdateProxyAsync(id, UpdateProxy.Create(Guid.NewGuid(), "203.0.113.33", 8443));
        var store = new StateStore(temp.Root);
        var state = store.Load();
        state.Operations.Add(new(Guid.NewGuid(), id, DateTimeOffset.UtcNow, RecordedOperation.ProxyHost, "secret value"));
        store.Save(state);
        var readiness = new HostReadiness(HyperVReadiness.Ready, true, false, "private backend detail",
            new(26200, "fixture edition", true, false, 8, 8L * 1024 * 1024 * 1024));
        var package = await lab.BuildSupportPackageAsync(readiness, id);
        Expect(package is { CatalogReadable: true, Host.WindowsBuild: 26200, Machine.Profile: NetworkMode.Updates } &&
            package.Operations.Single().Code is null, "Support did not contain the expected typed observations.");
        var preview = package.Preview();
        foreach (var secret in new[] { temp.Root, "Private VM label", "private-name.iso", "203.0.113.33", "fixture tunnel",
            "secret value", "private backend detail", id.ToString() })
            Expect(!preview.Contains(secret, StringComparison.OrdinalIgnoreCase), "Support preview includes local data.");
        var path = Path.Combine(temp.Root, "support.zip");
        package.Export(path);
        using var zip = ZipFile.OpenRead(path);
        Expect(zip.Entries.Single().FullName == "support.json", "The archive should contain only the displayed file.");
        using var input = new StreamReader(zip.Entries[0].Open());
        Expect(await input.ReadToEndAsync() == preview, "The exported content differs from the preview.");

        File.WriteAllText(Path.Combine(temp.Root, "state.json"), "{damaged");
        var partial = await lab.BuildSupportPackageAsync(readiness, id);
        Expect(!partial.CatalogReadable && partial.Host.WindowsBuild == 26200 && partial.Machine is null,
            "A corrupt workspace hid the host diagnostics.");
    }

    internal static async Task MissingCloneRecovery()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        await lab.ImportTemplateAsync("Base", GuestFamily.Linux, false, temp.File("base.vhdx", "source"));
        var template = (await lab.ObserveAsync()).Templates.Single();
        await lab.CreateCloneAsync(template.Id, new("Lost clone", 4, 2));
        var id = (await lab.ObserveAsync()).Machines.Single().Id;
        var previous = backend.Machines[id].Disk;
        await ErrorAsync("error.recoveryChanged", () => lab.RecreateMissingCloneAsync(id));
        backend.Machines.Remove(id);

        Expect((await lab.InspectRecoveryAsync()).Any(item => item.Id == id && item.Kind == RecoveryKind.RecreateMissingClone),
            "Recovery missed a clone with an intact template.");
        backend.FailAfterCreate = true;
        await ErrorAsync("error.backend", () => lab.RecreateMissingCloneAsync(id));
        var replacement = backend.Machines[id].Disk;
        await new LabService(temp.Root, backend, new FakeInbox()).ResumeProvisionAsync(id);
        Expect(backend.Machines[id].Disk == replacement && replacement != previous && File.Exists(previous),
            "Retry created another disk or removed the prior working copy.");
    }
}
