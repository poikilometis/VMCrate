namespace VMCrate.Core;

public sealed partial class LabService
{
    private void InspectStorage(LabState state, List<RecoveryItem> items)
    {
        foreach (var template in state.Templates)
        {
            var disk = store.TemplateDisk(template.Id);
            LocalPaths.RequireOrdinaryPath(disk);
            if (!File.Exists(disk))
                items.Add(new(RecoveryKind.MissingBaseDisk, template.Id, template.Name, disk));
        }

        InspectDirectories(store.Templates, state.Templates.Select(item => item.Id), items);
        InspectDirectories(store.Machines, state.Machines.Select(item => item.Id), items);
        foreach (var partial in OrdinaryEntries(store.Media, folders: false)
            .Where(path => path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) &&
                IsSha256(Path.GetFileNameWithoutExtension(path))))
            items.Add(new(RecoveryKind.PartialDownload, Detail: partial));
    }

    private static void InspectDirectories(string path, IEnumerable<Guid> known, List<RecoveryItem> items)
    {
        var ids = known.ToHashSet();
        foreach (var directory in OrdinaryEntries(path, folders: true))
        {
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) && !ids.Contains(id))
                items.Add(new(RecoveryKind.UntrackedDirectory, id, Detail: directory));
            if (Path.GetFileName(path) == "templates")
            {
                foreach (var file in OrdinaryEntries(directory, folders: false)
                    .Where(entry => entry.EndsWith(".pending.vhdx", StringComparison.OrdinalIgnoreCase)))
                    items.Add(new(RecoveryKind.OtherWorkingDisk, Detail: file));
            }
        }
    }

    private static IEnumerable<string> OrdinaryEntries(string directory, bool folders)
    {
        LocalPaths.RequireOrdinaryPath(directory);
        if (!Directory.Exists(directory))
            yield break;

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (!attributes.HasFlag(FileAttributes.ReparsePoint) && attributes.HasFlag(FileAttributes.Directory) == folders)
                yield return entry;
        }
    }
}
