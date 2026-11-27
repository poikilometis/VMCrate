using System.Text.Json;

namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task<LabBackup> BackupLabAsync(string blueprintId, string directory, CancellationToken cancellation = default,
        Guid? jobId = null, IProgress<FileProgress>? progress = null)
    {
        var destination = LocalPaths.FullPath(directory);
        LocalPaths.RequireOrdinaryPath(destination);
        var parent = Path.GetDirectoryName(destination);
        if (parent is null || !Directory.Exists(parent))
            throw new LabException("error.outputExists", destination);
        if (Path.Exists(destination))
        {
            var existing = Path.Combine(destination, LabBackup.ManifestName);
            if (jobId is Guid requested && File.Exists(existing) &&
                LabBackup.Read(LocalPaths.RequireFile(existing, ".json")) is { } committed &&
                committed.Id == requested && committed.BlueprintId == blueprintId)
                return committed;
            throw new LabException("error.outputExists", destination);
        }

        using var workspaceLock = store.Lock();
        var state = store.Load();
        var members = PointMachines(state, new(null, blueprintId));
        foreach (var vm in members)
        {
            RequireNoDiskRestore(state, vm.Id);
            if (state.Outboxes.Any(item => item.Uses(vm.Id)))
                throw new LabException("error.outbox", vm.Name);
        }
        var working = await ReadPointDisksAsync(state, members);
        var parents = members.Select(vm => vm.TemplateId!.Value).Distinct().Select(id => FindTemplate(state, id)).ToArray();
        var baseDisks = parents.ToDictionary(item => item.Id, item => LocalPaths.RequireFile(store.TemplateDisk(item.Id), ".vhdx"));
        var sources = working.Values.Concat(baseDisks.Values).ToArray();
        var id = jobId ?? Guid.NewGuid();
        var staging = PrepareBackupStage(parent, id, state.WorkspaceId);
        var held = new List<FileStream>(sources.Length);
        try
        {
            foreach (var source in sources)
                held.Add(File.OpenRead(source));
            var total = held.Sum(handle => handle.Length);
            var remaining = baseDisks.Sum(pair => ResumableFileCopy.RemainingBytes(pair.Value, LabBackup.TemplatePath(staging, pair.Key))) +
                working.Sum(pair => ResumableFileCopy.RemainingBytes(pair.Value, LabBackup.MachinePath(staging, pair.Key)));
            WorkspaceCapacity.RequireSpace(destination, remaining);
            long copied = 0;
            var aggregate = progress is null ? null : new BackupProgress(progress, total, () => copied);
            Directory.CreateDirectory(Path.Combine(staging, "templates"));
            Directory.CreateDirectory(Path.Combine(staging, "machines"));
            var templates = new List<LabBackupTemplate>(parents.Length);
            foreach (var template in parents)
            {
                RequirePublishedTemplate(template);
                var disk = await CopyBackupDiskAsync(baseDisks[template.Id], LabBackup.TemplatePath(staging, template.Id), aggregate, cancellation);
                if (template.Sha256 is { } expected && !disk.Sha256.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new LabException("error.projectIntegrity", template.Name);
                templates.Add(new(template.Id, template.Name, template.Guest, template.SecureBoot, template.Revision, disk));
                copied += disk.Bytes;
            }
            var machines = new List<LabBackupMachine>(members.Length);
            foreach (var vm in members)
            {
                var disk = await CopyBackupDiskAsync(working[vm.Id], LabBackup.MachinePath(staging, vm.Id), aggregate, cancellation);
                machines.Add(new(vm.Id, vm.Name, vm.TemplateId!.Value, vm.MemoryGiB, vm.CpuCount, vm.Network, vm.PrivateSegment, disk));
                copied += disk.Bytes;
            }
            if (!(await ReadPointDisksAsync(state, members)).OrderBy(item => item.Key)
                .SequenceEqual(working.OrderBy(item => item.Key)))
                throw new LabException("error.labChanged");

            var backup = new LabBackup(1, id, blueprintId, DateTimeOffset.UtcNow, templates.ToArray(), machines.ToArray());
            if (!backup.IsValid())
                throw new LabException("error.backupFormat");
            var draft = Path.Combine(staging, LabBackup.ManifestName);
            LocalPaths.RequireOrdinaryPath(draft);
            File.Delete(draft);
            await OutputFile.WriteNewAsync(draft, ".json", stream => JsonSerializer.SerializeAsync(stream, backup, JsonDefaults.Options, cancellation));
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return backup;
        }
        finally
        {
            foreach (var handle in held)
                handle.Dispose();
            if (jobId is null)
                LocalPaths.RemoveDirectory(parent, id);
        }
    }

    private sealed class BackupProgress(IProgress<FileProgress> output, long total, Func<long> copied) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => output.Report(new(copied() + value.ProcessedBytes, total));
    }

    private static string PrepareBackupStage(string parent, Guid id, Guid workspace)
    {
        var directory = Path.Combine(parent, id.ToString("N"));
        var marker = Path.Combine(directory, ".vmcrate-backup");
        LocalPaths.RequireOrdinaryPath(marker);
        var identity = $"{workspace:N}/{id:N}";
        if (Directory.Exists(directory))
        {
            if (!File.Exists(marker) || new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != identity)
                throw new LabException("error.outputExists", directory);
        }
        else
        {
            LocalPaths.MakeDirectory(directory);
            File.WriteAllText(marker, identity);
        }
        return directory;
    }

    private static async Task<LabBackupDisk> CopyBackupDiskAsync(string source, string target,
        IProgress<FileProgress>? progress, CancellationToken token)
    {
        var digest = await ResumableFileCopy.CopyAsync(source, target, progress, token);
        return new(digest, new FileInfo(target).Length);
    }
}
