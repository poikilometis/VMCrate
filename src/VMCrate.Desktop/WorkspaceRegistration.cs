using System.IO;
using System.Text.Json;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class WorkspaceRegistration(string? profile = null)
{
    internal string DefaultWorkspace { get; } = profile ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VMCrate");

    private string Settings => Path.Combine(DefaultWorkspace, "workspace-location.json");

    internal string? Read()
    {
        try
        {
            LocalPaths.RequireOrdinaryPath(Settings);
            if (File.Exists(Settings))
            {
                var registration = JsonSerializer.Deserialize<Location>(File.ReadAllText(Settings), JsonDefaults.Options);
                if (!string.IsNullOrWhiteSpace(registration?.Directory) && Path.IsPathFullyQualified(registration.Directory))
                    return LocalPaths.FullPath(registration.Directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or LabException or ArgumentException)
        {
            // Present the chooser if the registration is unreadable.
        }
        return null;
    }

    internal string Save(string directory)
    {
        var path = WorkspaceVolume.Prepare(directory);
        Directory.CreateDirectory(DefaultWorkspace);
        LocalPaths.RequireOrdinaryPath(Settings);
        var temp = Path.Combine(DefaultWorkspace, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Location(path), JsonDefaults.Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, Settings, overwrite: true);
            return path;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private sealed record Location(string Directory);
}
