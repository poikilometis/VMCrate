namespace VMCrate.Core;

public sealed partial class LabService
{
    private async Task StoreTemplateAsync(LabState state, string name, GuestFamily guest, bool secureBoot,
        string source, Guid? id = null, string? revision = null, RevisionLink? lineage = null,
        IProgress<FileProgress>? progress = null, CancellationToken cancellation = default, MediaProvenance? media = null,
        bool published = true)
    {
        var template = new Template(id ?? Guid.NewGuid(), ValidateName(name, state.Templates.Select(item => item.Name)),
            guest, secureBoot, Revision: revision, CreatedUtc: DateTimeOffset.UtcNow, Lineage: lineage, Media: media, Published: published);
        var disk = store.TemplateDisk(template.Id);
        var folder = LocalPaths.MakeDirectory(Path.GetDirectoryName(disk)!);
        LocalPaths.RequireOrdinaryPath(disk);
        var digest = File.Exists(disk)
            ? await VerifyUnpublishedDiskAsync(source, disk, progress, cancellation)
            : await PublishBaseDiskAsync(source, disk, folder, progress, cancellation);

        var stored = template with { Sha256 = digest };
        state.Templates.Add(stored);
        try { store.Save(state); }
        catch
        {
            state.Templates.Remove(stored);
            throw;
        }
    }

    private async Task<string> PublishBaseDiskAsync(string source, string disk, string folder,
        IProgress<FileProgress>? progress, CancellationToken cancellation)
    {
        WorkspaceCapacity.RequireSpace(disk, new FileInfo(source).Length);
        var stage = Path.Combine(folder, $"{Guid.NewGuid():N}.pending.vhdx");
        try
        {
            var digest = await FileDigests.CopyAndHashAsync(source, stage, progress, cancellation);
            await hypervisor.ValidateBaseDiskAsync(stage);
            cancellation.ThrowIfCancellationRequested();
            File.SetAttributes(stage, File.GetAttributes(stage) | FileAttributes.ReadOnly);
            File.Move(stage, disk);
            return digest;
        }
        finally
        {
            if (File.Exists(stage))
            {
                File.SetAttributes(stage, File.GetAttributes(stage) & ~FileAttributes.ReadOnly);
                File.Delete(stage);
            }
        }
    }

    private async Task<string> VerifyUnpublishedDiskAsync(string source, string disk,
        IProgress<FileProgress>? progress, CancellationToken cancellation)
    {
        var evidence = await FileDigests.InspectAsync(disk, cancellation, progress);
        if ((await FileDigests.InspectAsync(source, cancellation)).Sha256 != evidence.Sha256)
            throw new LabException("error.recoveryConflict", disk);
        await hypervisor.ValidateBaseDiskAsync(disk);
        File.SetAttributes(disk, File.GetAttributes(disk) | FileAttributes.ReadOnly);
        return evidence.Sha256;
    }
}
