using System.Text.RegularExpressions;
using VMCrate.Core;

namespace VMCrate.Windows;

public sealed record AssistantConnectionCheck(string? Version, bool Compatible, bool? SubscriptionAuthenticated);

public sealed partial class SubscriptionCli
{
    public async Task<AssistantConnectionCheck> InspectAsync(CancellationToken cancellation)
    {
        var cli = CliInstallation.Resolve(connection);
        var id = Guid.NewGuid();
        var root = LocalPaths.FullPath(scratchRoot);
        var folder = Path.Combine(root, id.ToString("N"));
        LocalPaths.RequireOrdinaryPath(folder);
        Directory.CreateDirectory(folder);
        try
        {
            var version = await InspectCommandAsync(cli, folder, ["--version"], cancellation);
            var versionText = Regex.Match(version.Text, @"(?<!\d)\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?").Value;
            var help = await InspectCommandAsync(cli, folder,
                connection.Provider == AssistantProvider.Codex ? ["exec", "--help"] : ["--help"], cancellation);
            string[] features = connection.Provider switch
            {
                AssistantProvider.Codex => ["--sandbox", "--output-schema"],
                AssistantProvider.ClaudeCode => ["--safe-mode", "--json-schema"],
                _ => ["--profile", "--patch"]
            };
            var compatible = version.ExitCode == 0 && help.ExitCode == 0 &&
                features.All(flag => help.Text.Contains(flag, StringComparison.Ordinal));

            bool? account = null;
            if (connection.Provider != AssistantProvider.DeepSeekHarness)
            {
                try
                {
                    await VerifyAccountAsync(cli, folder, cancellation);
                    account = true;
                }
                catch (LabException error) when (error.Key == "error.aiLogin") { account = false; }
            }
            return new(versionText.Length > 0 ? versionText : null, compatible, account);
        }
        finally { CleanRequest(folder); }
    }

    private Task<CliOutput> InspectCommandAsync(CliInstallation cli, string folder, string[] args, CancellationToken cancellation)
    {
        var start = cli.Start(folder, args);
        Configure(start, connection.Provider);
        return CliProcess.RunAsync(start, null, TimeSpan.FromSeconds(15), cancellation);
    }
}
