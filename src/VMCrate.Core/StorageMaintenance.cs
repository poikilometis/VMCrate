namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task DeleteUnusedInstallerAsync(string sha256)
    {
        if (!IsSha256(sha256))
            throw new LabException("error.isoHash");
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var hash = sha256.ToLowerInvariant();
        var path = store.InstallerMedia(hash);
        LocalPaths.RequireOrdinaryPath(path);
        if (!File.Exists(path))
            return;

        if (state.Builds.Any(build => build.Options.Hash == hash && build.Stage != BuildStage.Complete) ||
            await hypervisor.UsesMediaAsync(path))
            throw new LabException("error.inUse");
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }
}
