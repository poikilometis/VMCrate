using System.Diagnostics;
using System.Text.Json;
using VMCrate.Core;

namespace VMCrate.Windows;

public sealed partial class SubscriptionCli
{
    private async Task VerifyAccountAsync(CliInstallation cli, string directory, CancellationToken cancellation)
    {
        if (connection.Provider == AssistantProvider.DeepSeekHarness)
            return; // The account-only route checks the saved sign-in on its first request.

        var args = connection.Provider == AssistantProvider.ClaudeCode ? new[] { "auth", "status" } : ["login", "status"];
        var start = cli.Start(directory, args);
        Configure(start, connection.Provider);
        var result = await CliProcess.RunAsync(start, null, TimeSpan.FromSeconds(20), cancellation);
        var active = result.ExitCode == 0 && (connection.Provider == AssistantProvider.ClaudeCode
            ? ActiveClaude(result.Text) : (result.Text + result.Diagnostics).Contains("using ChatGPT", StringComparison.OrdinalIgnoreCase));
        if (!active)
            throw new LabException("error.aiLogin", CliInstallation.Command(connection.Provider));
    }

    private static bool ActiveClaude(string output)
    {
        try
        {
            using var json = JsonDocument.Parse(output);
            return json.RootElement.TryGetProperty("loggedIn", out var logged) && logged.ValueKind == JsonValueKind.True &&
                json.RootElement.TryGetProperty("authMethod", out var method) && method.GetString() == "claude.ai";
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private void Configure(ProcessStartInfo start, AssistantProvider provider)
    {
        string[] variables = provider switch
        {
            AssistantProvider.ClaudeCode => ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN",
                "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
                "CLAUDE_CODE_USE_GATEWAY", "CLAUDE_CODE_SIMPLE"],
            AssistantProvider.Codex => ["OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "OPENAI_ACCESS_TOKEN"],
            _ => ["DEEPSEEK_API_KEY", "DEEPSEEK_BASE_URL", "DSH_PERMISSION_MODE"]
        };
        foreach (var variable in variables)
            start.Environment.Remove(variable);

        if (provider == AssistantProvider.ClaudeCode)
            start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        if (provider == AssistantProvider.DeepSeekHarness)
            start.Environment["DSH_TELEMETRY_DISABLED"] = "1";

        if (!string.IsNullOrWhiteSpace(connection.ProfileDirectory) && provider != AssistantProvider.Codex)
        {
            var profile = LocalPaths.FullPath(connection.ProfileDirectory);
            if (!Directory.Exists(profile))
                throw new LabException("error.aiProfile");
            var variable = provider == AssistantProvider.ClaudeCode ? "CLAUDE_CONFIG_DIR" : "DSH_HOME";
            start.Environment[variable] = profile;
        }
    }
}
