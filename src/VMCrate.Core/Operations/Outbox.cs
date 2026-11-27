namespace VMCrate.Core;

public sealed partial class LabService
{
    public OutboxTransfer[] ReadOutboxes()
    {
        using var locked = store.Lock();
        return store.Load().Outboxes.ToArray();
    }

    public async Task PrepareOutboxAsync(Guid source, Guid receiver, int capacityMiB)
    {
        using var locked = store.Lock();
        var state = store.Load();
        var from = await RequireOutboxMachineAsync(state, source);
        var to = await RequireOutboxMachineAsync(state, receiver);
        var bytes = (long)capacityMiB << 20;
        if (from.Id == to.Id || to.Disposable || capacityMiB is < 64 or > 8192)
            throw new LabException("error.outbox");
        var transfer = state.Outboxes.SingleOrDefault(item => item.Source == source && item.Receiver == receiver && item.Status == OutboxStatus.Writing);
        if (transfer is null)
        {
            if (state.Outboxes.Count >= 128 || state.Outboxes.Any(item => item.Uses(source) || item.Uses(receiver)))
                throw new LabException("error.outbox");
            WorkspaceCapacity.RequireSpace(store.Root, bytes);
            transfer = new(Guid.NewGuid(), source, receiver, from.Name, to.Name, OutboxStatus.Writing, bytes, DateTimeOffset.UtcNow);
            LocalPaths.MakeDirectory(Path.GetDirectoryName(store.OutboxDisk(transfer.Id))!);
            state.Outboxes.Add(transfer);
            store.Save(state);
        }
        await hypervisor.AttachOutboxAsync(store.Location(state, from), transfer.CapacityBytes);
    }

    public async Task<OutboxTransfer> ReceiveOutboxAsync(Guid id, CancellationToken cancellation = default,
        IReadOnlyList<FileEvidence>? declaredFiles = null)
    {
        using var locked = store.Lock();
        var state = store.Load();
        var item = ReadCurrentOutbox(state, id);
        if (item.Status == OutboxStatus.Received)
            return item;
        if (item.Status == OutboxStatus.Archived)
            throw new LabException("error.outbox");
        var receiver = await RequireOutboxMachineAsync(state, item.Receiver);

        if (item.Status == OutboxStatus.Writing)
        {
            var declarations = declaredFiles?.ToArray();
            if (!OutboxTransfer.ValidClaims(declarations, item.CapacityBytes))
                throw new LabException("error.outboxClaims");
            if (state.Machines.SingleOrDefault(vm => vm.Id == item.Source) is { } source)
                await hypervisor.DetachOutboxAsync(store.Location(state, source));
            var sealedItem = item with { Status = OutboxStatus.Sealed, DeclaredFiles = declarations };
            SaveOutbox(state, item, sealedItem);
            item = sealedItem;
        }
        if (item.Evidence is null)
        {
            var path = LocalPaths.RequireFile(store.OutboxDisk(item.Id), ".vhdx");
            await hypervisor.ValidateBaseDiskAsync(path);
            var sealedItem = item with { Evidence = await FileDigests.InspectAsync(path, cancellation) };
            SaveOutbox(state, item, sealedItem);
            item = sealedItem;
        }
        cancellation.ThrowIfCancellationRequested();
        await hypervisor.AttachOutboxAsync(store.Location(state, receiver), 0);
        var received = item with { Status = OutboxStatus.Received };
        SaveOutbox(state, item, received);
        return received;
    }

    public async Task ArchiveOutboxAsync(Guid id)
    {
        using var locked = store.Lock();
        var state = store.Load();
        var item = ReadCurrentOutbox(state, id);
        if (item.Attachment is Guid owner && state.Machines.SingleOrDefault(vm => vm.Id == owner) is { } machine)
            await hypervisor.DetachOutboxAsync(store.Location(state, machine));
        SaveOutbox(state, item, item with { Status = OutboxStatus.Archived });
    }

    private async Task<Machine> RequireOutboxMachineAsync(LabState state, Guid id)
    {
        var vm = FindMachine(state, id);
        RequireNoDiskRestore(state, id);
        if (vm is not { TemplateId: not null, Network: NetworkMode.Offline } || state.Provisions.Any(item => item.Id == id) ||
            await ObserveOneAsync(store.Location(state, vm)) is not { State: PowerState.Running or PowerState.Off, Findings.Length: 0 })
            throw new LabException("error.outbox", vm.Name);
        return vm;
    }

    private static OutboxTransfer ReadCurrentOutbox(LabState state, Guid id) =>
        state.Outboxes.SingleOrDefault(item => item.Id == id) ?? throw new LabException("error.outbox");

    private void SaveOutbox(LabState state, OutboxTransfer old, OutboxTransfer current)
    {
        state.Outboxes[state.Outboxes.IndexOf(old)] = current;
        store.Save(state);
    }
}
