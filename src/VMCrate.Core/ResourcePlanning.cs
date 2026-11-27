namespace VMCrate.Core;

public sealed record BuildResourcePlan(Guid Id, string Name, BuildStage Stage, int MemoryGiB, int Processors, BuildSpace Space);
public sealed record HostLoad(int? CpuPercent, double? DiskLatencyMs, DateTimeOffset ObservedUtc);

public sealed partial class LabService
{
    public BuildResourcePlan[] ReadBuildResources()
    {
        return store.Load().Builds.Where(job => job.Stage != BuildStage.Complete).Select(PlanResources).ToArray();
    }

    private BuildResourcePlan PlanResources(TemplateBuild job)
    {
        var machine = job.Options.Installer.Machine;
        BuildSpace estimate;
        try { estimate = QueueBuildSpace(job); }
        catch (LabException) { estimate = new(WorkspaceCapacity.FreeBytes(store.Root), null, null); }
        return new(job.Id, job.Options.Name, job.Stage, machine.MemoryGiB, machine.CpuCount, estimate);
    }

    public void CheckBuildCapacity(Guid id, HostCapacity capacity)
    {
        using var locked = store.Lock();
        var job = store.Load().Builds.FirstOrDefault(item => item.Id == id) ?? throw new LabException("error.build");
        var plan = PlanResources(job);
        if (plan.Stage == BuildStage.Provision && (plan.MemoryGiB * (1L << 30) > capacity.FreeMemoryBytes ||
            plan.Processors > capacity.LogicalProcessors))
            throw new LabException("error.resourcesBusy");
    }
}
