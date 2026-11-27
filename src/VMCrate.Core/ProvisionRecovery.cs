namespace VMCrate.Core;

internal sealed record PreparedDisk(Guid FileId, string Sha256, long Bytes);

internal sealed record PendingProvision(Guid Id, string? Iso, int DiskGiB, PreparedDisk? Prepared = null)
{
    internal bool Matches(Machine machine)
    {
        if (Id != machine.Id)
            return false;
        if (Prepared is { } disk)
            return machine is { TransferId: not null, TemplateId: null, Network: NetworkMode.Offline, Disposable: false } &&
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
