namespace VMCrate.Core;

public sealed record WorkspaceVolume(string Directory, long? FreeBytes, string? FileSystem, bool ExistingCatalog)
{
    public static WorkspaceVolume Inspect(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new LabException("error.localPath", directory);
        var path = Path.TrimEndingDirectorySeparator(LocalPaths.FullPath(directory));
        LocalPaths.RequireOrdinaryPath(path);
        var root = Path.GetPathRoot(path)!;
        if (path == root || File.Exists(path))
            throw new LabException("error.workspaceFolder", path);

        var volume = new DriveInfo(root);
        try
        {
            return new(path, volume.AvailableFreeSpace, volume.DriveFormat, HasCatalog(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(path, null, null, HasCatalog(path));
        }
    }

    public static bool HasCatalog(string directory) =>
        File.Exists(Path.Combine(directory, "state.json")) || File.Exists(Path.Combine(directory, "state.previous.json"));

    public static string Prepare(string directory)
    {
        var volume = Inspect(directory);
        LocalPaths.MakeDirectory(volume.Directory);
        var test = Path.Combine(volume.Directory, $".vmcrate-{Guid.NewGuid():N}.tmp");
        using (var output = new FileStream(test, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.DeleteOnClose))
        {
            output.WriteByte(0);
            output.Flush(flushToDisk: true);
        }
        return volume.Directory;
    }
}

public enum HyperVReadiness { Ready, ModuleMissing, ServiceStopped, AccessDenied, Unavailable }

public sealed record HostReadiness(HyperVReadiness HyperV, bool Windows11X64, bool SandboxAvailable, string? Detail,
    HostCapacity? Capacity = null);

public sealed record HostCapacity(int WindowsBuild, string? Edition, bool? FirmwareVirtualization,
    bool? RestartRequired, int? LogicalProcessors, long? FreeMemoryBytes);
