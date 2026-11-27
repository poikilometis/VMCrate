using System.Globalization;
using System.IO;
using System.Text.Json;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed class Preferences(string? root = null)
{
    internal string Root { get; } = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VMCrate");

    internal string ReadLanguage()
    {
        try
        {
            var path = Path.Combine(Root, "settings.json");
            LocalPaths.RequireOrdinaryPath(path);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonDefaults.Options)?.Language
                    ?? CultureInfo.CurrentUICulture.Name;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or LabException)
        {
            // A damaged settings file cannot prevent opening the workspace.
        }
        return CultureInfo.CurrentUICulture.Name;
    }

    internal void WriteLanguage(string language)
    {
        var path = Path.Combine(Root, "settings.json");
        LocalPaths.RequireOrdinaryPath(path);
        Directory.CreateDirectory(Root);
        var temp = Path.Combine(Root, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Settings(language), JsonDefaults.Options));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private sealed record Settings(string Language);
}
