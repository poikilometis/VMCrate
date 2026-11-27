namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task<Guid> PrepareCleanSessionAsync(Guid templateId, MachineOptions options, IReadOnlyList<string> files)
    {
        // Validate the inbox before allocating a new guest. Recovery uses the ordinary VM inventory.
        if (files.Count != 0)
            ValidateInboxFiles(files);

        using var workspaceLock = store.Lock();
        var state = store.Load();
        var template = FindTemplate(state, templateId);
        RequirePublishedTemplate(template);
        LocalPaths.RequireFile(store.TemplateDisk(template.Id), ".vhdx");
        var machine = NewMachine(state, options, template.Guest, template.SecureBoot, template.Id) with { Disposable = true };
        await ProvisionMachineAsync(state, machine, null, 0);
        if (files.Count > 0)
        {
            var copied = await SendFilesCoreAsync(store.Location(state, machine), files, record: true);
            state.Machines[state.Machines.IndexOf(machine)] = machine with { SessionFiles = copied };
            RecordOperation(state, machine.Id, RecordedOperation.Inbox);
        }
        RecordOperation(state, machine.Id, RecordedOperation.Prepare);
        store.Save(state);
        return machine.Id;
    }
}
