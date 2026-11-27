using System.Diagnostics;
using System.Text.Json;
using VMCrate.Core;

namespace VMCrate.Windows;

public sealed record CliConnection(AssistantProvider Provider, string? Executable = null, string? ProfileDirectory = null);

internal sealed record CliInstallation(string Executable, string? NodeScript = null)
{
    internal static string Command(AssistantProvider provider) => provider switch
    {
        AssistantProvider.ClaudeCode => "claude",
        AssistantProvider.Codex => "codex",
        AssistantProvider.DeepSeekHarness => "dsh",
        _ => throw new LabException("error.aiCliMissing")
    };

    internal static CliInstallation Resolve(CliConnection connection)
    {
        var command = Command(connection.Provider);
        var path = string.IsNullOrWhiteSpace(connection.Executable)
            ? FindOnPath(command + ".exe") ?? FindOnPath(command + ".cmd")
            : LocalPaths.RequireFile(connection.Executable);
        if (path is null)
            throw new LabException("error.aiCliMissing", command);

        if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new(path);
        return ResolveNpm(connection.Provider, path);
    }

    internal ProcessStartInfo Start(string directory, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = directory
        };
        if (NodeScript is not null)
            start.ArgumentList.Add(NodeScript);
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        return start;
    }

    private static CliInstallation ResolveNpm(AssistantProvider provider, string path)
    {
        var command = Command(provider);
        if (provider == AssistantProvider.ClaudeCode ||
            !Path.GetFileName(path).Equals(command + ".cmd", StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.aiCliMissing", command);

        var relativeName = provider == AssistantProvider.Codex ? "@openai/codex" : "@deepseek-ai/dsh";
        var folder = Path.GetDirectoryName(path)!;
        var package = Path.Combine(folder, "node_modules", relativeName);
        var node = File.Exists(Path.Combine(folder, "node.exe")) ? Path.Combine(folder, "node.exe") : FindOnPath("node.exe");
        var manifest = Path.Combine(package, "package.json");
        if (node is null || !File.Exists(manifest) || new FileInfo(manifest).Length > 256 * 1024)
            throw new LabException("error.aiCliMissing", command);
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(manifest));
            var bin = json.RootElement.GetProperty("bin");
            var script = bin.ValueKind == JsonValueKind.String ? bin.GetString() : bin.GetProperty(command).GetString();
            var fullPath = Path.GetFullPath(Path.Combine(package, script ?? ""));
            if (!fullPath.StartsWith(Path.GetFullPath(package) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(fullPath).ToLowerInvariant() is not (".js" or ".mjs" or ".cjs") || !File.Exists(fullPath))
                throw new LabException("error.aiCliMissing", command);
            return new(node, fullPath);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new LabException("error.aiCliMissing", command, error);
        }
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), fileName);
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate) &&
                !candidate.StartsWith(@"\\", StringComparison.Ordinal))
                return candidate;
        }
        return null;
    }
}
