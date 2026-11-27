namespace VMCrate.Core;

internal sealed record PreparedDisk(Guid FileId, string Sha256, long Bytes);

internal sealed record PendingProvision(Guid Id, string? Iso, int DiskGiB, PreparedDisk? Prepared = null)
{
    internal bool Matches(Machine machine)
    {
        if (Id != machine.Id)
            return false;
        if (Prepared is { } disk)
            return machine is { TransferId: not null, Disposable: false } &&
                (machine.Network == NetworkMode.Offline || machine is { Network: NetworkMode.Private, TemplateId: not null, PrivateSegment: not null }) &&
                Iso is null && DiskGiB == 0 && disk.FileId != Guid.Empty &&
                disk.Bytes is > 0 and <= MachineTransfer.MaxBytes &&
                disk.Sha256 is { Length: 64 } && disk.Sha256.All(char.IsAsciiHexDigit);

        return machine.TemplateId is null
            ? !string.IsNullOrWhiteSpace(Iso) && DiskGiB is >= 16 and <= 2048 && machine.Network == NetworkMode.Offline
            : Iso is null && DiskGiB == 0 && machine.Network != NetworkMode.Updates;
    }
}

public sealed partial class LabService
{
    private async Task ProvisionPreparedDiskAsync(LabState state, Machine machine, string source, string hash, long bytes,
        IProgress<FileProgress>? progress, CancellationToken cancellation)
    {
        var location = store.Location(state, machine);
        if (bytes is <= 0 or > MachineTransfer.MaxBytes || new FileInfo(source).Length != bytes)
            throw new LabException("error.projectIntegrity");
        WorkspaceCapacity.RequireSpace(store.Root, bytes);
        var fileId = Guid.NewGuid();
        var disk = Path.Combine(location.DiskDirectory, $"{fileId:N}.vhdx");
        PendingProvision operation;
        try
        {
            LocalPaths.MakeDirectory(location.DiskDirectory);
            var actual = await FileDigests.CopyAndHashAsync(source, disk, progress, cancellation);
            if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase) || new FileInfo(disk).Length != bytes)
                throw new LabException("error.projectIntegrity");
            if (location.BaseDisk is { } parent)
            {
                await hypervisor.BindDiskParentAsync(disk, parent);
                var evidence = await FileDigests.InspectAsync(disk, cancellation);
                (actual, bytes) = (evidence.Sha256, evidence.SizeBytes);
            }
            else
                await hypervisor.ValidateBaseDiskAsync(disk);

            cancellation.ThrowIfCancellationRequested();
            operation = new PendingProvision(machine.Id, null, 0, new(fileId, actual, bytes));
            state.Machines.Add(machine);
            state.Provisions.Add(operation);
            store.Save(state);
        }
        catch
        {
            File.Delete(disk);
            throw;
        }

        await FinishProvisionAsync(state, location, operation);
    }

    private async Task ProvisionMachineAsync(LabState state, Machine machine, string? iso, int diskGiB, bool record = true)
    {
        var location = store.Location(state, machine);
        LocalPaths.MakeDirectory(location.DiskDirectory);
        if (record)
            state.Machines.Add(machine);
        var operation = state.Provisions.SingleOrDefault(item => item.Id == machine.Id);
        if (operation is null)
        {
            // Record the request before invoking Hyper-V. A lost response is resolved by observation.
            operation = new PendingProvision(machine.Id, iso, diskGiB);
            state.Provisions.Add(operation);
            store.Save(state);
        }

        await FinishProvisionAsync(state, location, operation);
    }

    private async Task FinishProvisionAsync(LabState state, MachineLocation location, PendingProvision operation)
    {
        var observed = await ObserveOneAsync(location);
        if (operation.Prepared is { } prepared)
        {
            var path = LocalPaths.RequireFile(Path.Combine(location.DiskDirectory, $"{prepared.FileId:N}.vhdx"), ".vhdx");
            if (observed is { State: PowerState.Missing })
            {
                var evidence = await FileDigests.InspectAsync(path);
                if (evidence.SizeBytes != prepared.Bytes || !evidence.Sha256.Equals(prepared.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new LabException("error.projectIntegrity");
                await hypervisor.RegisterPreparedDiskAsync(location, path);
                observed = await ObserveOneAsync(location);
            }
            if (observed is not { State: PowerState.Off, Findings.Length: 0, AttachedDisk: not null } ||
                !path.Equals(observed.AttachedDisk, StringComparison.OrdinalIgnoreCase))
                throw new LabException("error.recoveryChanged", location.Machine.Name);
        }
        else if (observed is { State: PowerState.Missing })
        {
            var iso = operation.Iso is null ? null : LocalPaths.RequireFile(operation.Iso, ".iso");
            if (location.BaseDisk is not null)
                LocalPaths.RequireFile(location.BaseDisk, ".vhdx");
            LocalPaths.MakeDirectory(location.DiskDirectory);
            // Use a fresh path. A disk from a failed call may contain data or be partially written.
            await hypervisor.CreateAsync(location, location.NewDiskPath(), iso, operation.DiskGiB);
        }
        else if (observed is not { State: PowerState.Off, Findings.Length: 0 })
        {
            throw new LabException("error.recoveryChanged", location.Machine.Name);
        }

        state.Provisions.Remove(operation);
        try { store.Save(state); }
        catch
        {
            state.Provisions.Add(operation);
            throw;
        }
    }
}
