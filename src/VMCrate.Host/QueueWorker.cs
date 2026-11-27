using VMCrate.Core;
using VMCrate.Windows;

namespace VMCrate.Host;

internal sealed class QueueWorker(QueueSettings settings, JobLedger ledger, ILogger<QueueWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hypervisor = new PowerShellHypervisor();
        while (!stoppingToken.IsCancellationRequested)
        {
            if (ledger.Claim() is not { } running)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            string? errorCode = null;
            try
            {
                var owner = settings.Accounts.SingleOrDefault(account => account.Id == running.Owner);
                if (owner is null || !owner.Recipes.TryGetValue(running.Job.Recipe, out var recipe) ||
                    recipe.Fingerprint != running.RecipeFingerprint)
                    throw new LabException("error.remoteJob");
                var lab = new LabService(owner.Workspace, hypervisor, new UdfInboxWriter());
                await lab.RunPinnedLabAsync(recipe.Blueprint, recipe.Pins, running.Job.Action);
            }
            catch (Exception error)
            {
                log.LogWarning(error, "Job {JobId} failed for {Account}", running.Job.Id, running.Owner);
                errorCode = error is LabException problem ? problem.Key : "error.backend";
            }
            ledger.Complete(running, errorCode);
        }
    }
}
