namespace VMCrate.Core;

public sealed record BuildSpace(long? FreeBytes, long? CacheBytes, long? FullDiskGrowthBytes);

internal static class WorkspaceCapacity
{
    internal static long? FreeBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static void RequireSpace(string path, long bytes)
    {
        if (FreeBytes(path) is long free && free < bytes)
            throw new LabException("error.buildSpace");
    }
}

public sealed partial class LabService
{
    public BuildSpace EstimateBuildSpace(string? isoPath, string? sha256, int diskGiB)
    {
        var hash = sha256?.Trim();
        var cache = hash is not null && IsSha256(hash) && File.Exists(store.InstallerMedia(hash));
        long? isoBytes = cache ? 0 : string.IsNullOrWhiteSpace(isoPath) ? null :
            new FileInfo(LocalPaths.RequireFile(isoPath, ".iso")).Length;
        var fullGrowth = diskGiB is >= 16 and <= 2048 && isoBytes is long size
            ? size + 2L * diskGiB * 1024 * 1024 * 1024 : (long?)null;
        return new(WorkspaceCapacity.FreeBytes(store.Root), isoBytes, fullGrowth);
    }
}
