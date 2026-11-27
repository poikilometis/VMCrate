namespace VMCrate.Core;

public sealed partial class LabService
{
    private async Task StoreTemplateAsync(LabState state, string name, GuestFamily guest, bool secureBoot,
        string source, Guid? id = null, string? revision = null, RevisionLink? lineage = null,
        IProgress<FileProgress>? progress = null, CancellationToken cancellation = default, MediaProvenance? media = null,
        bool published = true, string? expectedSha256 = null)
    {
        var template = new Template(id ?? Guid.NewGuid(), ValidateName(name, state.Templates.Select(item => item.Name)),
            guest, secureBoot, Revision: revision, CreatedUtc: DateTimeOffset.UtcNow, Lineage: lineage, Media: media, Published: published);
        var disk = store.TemplateDisk(template.Id);
        var folder = LocalPaths.MakeDirectory(Path.GetDirectoryName(disk)!);
        LocalPaths.RequireOrdinaryPath(disk);
        var digest = File.Exists(disk)
            ? await VerifyUnpublishedDiskAsync(source, disk, progress, cancellation)
            : await PublishBaseDiskAsync(source, disk, folder, progress, cancellation, expectedSha256);
        RequireTemplateHash(digest, expectedSha256);

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
        IProgress<FileProgress>? progress, CancellationToken cancellation, string? expectedSha256 = null)
    {
        var stage = Path.Combine(folder, "base.pending.vhdx");
        WorkspaceCapacity.RequireSpace(disk, ResumableFileCopy.RemainingBytes(source, stage));
        try
        {
            var digest = await ResumableFileCopy.CopyAsync(source, stage, progress, cancellation);
            RequireTemplateHash(digest, expectedSha256);
            await hypervisor.ValidateBaseDiskAsync(stage);
            cancellation.ThrowIfCancellationRequested();
            File.Move(stage, disk);
            File.SetAttributes(disk, File.GetAttributes(disk) | FileAttributes.ReadOnly);
            return digest;
        }
        catch (LabException)
        {
            File.Delete(stage);
            throw;
        }
    }

    private static void RequireTemplateHash(string actual, string? expected)
    {
        if (expected is not null && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.projectIntegrity");
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
