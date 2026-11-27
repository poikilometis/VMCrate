namespace VMCrate.Core;

public enum LabOperation { Start, PowerOff, Reset, Remove }

public sealed record LabMember(Guid Id, string Name, PowerState State, Guid? Segment, long Bytes);
public sealed record LabOperationPreview(string BlueprintId, LabOperation Operation, IReadOnlyList<LabMember> Members,
    IReadOnlyList<Guid> PrivateSegments);

public sealed partial class LabService
{
    public async Task<LabOperationPreview> PreviewLabOperationAsync(string blueprintId, LabOperation operation)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var members = GetLabMembers(state, blueprintId);
        var observations = await GetLabObservationsAsync(state, members);
        ValidateLabOperation(state, operation, members, observations);
        var storage = ScanStorage(state).Machines.ToDictionary(vm => vm.Id);
        return new LabOperationPreview(blueprintId, operation,
            members.Select(vm => new LabMember(vm.Id, vm.Name, observations[vm.Id].State, vm.PrivateSegment,
                storage[vm.Id].DiskBytes + storage[vm.Id].InboxBytes)).ToArray(),
            members.Select(vm => vm.PrivateSegment).OfType<Guid>().Distinct().ToArray());
    }

    public async Task ApplyLabOperationAsync(LabOperationPreview preview)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var members = GetLabMembers(state, preview.BlueprintId);
        if (!members.Select(vm => vm.Id).ToHashSet().SetEquals(preview.Members.Select(vm => vm.Id)))
            throw new LabException("error.labChanged");

        await ApplyLabOperationCoreAsync(state, preview.Operation, members);
    }

    private async Task ApplyLabOperationCoreAsync(LabState state, LabOperation operation, Machine[] members)
    {
        var observed = await GetLabObservationsAsync(state, members);
        ValidateLabOperation(state, operation, members, observed);
        foreach (var member in members)
        {
            var location = store.Location(state, member);
            LocalPaths.RequireOrdinaryPath(location.Directory);
            var power = observed[member.Id].State;
            switch (operation)
            {
                case LabOperation.Start when power == PowerState.Off:
                    await hypervisor.StartAsync(location);
                    break;
                case LabOperation.PowerOff when power != PowerState.Off:
                    await hypervisor.PowerOffAsync(location);
                    break;
                case LabOperation.Reset:
                    await ResetDiskCoreAsync(location);
                    break;
                case LabOperation.Remove:
                    await DeleteMachineCoreAsync(state, location);
                    break;
            }
        }

        if (operation == LabOperation.Remove)
        {
            var segments = members.Select(vm => vm.PrivateSegment).OfType<Guid>().Distinct();
            foreach (var segment in segments.Where(id => state.Machines.All(vm => vm.PrivateSegment != id)))
                await hypervisor.RemoveEmptySegmentAsync(state.WorkspaceId, segment);
        }
    }

    private static Machine[] GetLabMembers(LabState state, string blueprintId)
    {
        var members = state.Machines.Where(vm => vm.BlueprintId == blueprintId).ToArray();
        if (members.Length == 0)
            throw new LabException("error.labChanged");
        return members;
    }

    private async Task<Dictionary<Guid, VmObservation>> GetLabObservationsAsync(LabState state, Machine[] members) =>
        (await hypervisor.ObserveAsync(members.Select(vm => store.Location(state, vm)).ToArray()))
            .Machines.ToDictionary(vm => vm.Id);

    private static void ValidateLabOperation(LabState state, LabOperation operation, Machine[] members,
        Dictionary<Guid, VmObservation> observations)
    {
        foreach (var member in members)
        {
            if (operation != LabOperation.PowerOff)
                RequireNoDiskRestore(state, member.Id);
            if (operation == LabOperation.Start && state.Provisions.Any(item => item.Id == member.Id))
                throw new LabException("error.recoveryRequired");
            if (operation == LabOperation.Remove && state.RecoveryPoints.Any(point => point.Disks.Any(disk => disk.MachineId == member.Id)))
                throw new LabException("error.inUse", member.Name);
            if (!observations.TryGetValue(member.Id, out var vm))
                throw new LabException("error.labChanged", member.Name);

            var valid = operation switch
            {
                LabOperation.Start => vm.State is PowerState.Off or PowerState.Running && vm.Findings.Length == 0,
                LabOperation.PowerOff => vm.State is PowerState.Off or PowerState.Running or PowerState.Saved,
                LabOperation.Reset => vm.State == PowerState.Off && vm.Findings.Length == 0,
                LabOperation.Remove => vm.State is PowerState.Off or PowerState.Missing,
                _ => false
            };
            if (!valid)
                throw new LabException("error.labState", member.Name);
        }
    }
}
