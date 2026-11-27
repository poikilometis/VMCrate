namespace VMCrate.Core;

public static class LocalPaths
{
    public static string FullPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (!OperatingSystem.IsWindows())
            return full;

        if (root is null || root.Length != 3 || !char.IsAsciiLetter(root[0]) ||
            root[1] != ':' || full.AsSpan(2).Contains(':') || new DriveInfo(root).DriveType == DriveType.Network)
            throw new LabException("error.localPath", path);
        return full;
    }

    public static string RequireFile(string path, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new LabException("error.file");

        var full = FullPath(path);
        if (!File.Exists(full) || (extension is not null &&
            !Path.GetExtension(full).Equals(extension, StringComparison.OrdinalIgnoreCase)))
            throw new LabException("error.file", full);

        RequireOrdinaryPath(full);
        return full;
    }

    public static void RequireOrdinaryPath(string path)
    {
        for (string? part = FullPath(path); part is not null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if (File.GetAttributes(part).HasFlag(FileAttributes.ReparsePoint))
                    throw new LabException("error.localPath", part);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static string MakeDirectory(string directory)
    {
        RequireOrdinaryPath(directory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static void RemoveDirectory(string parent, Guid ownedId)
    {
        // The immediate child is always an application-generated ID, never an input path.
        var owned = FullPath(Path.Combine(parent, ownedId.ToString("N")));
        RequireOrdinaryPath(owned);
        if (!Directory.Exists(owned))
            return;

        var files = new List<string>();
        CollectOrdinaryFiles(owned, files);
        foreach (var file in files)
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(owned, recursive: true);
    }

    private static void CollectOrdinaryFiles(string directory, List<string> files)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new LabException("error.localPath", entry);

            if (attributes.HasFlag(FileAttributes.Directory))
                CollectOrdinaryFiles(entry, files);
            else
                files.Add(entry);
        }
    }
}
