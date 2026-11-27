using System.Diagnostics;
using System.Text.Json;
using VMCrate.Core;

namespace VMCrate.Windows;

public sealed partial class SubscriptionCli(CliConnection connection, string scratchRoot) : IAssistantClient
{
    public async Task<string> CompleteAsync(AssistantRequest request, CancellationToken cancellation)
    {
        var cli = CliInstallation.Resolve(connection);
        var runId = Guid.NewGuid();
        var directory = Path.Combine(LocalPaths.FullPath(scratchRoot), runId.ToString("N"));
        LocalPaths.RequireOrdinaryPath(directory);
        Directory.CreateDirectory(directory);
        try
        {
            await VerifyAccountAsync(cli, directory, cancellation);
            var schema = Path.Combine(directory, "schema.json");
            var answer = Path.Combine(directory, "answer.json");
            await File.WriteAllTextAsync(schema, request.Schema, cancellation);
            var start = cli.Start(directory, Arguments(connection.Provider, directory, schema, answer));
            Configure(start, connection.Provider);

            var result = await CliProcess.RunAsync(start, request.Prompt, TimeSpan.FromMinutes(3), cancellation);
            if (result.ExitCode != 0)
                throw new LabException(IsAuthProblem(result.Diagnostics) ? "error.aiLogin" : "error.aiCli",
                    $"{CliInstallation.Command(connection.Provider)} · {result.ExitCode}");

            return connection.Provider switch
            {
                AssistantProvider.ClaudeCode => ClaudeResult(result.Text),
                AssistantProvider.Codex => await ReadAnswerAsync(answer, cancellation),
                AssistantProvider.DeepSeekHarness when result.Diagnostics.Contains(DeepSeekProfile.Ready, StringComparison.Ordinal) =>
                    result.Text.Trim(),
                _ => throw new LabException("error.aiResponse")
            };
        }
        finally { CleanRequest(directory); }
    }

    public void OpenSignIn()
    {
        var cli = CliInstallation.Resolve(connection);
        var args = connection.Provider switch
        {
            AssistantProvider.ClaudeCode => new[] { "auth", "login" },
            AssistantProvider.Codex => ["login"],
            _ => ["--profile", "web"]
        };
        var directory = Path.Combine(LocalPaths.FullPath(scratchRoot), "signin");
        LocalPaths.RequireOrdinaryPath(directory);
        Directory.CreateDirectory(directory);
        var start = cli.Start(directory, args);
        Configure(start, connection.Provider);
        using var process = Process.Start(start);
    }

    internal static string[] Arguments(AssistantProvider provider, string directory, string schema, string answer) => provider switch
    {
        AssistantProvider.ClaudeCode => ["--safe-mode", "--restricted", "--print", "--input-format", "text", "--output-format", "json",
            "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--disallowedTools", "mcp__*",
            "--no-session-persistence", "--json-schema", File.ReadAllText(schema)],
        AssistantProvider.Codex => ["-a", "never", "-c", "features.shell_tool=false", "-c", "web_search=\"disabled\"",
            "-c", "features.multi_agent=false", "-c", "project_doc_max_bytes=0", "exec", "--ignore-user-config", "--ephemeral", "--skip-git-repo-check",
            "--sandbox", "read-only", "--output-schema", schema, "--output-last-message", answer, "-"],
        _ => ["--profile", "headless", "--patch", DeepSeekProfile.Write(directory), "--permission-mode", "read-only",
            "--approval-policy", "never", "-"]
    };

    private static bool IsAuthProblem(string diagnostic) => diagnostic.Contains("ACCOUNT_SIGN_IN_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
        diagnostic.Contains("MISSING_CREDENTIAL", StringComparison.OrdinalIgnoreCase) ||
        diagnostic.Contains("not logged in", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadAnswerAsync(string path, CancellationToken cancellation)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024)
            throw new LabException("error.aiResponse");
        LocalPaths.RequireOrdinaryPath(path);
        return await File.ReadAllTextAsync(path, cancellation);
    }

    private static string ClaudeResult(string text)
    {
        using var json = ParseClaude(text);
        var root = json.RootElement;
        if (root.TryGetProperty("structured_output", out var value) && value.ValueKind == JsonValueKind.Object)
            return value.GetRawText();
        if (root.TryGetProperty("result", out value) && value.ValueKind == JsonValueKind.String)
            return value.GetString()!;
        throw new LabException("error.aiResponse");
    }

    private static JsonDocument ParseClaude(string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException error) { throw new LabException("error.aiResponse", inner: error); }
    }

    private static void CleanRequest(string directory)
    {
        foreach (var name in new[] { "schema.json", "answer.json", "assistant.yml", "text-only.mjs" })
            File.Delete(Path.Combine(directory, name));
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }
}
