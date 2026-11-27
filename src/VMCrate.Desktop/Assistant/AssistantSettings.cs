using System.IO;
using System.Text.Json;
using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Desktop;

internal sealed class AssistantSettings
{
    private static readonly string Profile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VMCrate");
    private readonly string file;

    internal AssistantSettings(string? profile = null) => file = Path.Combine(profile ?? Profile, "assistant.json");

    internal CliConnection Read() => ReadSettings()?.Selected is { } mode ? Read(mode) : new(AssistantProvider.Codex);

    internal CliConnection Read(AssistantProvider mode) =>
        ReadSettings()?.Connections?.OfType<CliConnection>().LastOrDefault(connection => connection.Provider == mode) ?? new(mode);

    internal static SubscriptionCli Client(CliConnection connection) =>
        new(connection, Path.Combine(Path.GetTempPath(), "VMCrateAssistant"));

    internal void Save(CliConnection connection)
    {
        var old = ReadSettings()?.Connections ?? [];
        var updated = new Settings(connection.Provider, [.. old.Where(item => item.Provider != connection.Provider), connection]);
        LocalPaths.RequireOrdinaryPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = Path.Combine(Path.GetDirectoryName(file)!, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(updated, JsonDefaults.Options));
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private Settings? ReadSettings()
    {
        try
        {
            LocalPaths.RequireOrdinaryPath(file);
            return File.Exists(file) && new FileInfo(file).Length < 32 * 1024
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), JsonDefaults.Options) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or LabException)
        {
            return null;
        }
    }

    internal static string Name(AssistantProvider mode) => mode switch
    {
        AssistantProvider.ClaudeCode => "Claude Code CLI",
        AssistantProvider.DeepSeekHarness => "DeepSeek Harness",
        _ => "Codex CLI"
    };

    private sealed record Settings(AssistantProvider Selected, CliConnection[] Connections);
}
