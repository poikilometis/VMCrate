using System.IO;
using System.IO.Compression;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class PortableProjectChecks
{
    internal static async Task TransferAndRejectTampering()
    {
        using var temp = new TemporaryDirectory();
        var source = new LabService(Path.Combine(temp.Root, "source"), new FakeHypervisor(), new FakeInbox());
        var dest = new LabService(Path.Combine(temp.Root, "dest"), new FakeHypervisor(), new FakeInbox());
        var clean = new LabService(Path.Combine(temp.Root, "clean"), new FakeHypervisor(), new FakeInbox());
        var disk = temp.File("disk.vhdx", "pristine bits");
        await source.ImportTemplateAsync("Base", GuestFamily.Linux, true, disk);
        await dest.ImportTemplateAsync("Base", GuestFamily.Linux, true, temp.File("other.vhdx", "a different image"));
        var plan = new LabBlueprint(1, "portable", [new("VM", "Base", 4, 2, "peers")]);
        var manifest = Path.Combine(temp.Root, "manifest.zip");
        await source.ExportProjectAsync(plan, manifest, false);
        Expect(!dest.PreviewProject(manifest).Ready, "Missing required disk was marked available.");

        var archive = Path.Combine(temp.Root, "portable.zip");
        await source.ExportProjectAsync(plan, archive, true);
        var preview = dest.PreviewProject(archive);
        Expect(preview.Ready && preview.ImportBytes > 0, "Embedded VHDX was not offered for import.");
        var imported = await dest.ImportProjectAsync(archive, preview);
        Expect(imported.Machines.Single().Template == "Base (2)" && imported.Machines.Single().Segment == "peers",
            "Template collision or network topology was not preserved.");
        Expect(dest.PreviewProject(archive).ImportBytes == 0, "A verified template was not reused.");
        var pending = clean.PreviewProject(archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.Single(item => item.FullName.EndsWith(".vhdx"));
            using var data = entry.Open();
            data.WriteByte((byte)'X');
        }
        await ErrorAsync("error.projectIntegrity", () => clean.ImportProjectAsync(archive, pending));
        Expect((await clean.ObserveAsync()).Templates.Count == 0, "A corrupt VHDX left a catalog entry.");
    }

    internal static async Task Revisions()
    {
        using var temp = new TemporaryDirectory();
        var host = new FakeHypervisor { BootVerified = true };
        var lab = new LabService(temp.Root, host, new FakeInbox());
        await lab.ImportTemplateAsync("Base", GuestFamily.Linux, false, temp.File("base.vhdx", "unchanged"));
        var original = (await lab.ObserveAsync()).Templates.Single();
        await lab.CreateCloneAsync(original.Id, new("Existing clone", 4, 2));
        var iso = temp.File("new.iso", "official fixture");
        var hash = await lab.CalculateLocalIsoDigestAsync(iso);
        var job = lab.QueueBuild(new("Base updated", "r2", new(new("build", 4, 2), GuestFamily.Linux, false, iso, 32),
            hash, null, original.Id));
        await lab.ContinueBuildAsync(job.Id);
        await lab.ContinueBuildAsync(job.Id);
        await lab.StartAsync(job.Id);
        await lab.EjectInstallerAsync(job.Id);
        await lab.ContinueBuildAsync(job.Id);
        await lab.PowerOffAsync(job.Id);
        await lab.ContinueBuildAsync(job.Id);

        var group = lab.ReadRevisions(original.Id);
        Expect(group.Count == 2 && group.Single(row => row.Template.Id == original.Id).Dependents.SequenceEqual(["Existing clone"]),
            "The prior clone lost its revision reference.");
        var first = await lab.VerifyRevisionAsync(job.Id);
        Expect(lab.ReadRevisions(job.Id).Single(row => row.Template.Id == job.Id).Template.BootVerifiedUtc == first.VerifiedUtc,
            "The verified boot was not recorded.");
        Expect((await lab.ObserveAsync()).Machines.All(machine => !machine.Disposable), "A temporary verification VM remains.");
        Expect((await lab.VerifyTemplateAsync(original.Id)).Matches == true, "The prior base was changed.");
    }
}
