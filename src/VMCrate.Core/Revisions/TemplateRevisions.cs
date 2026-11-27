using System.Diagnostics;

namespace VMCrate.Core;

public sealed record TemplateRevision(Template Template, string[] Dependents);
public sealed record RevisionLink(Guid SeriesId, Guid PreviousId);
public sealed record RevisionVerification(DateTimeOffset VerifiedUtc, string Sha256);

public sealed partial class LabService
{
    public IReadOnlyList<TemplateRevision> ReadRevisions(Guid templateId)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var selected = FindTemplate(state, templateId);
        var series = selected.Lineage?.SeriesId ?? selected.Id;
        return state.Templates.Where(template => template.Published && (template.Lineage?.SeriesId ?? template.Id) == series)
            .OrderByDescending(template => template.Preferred).ThenByDescending(template => template.CreatedUtc)
            .Select(template => new TemplateRevision(template,
                state.Machines.Where(vm => vm.TemplateId == template.Id).Select(vm => vm.Name).ToArray())).ToArray();
    }

    public async Task PreferRevisionAsync(Guid templateId, CancellationToken cancellation = default)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var candidate = FindTemplate(state, templateId);
        if (candidate is not { Published: true, Sha256: { } expected, BootVerifiedUtc: not null })
            throw new LabException("error.revision");

        var digest = await FileDigests.InspectAsync(LocalPaths.RequireFile(store.TemplateDisk(candidate.Id), ".vhdx"), cancellation);
        if (!digest.Sha256.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.projectIntegrity", candidate.Name);

        var series = candidate.Lineage?.SeriesId ?? candidate.Id;
        for (var index = 0; index < state.Templates.Count; index++)
        {
            var item = state.Templates[index];
            if ((item.Lineage?.SeriesId ?? item.Id) == series)
                state.Templates[index] = item with { Preferred = item.Id == templateId };
        }
        store.Save(state);
    }

    public async Task<RevisionVerification> VerifyRevisionAsync(Guid templateId, CancellationToken cancellation = default)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        return await VerifyRevisionAsync(state, FindTemplate(state, templateId), cancellation);
    }

    private async Task<RevisionVerification> VerifyRevisionAsync(LabState state, Template template, CancellationToken cancellation)
    {
        var baseDisk = LocalPaths.RequireFile(store.TemplateDisk(template.Id), ".vhdx");
        var digest = await FileDigests.InspectAsync(baseDisk, cancellation);
        if (template.Sha256 is not null && !string.Equals(template.Sha256, digest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.projectIntegrity", template.Name);

        var started = Stopwatch.GetTimestamp();
        var options = new MachineOptions($"VMCrate verification {Guid.NewGuid():N}", 4, 2);
        var machine = NewMachine(state, options, template.Guest, template.SecureBoot, template.Id) with { Disposable = true };
        var location = store.Location(state, machine);
        try
        {
            await ProvisionMachineAsync(state, machine, null, 0);
            cancellation.ThrowIfCancellationRequested();
            await hypervisor.StartAsync(location);
            var launched = machine with { SessionStarted = true };
            state.Machines[state.Machines.IndexOf(machine)] = launched;
            machine = launched;
            location = store.Location(state, machine);
            RecordOperation(state, machine.Id, RecordedOperation.Start);
            store.Save(state);
            var deadline = Stopwatch.GetTimestamp();
            while (!await hypervisor.CheckBootAsync(location))
            {
                if (Stopwatch.GetElapsedTime(deadline) > TimeSpan.FromMinutes(3))
                    throw new LabException("error.buildBoot");
                await Task.Delay(TimeSpan.FromSeconds(3), cancellation);
            }

            if (await ObserveOneAsync(location) is not { State: PowerState.Running, Findings.Length: 0 })
                throw new LabException("error.isolation");
        }
        finally
        {
            if (state.Machines.Contains(machine))
            {
                var observation = await ObserveOneAsync(location);
                if (observation?.State is PowerState.Running or PowerState.Saved)
                    await hypervisor.PowerOffAsync(location);
                await DeleteMachineCoreAsync(state, location);
            }
        }

        var time = DateTimeOffset.UtcNow;
        state.Templates[state.Templates.IndexOf(template)] = template with
        {
            BootVerifiedUtc = time, Sha256 = digest.Sha256, Published = true
        };
        RecordOperation(state, template.Id, RecordedOperation.VerifyRevision, "Passed", Elapsed(started));
        store.Save(state);
        return new(time, digest.Sha256);
    }
}
