namespace VMCrate.Core;

public sealed partial class LabService
{
    private async Task VerifyBuildMediaAsync(TemplateBuild build, IProgress<FileProgress>? progress,
        CancellationToken cancellation)
    {
        LocalPaths.MakeDirectory(store.Media);
        var target = store.InstallerMedia(build.Options.Hash);
        LocalPaths.RequireOrdinaryPath(target);
        if (File.Exists(target))
        {
            var cached = await FileDigests.InspectAsync(target, cancellation, progress);
            if (cached.Sha256 != build.Options.Hash)
                throw new LabException("error.isoMismatch", cached.Sha256);
            return;
        }

        var input = LocalPaths.RequireFile(build.Options.Installer.IsoPath, ".iso");
        var staged = Path.Combine(store.Media, $"{build.Id:N}.copying");
        WorkspaceCapacity.RequireSpace(target, ResumableFileCopy.RemainingBytes(input, staged));
        var digest = await ResumableFileCopy.CopyAsync(input, staged, progress, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (digest != build.Options.Hash)
        {
            File.Delete(staged);
            throw new LabException("error.isoMismatch", digest);
        }
        File.Move(staged, target);
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
    }
}
