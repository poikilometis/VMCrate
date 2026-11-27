namespace VMCrate.Core;

public sealed record StorageTemplate(Guid Id, string Name, string? Revision, long Bytes, IReadOnlyList<string> Dependents);
public sealed record StorageMachine(Guid Id, string Name, long DiskBytes, long InboxBytes);
public sealed record StorageMedia(string Sha256, long Bytes, int Builds);
public sealed record WorkspaceStorage(string Directory, long? FreeBytes,
    IReadOnlyList<StorageTemplate> Templates, IReadOnlyList<StorageMachine> Machines, IReadOnlyList<StorageMedia> Media,
    long RecoveryPointBytes = 0)
{
    public long TemplateBytes => Templates.Sum(template => template.Bytes);
    public long MachineBytes => Machines.Sum(vm => vm.DiskBytes + vm.InboxBytes);
    public long MediaBytes => Media.Sum(item => item.Bytes);
}

public sealed partial class LabService
{
    public WorkspaceStorage InspectStorage()
    {
        using var workspaceLock = store.Lock();
        return ScanStorage(store.Load());
    }

    private WorkspaceStorage ScanStorage(LabState state)
    {
        var templates = state.Templates.Select(template => new StorageTemplate(template.Id, template.Name,
            template.Revision, FileLength(store.TemplateDisk(template.Id)), state.Machines
                .Where(vm => vm.TemplateId == template.Id).Select(vm => vm.Name).Order().ToArray())).ToArray();
        var machines = state.Machines.Select(vm =>
        {
            var path = store.Location(state, vm);
            return new StorageMachine(vm.Id, vm.Name, ListMedia(path.DiskDirectory, ".vhdx").Sum(file => file.Length),
                ListMedia(path.InboxDirectory, ".iso").Sum(file => file.Length));
        }).ToArray();
        var media = ListMedia(store.Media, ".iso").Where(file => IsSha256(Path.GetFileNameWithoutExtension(file.Name)))
            .Select(file =>
            {
                var hash = Path.GetFileNameWithoutExtension(file.Name);
                return new StorageMedia(hash, file.Length,
                    state.Builds.Count(build => string.Equals(build.Options.Hash, hash, StringComparison.OrdinalIgnoreCase)));
            }).ToArray();

        var recoveryBytes = state.RecoveryPoints.Sum(point => point.Disks.Sum(disk => FileLength(PointDisk(point.Id, disk.MachineId))));
        return new WorkspaceStorage(store.Root, WorkspaceCapacity.FreeBytes(store.Root), templates, machines, media, recoveryBytes);
    }

    private static long FileLength(string path)
    {
        LocalPaths.RequireOrdinaryPath(path);
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }

    private static IEnumerable<FileInfo> ListMedia(string directory, string extension)
    {
        LocalPaths.RequireOrdinaryPath(directory);
        if (!Directory.Exists(directory))
            return [];
        return new DirectoryInfo(directory).EnumerateFiles()
            .Where(file => file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase) &&
                !file.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }
}
