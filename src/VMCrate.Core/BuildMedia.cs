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
        WorkspaceCapacity.RequireSpace(target, new FileInfo(input).Length);
        var staged = Path.Combine(store.Media, $"{Guid.NewGuid():N}.tmp");
        try
        {
            var digest = await FileDigests.CopyAndHashAsync(input, staged, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (digest != build.Options.Hash)
                throw new LabException("error.isoMismatch", digest);

            File.Move(staged, target);
            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        }
        finally
        {
            File.Delete(staged);
        }
    }
}
