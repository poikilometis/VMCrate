using System.Security.Cryptography;

namespace VMCrate.Core;

public sealed record RevisionCandidate(Guid SourceId, string SourceName, string OriginalTemplate, string TrialName);

public sealed partial class LabService
{
    public IReadOnlyList<RevisionCandidate> ReadRevisionCandidates(Guid targetId)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var target = FindTemplate(state, targetId);
        var candidates = RolloutSources(state, target);
        return candidates.Select(vm => new RevisionCandidate(vm.Id, vm.Name,
            FindTemplate(state, vm.TemplateId!.Value).Name, RevisionTrialName(vm, target))).ToArray();
    }

    public async Task ApplyRevisionRolloutAsync(Guid targetId, IReadOnlyList<Guid> sourceIds,
        CancellationToken cancellation = default)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var target = FindTemplate(state, targetId);
        if (target is not { BootVerifiedUtc: not null, Sha256: { } hash } || sourceIds.Count == 0)
            throw new LabException("error.revision");

        var sources = RolloutSources(state, target).Where(vm => sourceIds.Contains(vm.Id)).ToArray();
        if (sources.Length != sourceIds.Count)
            throw new LabException("error.labChanged");
        var actual = await FileDigests.InspectAsync(LocalPaths.RequireFile(store.TemplateDisk(targetId), ".vhdx"), cancellation);
        if (!hash.Equals(actual.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.projectIntegrity", target.Name);

        var plans = new List<(Machine Desired, bool Recorded)>();
        foreach (var source in sources)
        {
            var id = new Guid(SHA256.HashData(source.Id.ToByteArray().Concat(targetId.ToByteArray()).ToArray()).AsSpan(0, 16));
            var name = RevisionTrialName(source, target);
            var desired = new Machine(id, name, target.Guest, target.SecureBoot, targetId, source.MemoryGiB,
                source.CpuCount, NetworkMode.Offline);
            var existing = state.Machines.SingleOrDefault(vm => vm.Id == id);
            if (existing is null)
                _ = ValidateName(name, state.Machines.Select(vm => vm.Name).Concat(plans.Select(plan => plan.Desired.Name)));
            else if (existing != desired)
                throw new LabException("error.blueprintConflict", name);
            plans.Add((desired, existing is not null));
        }

        foreach (var (vm, recorded) in plans)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!recorded || state.Provisions.Any(provision => provision.Id == vm.Id))
                await ProvisionMachineAsync(state, vm, null, 0, record: !recorded);
            else if (await ObserveOneAsync(store.Location(state, vm)) is { State: PowerState.Missing })
                await ProvisionMachineAsync(state, vm, null, 0, record: false);
        }
    }

    private static Machine[] RolloutSources(LabState state, Template target)
    {
        var series = target.Lineage?.SeriesId ?? target.Id;
        var older = state.Templates.Where(item => item.Id != target.Id &&
            (item.Lineage?.SeriesId ?? item.Id) == series).Select(item => item.Id).ToHashSet();
        return state.Machines.Where(vm => vm is { TemplateId: not null, Disposable: false, BlueprintId: null } &&
            older.Contains(vm.TemplateId.Value)).ToArray();
    }

    private static string RevisionTrialName(Machine source, Template target) =>
        $"{source.Name[..Math.Min(52, source.Name.Length)]} · {target.Id.ToString("N")[..8]}";
}
