namespace VMCrate.Core;

public sealed partial class LabService
{
    private static bool IsRevisionTrial(LabState state, Guid id, Guid? source = null) => state.Builds.Any(job =>
        job.Id == id && job.Options.RevisionTrial && (source is null || job.Options.PreviousTemplateId == source));

    private async Task ProvisionTrialBuilderAsync(LabState state, TemplateBuild job, IProgress<FileProgress>? progress,
        CancellationToken cancellation)
    {
        if (state.Machines.SingleOrDefault(vm => vm.Id == job.Id) is { } existing)
        {
            var location = store.Location(state, existing);
            if (state.Provisions.SingleOrDefault(item => item.Id == job.Id) is { } pending)
                await FinishProvisionAsync(state, location, pending);
            else if (await ObserveOneAsync(location) is not { State: PowerState.Off, Findings.Length: 0 })
                throw new LabException("error.recoveryRequired");
            return;
        }

        var source = FindTemplate(state, job.Options.PreviousTemplateId ?? throw new LabException("error.revision"));
        RequirePublishedTemplate(source);
        var disk = LocalPaths.RequireFile(store.TemplateDisk(source.Id), ".vhdx");
        var spec = job.Options.Installer;
        var builder = NewMachine(state, spec.Machine, source.Guest, source.SecureBoot, templateId: null)
            with { Id = job.Id, TransferId = job.Id };
        await ProvisionPreparedDiskAsync(state, builder, disk, job.Options.Hash, new FileInfo(disk).Length,
            progress, cancellation);
    }
}
