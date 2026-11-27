namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task RunPinnedLabAsync(LabBlueprint blueprint, BlueprintLock pins, RemoteLabAction action,
        CancellationToken cancellation = default)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var entries = action == RemoteLabAction.PowerOff ? BuildBlueprintEntries(state, blueprint, checkCollisions: true)
            : await PlanBlueprintAsync(state, blueprint);
        var templates = entries.Select(entry => entry.Template).DistinctBy(item => item.Id).ToArray();
        OfflinePlanning.RequireMatchingLock(blueprint, templates.Select(item => item.Name).ToArray(), pins);

        if (action != RemoteLabAction.PowerOff)
        {
            foreach (var template in templates)
            {
                var expected = pins.Templates.Single(item => item.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase));
                var disk = await FileDigests.InspectAsync(LocalPaths.RequireFile(store.TemplateDisk(template.Id), ".vhdx"), cancellation);
                if (!expected.Sha256.Equals(disk.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    disk.SizeBytes != expected.Bytes || template.Revision != expected.Revision ||
                    (template.Sha256 is { } recorded && !recorded.Equals(disk.Sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new LabException("error.projectIntegrity", template.Name);
            }
        }

        cancellation.ThrowIfCancellationRequested();
        if (action == RemoteLabAction.Deploy)
        {
            await DeployBlueprintEntriesAsync(state, entries);
            return;
        }

        var operation = action switch
        {
            RemoteLabAction.Start => LabOperation.Start,
            RemoteLabAction.PowerOff => LabOperation.PowerOff,
            _ => throw new LabException("error.remoteJob")
        };
        var members = GetLabMembers(state, blueprint.Id);
        if (entries.Any(entry => !entry.Recorded || (operation == LabOperation.Start && !entry.Registered)) ||
            !members.Select(vm => vm.Id).ToHashSet()
            .SetEquals(entries.Select(entry => entry.Machine.Id)))
            throw new LabException("error.labChanged");
        await ApplyLabOperationCoreAsync(state, operation, members);
    }
}
