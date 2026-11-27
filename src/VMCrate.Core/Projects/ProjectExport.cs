using System.IO.Compression;
using System.Text.Json;

namespace VMCrate.Core;

public sealed partial class LabService
{
    public LabBlueprint ReadDeployedBlueprint(string blueprintId)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var members = GetLabMembers(state, blueprintId);
        var segments = members.Select(vm => vm.PrivateSegment).OfType<Guid>().Distinct()
            .Select((id, index) => (id, Name: $"private-{index + 1}")).ToDictionary(item => item.id, item => item.Name);

        var machines = members.Select(vm => new BlueprintMachine(vm.Name, FindTemplate(state, vm.TemplateId!.Value).Name,
            vm.MemoryGiB, vm.CpuCount, vm.PrivateSegment is Guid segment ? segments[segment] : null));
        return new(1, blueprintId, machines.ToArray());
    }

    public async Task ExportProjectAsync(LabBlueprint blueprint, string path, bool includeDisks,
        CancellationToken cancellation = default)
    {
        using var workspaceLock = store.Lock();
        var entries = BuildBlueprintEntries(store.Load(), blueprint, checkCollisions: false);
        var templates = new List<(ProjectDisk Disk, string Path)>();
        foreach (var template in entries.Select(entry => entry.Template).DistinctBy(template => template.Id))
        {
            var source = store.TemplateDisk(template.Id);
            var actual = await FileDigests.InspectAsync(source, cancellation);
            if (template.Sha256 is not null && !actual.Sha256.Equals(template.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new LabException("error.projectIntegrity", template.Name);
            templates.Add((new(template.Name, template.Guest, template.SecureBoot, template.Revision,
                actual.Sha256, actual.SizeBytes, includeDisks), source));
        }

        var manifest = new ProjectManifest(1, blueprint, templates.Select(item => item.Disk).ToArray());
        await OutputFile.WriteNewAsync(path, ".zip", async output =>
        {
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            await using (var metadata = archive.CreateEntry(ProjectManifest.Entry).Open())
                await JsonSerializer.SerializeAsync(metadata, manifest, JsonDefaults.Options, cancellation);

            foreach (var (disk, source) in templates.Where(item => item.Disk.Included).DistinctBy(item => item.Disk.Sha256))
            {
                await using var input = File.OpenRead(source);
                await using var payload = archive.CreateEntry(ProjectManifest.DiskEntry(disk), CompressionLevel.NoCompression).Open();
                var hash = await CopyProjectDiskAsync(input, payload, disk.Bytes, cancellation);
                if (hash != disk.Sha256)
                    throw new LabException("error.projectIntegrity", disk.Name);
            }
        });
    }

    internal static bool ValidProjectName(string? name)
    {
        if (name is null)
            return false;
        try { return ValidateName(name, []) == name; }
        catch (LabException) { return false; }
    }
}
