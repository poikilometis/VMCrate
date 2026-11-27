using System.Security.Cryptography;
using System.Text;

namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task<BlueprintPreview> PreviewBlueprintAsync(LabBlueprint blueprint)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var entries = await PlanBlueprintAsync(state, blueprint);
        return new BlueprintPreview(entries.Select(entry => new BlueprintVmPreview(entry.Machine.Name,
            entry.Template.Name, entry.Machine.MemoryGiB, entry.Machine.CpuCount, entry.Segment, entry.Registered)).ToArray());
    }

    public async Task DeployBlueprintAsync(LabBlueprint blueprint)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var entries = await PlanBlueprintAsync(state, blueprint);
        await DeployBlueprintEntriesAsync(state, entries);
    }

    private async Task DeployBlueprintEntriesAsync(LabState state, IReadOnlyList<BlueprintEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!entry.Registered || state.Provisions.Any(item => item.Id == entry.Machine.Id))
                await ProvisionMachineAsync(state, entry.Machine, null, 0, record: !entry.Recorded);
        }
    }

    public void ValidateBlueprint(LabBlueprint blueprint)
    {
        using var workspaceLock = store.Lock();
        BuildBlueprintEntries(store.Load(), blueprint, checkCollisions: false);
    }

    private async Task<IReadOnlyList<BlueprintEntry>> PlanBlueprintAsync(LabState state, LabBlueprint blueprint)
    {
        var entries = BuildBlueprintEntries(state, blueprint, checkCollisions: true);
        var locations = entries.Where(entry => entry.Recorded).Select(entry => store.Location(state, entry.Machine)).ToArray();
        var host = await hypervisor.ObserveAsync(locations);
        var observed = host.Machines.ToDictionary(vm => vm.Id);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!entry.Recorded)
                continue;
            if (!observed.TryGetValue(entry.Machine.Id, out var vm) ||
                vm.State is PowerState.Unavailable or PowerState.Other ||
                (vm.State != PowerState.Missing && vm.Findings.Length != 0))
                throw new LabException("error.blueprintConflict", entry.Machine.Name);
            entries[index] = entry with { Registered = vm.State != PowerState.Missing };
        }
        return entries;
    }

    internal static void ValidateBlueprintShape(LabBlueprint blueprint)
    {
        if (blueprint.Version != 1 || !IsSlug(blueprint.Id) || blueprint.Machines is not { Length: >= 1 and <= 64 })
            throw new LabException("error.blueprint");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in blueprint.Machines)
        {
            if (spec is null || string.IsNullOrWhiteSpace(spec.Name) || string.IsNullOrWhiteSpace(spec.Template) ||
                (spec.Segment is not null && !IsSlug(spec.Segment)))
                throw new LabException("error.blueprint");

            var name = ValidateName(spec.Name, names);
            names.Add(name);
            ValidateResources(spec.MemoryGiB, spec.CpuCount);
        }
    }

    private List<BlueprintEntry> BuildBlueprintEntries(LabState state, LabBlueprint blueprint, bool checkCollisions)
    {
        ValidateBlueprintShape(blueprint);
        var entries = new List<BlueprintEntry>();
        foreach (var spec in blueprint.Machines)
        {
            var name = spec.Name.Trim();
            var template = state.Templates.SingleOrDefault(item => item.Published &&
                string.Equals(item.Name, spec.Template, StringComparison.OrdinalIgnoreCase))
                ?? throw new LabException("error.template", spec.Template);
            LocalPaths.RequireFile(store.TemplateDisk(template.Id), ".vhdx");

            var existing = checkCollisions
                ? state.Machines.SingleOrDefault(vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase))
                : null;
            var desired = DescribeBlueprintMachine(state.WorkspaceId, blueprint.Id, spec, template, existing?.Id ?? Guid.NewGuid());
            if (existing is not null && desired != (existing with { Name = name }))
                throw new LabException("error.blueprintConflict", name);

            entries.Add(new BlueprintEntry(desired, template, spec.Segment, existing is not null, false));
        }
        return entries;
    }

    internal static Machine DescribeBlueprintMachine(Guid workspace, string blueprint, BlueprintMachine spec,
        Template template, Guid id)
    {
        var segment = spec.Segment is null ? (Guid?)null : SegmentId(workspace, blueprint, spec.Segment);
        return new(id, spec.Name.Trim(), template.Guest, template.SecureBoot, template.Id, spec.MemoryGiB, spec.CpuCount,
            segment is null ? NetworkMode.Offline : NetworkMode.Private, segment, blueprint);
    }

    private static bool IsSlug(string? value) => value is { Length: >= 1 and <= 32 } &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    private static Guid SegmentId(Guid workspace, string blueprint, string segment)
    {
        // Both text fields are validated slugs so the delimiter cannot be part of either value.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{workspace:N}/{blueprint}/{segment}"));
        return new Guid(digest.AsSpan(0, 16));
    }

    private sealed record BlueprintEntry(Machine Machine, Template Template, string? Segment, bool Recorded, bool Registered);
}
