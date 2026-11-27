namespace VMCrate.Core;

public sealed record DiskRestoreChange(MachineLocation Location, string Previous, string Replacement);

public sealed partial class LabService
{
    public async Task RestoreRecoveryPointAsync(Guid id, CancellationToken cancellation = default)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var point = FindRecoveryPoint(state, id);
        if (state.DiskRestore is { } active && active.PointId != id)
            throw new LabException("error.recoveryRequired");

        var members = PointMachines(state, point.Scope);
        var current = await ReadPointDisksAsync(state, members);
        var disks = point.Disks.ToDictionary(disk => disk.MachineId);
        if (!current.Keys.ToHashSet().SetEquals(disks.Keys) ||
            members.Any(vm => vm.TemplateId != disks[vm.Id].TemplateId))
            throw new LabException("error.labChanged");
        var parents = await VerifyPointParentsAsync(state, members, cancellation);
        if (point.Disks.Any(disk => !parents[disk.TemplateId].Equals(disk.ParentSha256, StringComparison.OrdinalIgnoreCase)))
            throw new LabException("error.projectIntegrity", point.Name);

        var pending = state.DiskRestore ?? await StagePointRestoreAsync(state, point, members, current, cancellation);
        var changes = pending.Entries.Select(entry =>
        {
            var location = store.Location(state, FindMachine(state, entry.MachineId));
            return new DiskRestoreChange(location, Path.Combine(location.DiskDirectory, $"{entry.Previous:N}.vhdx"),
                Path.Combine(location.DiskDirectory, $"{entry.Replacement:N}.vhdx"));
        }).ToArray();
        foreach (var change in changes)
        {
            var vm = change.Location.Machine;
            var path = current[vm.Id];
            if (!path.Equals(change.Previous, StringComparison.OrdinalIgnoreCase) &&
                !path.Equals(change.Replacement, StringComparison.OrdinalIgnoreCase))
                throw new LabException("error.labChanged", vm.Name);
            var evidence = await FileDigests.InspectAsync(LocalPaths.RequireFile(change.Replacement, ".vhdx"), cancellation);
            if (evidence.Sha256 != disks[vm.Id].Sha256 || evidence.SizeBytes != disks[vm.Id].Bytes)
                throw new LabException("error.projectIntegrity", vm.Name);
        }

        cancellation.ThrowIfCancellationRequested();
        await hypervisor.RestoreDisksAsync(changes);
        state.DiskRestore = null;
        foreach (var member in members)
            RecordOperation(state, member.Id, RecordedOperation.Reset);
        store.Save(state);
    }

    private async Task<PendingDiskRestore> StagePointRestoreAsync(LabState state, DiskRecoveryPoint point, Machine[] members,
        Dictionary<Guid, string> current, CancellationToken cancellation)
    {
        WorkspaceCapacity.RequireSpace(store.Root, point.Bytes);
        var entries = new List<DiskRestoreEntry>(members.Length);
        var staged = new List<string>(members.Length);
        try
        {
            foreach (var member in members)
            {
                var location = store.Location(state, member);
                var destination = location.NewDiskPath();
                staged.Add(destination);
                var disk = point.Disks.Single(item => item.MachineId == member.Id);
                var source = LocalPaths.RequireFile(PointDisk(point.Id, member.Id), ".vhdx");
                if (new FileInfo(source).Length != disk.Bytes ||
                    await FileDigests.CopyAndHashAsync(source, destination, cancellation: cancellation) != disk.Sha256)
                    throw new LabException("error.projectIntegrity", member.Name);
                entries.Add(new(member.Id, Guid.ParseExact(Path.GetFileNameWithoutExtension(current[member.Id]), "N"),
                    Guid.ParseExact(Path.GetFileNameWithoutExtension(destination), "N")));
            }
            cancellation.ThrowIfCancellationRequested();
            var pending = new PendingDiskRestore(point.Id, entries.ToArray());
            state.DiskRestore = pending;
            store.Save(state);
            return pending;
        }
        catch
        {
            foreach (var path in staged)
                File.Delete(path);
            throw;
        }
    }
}
