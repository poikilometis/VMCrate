namespace VMCrate.Core;

public sealed record BuildQueueLimits(int MemoryGiB, int Processors, long GrowthBytes);
public sealed record BuildQueueStep(Guid Id, string Name, BuildStage Stage, string? BlockedBy);

public sealed partial class LabService
{
    public async Task<BuildQueueStep?> PrepareNextQueuedBuildAsync(BuildQueueLimits limits, CancellationToken cancellation = default)
    {
        if (limits.MemoryGiB <= 0 || limits.Processors <= 0 || limits.GrowthBytes <= 0)
            throw new LabException("error.resources");
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var next = state.Builds.FirstOrDefault(job => job.Stage is BuildStage.Verify or BuildStage.Provision);
        if (next is null)
            return null;

        var active = state.Machines.Where(vm => vm.Id != next.Id &&
            state.Builds.Any(job => job.Id == vm.Id && job.Stage != BuildStage.Complete)).ToArray();
        var minimumMemory = active.Sum(vm => vm.MemoryGiB) + next.Options.Installer.Machine.MemoryGiB;
        var minimumCpu = active.Sum(vm => vm.CpuCount) + next.Options.Installer.Machine.CpuCount;
        var disk = EstimateBuildSpace(next.Options.Installer.IsoPath, next.Options.Hash, next.Options.Installer.DiskGiB);
        var growth = disk.FullDiskGrowthBytes + state.Builds.Where(job => active.Any(vm => vm.Id == job.Id))
            .Sum(job => 2L * job.Options.Installer.DiskGiB * 1024 * 1024 * 1024);
        var problem = minimumMemory > limits.MemoryGiB || minimumCpu > limits.Processors
            ? "error.resources"
            : growth > limits.GrowthBytes || disk.CacheBytes > disk.FreeBytes
                ? "error.buildSpace"
                : null;
        if (problem is not null)
            return new(next.Id, next.Options.Name, next.Stage, problem);

        await ContinueBuildAsync(state, next, null, cancellation);
        var prepared = state.Builds.Single(item => item.Id == next.Id);
        return new(prepared.Id, prepared.Options.Name, prepared.Stage, null);
    }
}
