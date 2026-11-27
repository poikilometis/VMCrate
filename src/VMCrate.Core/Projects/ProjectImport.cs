using System.IO.Compression;
using System.Security.Cryptography;

namespace VMCrate.Core;

public sealed partial class LabService
{
    public ProjectPreview PreviewProject(string path)
    {
        using var workspaceLock = store.Lock();
        using var archive = OpenProject(path);
        return MatchProject(store.Load(), ProjectManifest.Read(archive));
    }

    public async Task<LabBlueprint> ImportProjectAsync(string path, ProjectPreview preview, CancellationToken cancellation = default)
    {
        using var workspaceLock = store.Lock();
        using var archive = OpenProject(path);
        var state = store.Load();
        var manifest = ProjectManifest.Read(archive);
        var current = MatchProject(state, manifest);
        if (!current.Ready || current.Fingerprint != preview.Fingerprint || !current.Disks.SequenceEqual(preview.Disks))
            throw new LabException("error.projectChanged");

        if (new DriveInfo(Path.GetPathRoot(store.Root)!).AvailableFreeSpace < current.ImportBytes)
            throw new LabException("error.projectSpace");

        var reserved = state.Templates.Select(item => item.Name).Concat(state.Builds.Select(item => item.Options.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var created = new List<Template>();
        try
        {
            foreach (var item in current.Disks)
            {
                cancellation.ThrowIfCancellationRequested();
                var local = await ResolveProjectDiskAsync(state, archive, item, reserved, created, cancellation);
                mapping.Add(item.Disk.Name, local);
            }

            var blueprint = manifest.Blueprint with
            {
                Machines = manifest.Blueprint.Machines.Select(vm => vm with { Template = mapping[vm.Template] }).ToArray()
            };
            state.Templates.AddRange(created);
            BuildBlueprintEntries(state, blueprint, checkCollisions: false);
            if (created.Count > 0)
                store.Save(state);
            return blueprint;
        }
        catch
        {
            foreach (var template in created)
                LocalPaths.RemoveDirectory(store.Templates, template.Id);
            throw;
        }
    }

    private async Task<string> ResolveProjectDiskAsync(LabState state, ZipArchive archive, ProjectDiskPreview item,
        HashSet<string> reserved, List<Template> created, CancellationToken cancellation)
    {
        var disk = item.Disk;
        if (item.LocalName is { } local)
        {
            var template = state.Templates.Single(row => row.Name == local);
            var actual = await FileDigests.InspectAsync(store.TemplateDisk(template.Id), cancellation);
            if (!actual.Sha256.Equals(disk.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new LabException("error.projectIntegrity", local);
            return local;
        }

        var name = AvailableTemplateName(disk.Name, reserved);
        var imported = new Template(Guid.NewGuid(), name, disk.Guest, disk.SecureBoot, disk.Sha256.ToLowerInvariant(),
            disk.Revision, DateTimeOffset.UtcNow);
        var target = store.TemplateDisk(imported.Id);
        LocalPaths.MakeDirectory(Path.GetDirectoryName(target)!);
        created.Add(imported);
        await using (var payload = archive.GetEntry(ProjectManifest.DiskEntry(disk))!.Open())
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var hash = await CopyProjectDiskAsync(payload, output, disk.Bytes, cancellation);
            if (!hash.Equals(disk.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new LabException("error.projectIntegrity", name);
            output.Flush(flushToDisk: true);
        }
        await hypervisor.ValidateBaseDiskAsync(target);
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        return name;
    }

    private ProjectPreview MatchProject(LabState state, ProjectManifest manifest)
    {
        return new(manifest.Blueprint, manifest.Disks.Select(disk =>
        {
            var local = state.Templates.Where(item => item.Published && item.Guest == disk.Guest && item.SecureBoot == disk.SecureBoot &&
                item.Revision == disk.Revision && string.Equals(item.Sha256, disk.Sha256, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(store.TemplateDisk(item.Id)))
                .OrderByDescending(item => string.Equals(item.Name, disk.Name, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            return new ProjectDiskPreview(disk, local?.Name);
        }).ToArray(), manifest.Fingerprint());
    }

    private static ZipArchive OpenProject(string path)
    {
        var stream = File.OpenRead(LocalPaths.RequireFile(path, ".zip"));
        try { return new ZipArchive(stream, ZipArchiveMode.Read); }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string AvailableTemplateName(string requested, HashSet<string> reserved)
    {
        var name = requested;
        for (var n = 2; !reserved.Add(name); n++)
        {
            var suffix = $" ({n})";
            name = requested[..Math.Min(requested.Length, 64 - suffix.Length)] + suffix;
        }
        return name;
    }

    private static async Task<string> CopyProjectDiskAsync(Stream input, Stream output, long expected,
        CancellationToken cancellation)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        long bytes = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            bytes += read;
            if (bytes > expected)
                throw new LabException("error.projectIntegrity");
            digest.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
        }
        if (bytes != expected)
            throw new LabException("error.projectIntegrity");

        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }
}
