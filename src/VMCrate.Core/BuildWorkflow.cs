using System.Diagnostics;

namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task ContinueBuildAsync(Guid id, IProgress<FileProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var build = state.Builds.SingleOrDefault(item => item.Id == id) ?? throw new LabException("error.build");
        await ContinueBuildAsync(state, build, progress, cancellation);
    }

    private async Task ContinueBuildAsync(LabState state, TemplateBuild build, IProgress<FileProgress>? progress,
        CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var next = await AdvanceBuildAsync(state, build, progress, cancellation);
            if (next != build.Stage)
                RecordBuildStep(state, build, next, Elapsed(started));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RecordBuildStep(state, build, build.Stage, Elapsed(started), "status.cancelled");
            throw;
        }
        catch (Exception error) when (error is LabException or IOException or UnauthorizedAccessException)
        {
            RecordBuildStep(state, build, build.Stage, Elapsed(started), error is LabException problem ? problem.Key : "error.operation");
            throw;
        }
    }

    private async Task<BuildStage> AdvanceBuildAsync(LabState state, TemplateBuild build,
        IProgress<FileProgress>? progress, CancellationToken cancellation)
    {
        switch (build.Stage)
        {
            case BuildStage.Verify:
                await VerifyBuildMediaAsync(build, progress, cancellation);
                return BuildStage.Provision;
            case BuildStage.Provision:
                await ProvisionBuilderAsync(state, build, cancellation);
                return BuildStage.Install;
            case BuildStage.Install:
                if (!await hypervisor.CheckBootAsync(store.Location(state, FindMachine(state, build.Id))))
                    throw new LabException("error.buildBoot");
                return BuildStage.Capture;
            case BuildStage.Capture:
                await CaptureBuildAsync(state, build, progress, cancellation);
                return build.Options.VerifyCloneOnPublish ? BuildStage.BootTest : BuildStage.Complete;
            case BuildStage.BootTest:
                var template = FindTemplate(state, build.Id);
                if (!template.Published)
                    await VerifyRevisionAsync(state, template, cancellation);
                return BuildStage.Complete;
            default:
                return build.Stage;
        }
    }

    private async Task ProvisionBuilderAsync(LabState state, TemplateBuild job, CancellationToken cancellation)
    {
        var spec = job.Options.Installer;
        var iso = LocalPaths.RequireFile(store.InstallerMedia(job.Options.Hash), ".iso");
        var existing = state.Machines.SingleOrDefault(machine => machine.Id == job.Id);
        var builder = existing ?? NewMachine(state, spec.Machine, spec.Guest, spec.SecureBoot, null) with { Id = job.Id };
        var location = store.Location(state, builder);
        var observed = existing is null ? null : await ObserveOneAsync(location);
        if (existing is null || observed?.State == PowerState.Missing || state.Provisions.Any(item => item.Id == job.Id))
            await ProvisionMachineAsync(state, builder, iso, spec.DiskGiB, record: existing is null);
        else if (observed?.State != PowerState.Off || observed.Findings.Length != 0)
            throw new LabException("error.build");

        var answer = job.Options.AnswerFile;
        if (answer is not null && job.Options.BootSeedHash is { } expected)
            await ArmBuilderSeedAsync(location, answer, expected, cancellation);
        else if (answer is not null)
            await SendFilesCoreAsync(location, [answer]);
    }

    private async Task CaptureBuildAsync(LabState state, TemplateBuild job, IProgress<FileProgress>? progress,
        CancellationToken cancellation)
    {
        if (state.Templates.Any(item => item.Id == job.Id))
            return;

        var builder = FindMachine(state, job.Id);
        var location = store.Location(state, builder);
        // CaptureDisk confirms the current power state, disk ownership, and absence of checkpoints.
        var source = LocalPaths.RequireFile(await hypervisor.GetCaptureDiskAsync(location), ".vhdx");
        if (builder.Network != NetworkMode.Offline)
        {
            builder = builder with { Network = NetworkMode.Offline, Proxy = null };
            await ApplyNetworkAsync(state, location.Machine, builder);
            location = store.Location(state, builder);
        }
        await hypervisor.SetInboxAsync(location, null);
        await StoreTemplateAsync(state, job.Options.Name, builder.Guest, builder.SecureBoot, source,
            job.Id, job.Options.Revision, job.Options.Lineage, progress, cancellation,
            job.Options.Media ?? new MediaProvenance(MediaVerification.LocalSha256, VerifiedUtc: DateTimeOffset.UtcNow),
            published: !job.Options.VerifyCloneOnPublish);
    }

    private async Task<VmObservation?> ObserveOneAsync(MachineLocation location) =>
        (await hypervisor.ObserveAsync([location])).Machines.SingleOrDefault(vm => vm.Id == location.Machine.Id);

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private void RecordBuildStep(LabState state, TemplateBuild build, BuildStage stage, long elapsedMs, string? error = null)
    {
        var index = state.Builds.FindIndex(item => item.Id == build.Id);
        state.Builds[index] = build with
        {
            Stage = stage,
            History = [.. build.History.TakeLast(63), new BuildEvent(DateTimeOffset.UtcNow, stage, error, elapsedMs)]
        };
        RecordOperation(state, build.Id, RecordedOperation.Build, error ?? stage.ToString(), elapsedMs);
        store.Save(state);
    }
}
