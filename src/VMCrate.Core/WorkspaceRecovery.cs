namespace VMCrate.Core;

public enum RecoveryKind
{
    CatalogUnreadable, RestoreCatalog, HostUnavailable, ResumeProvision, PendingProvision, MissingMachine,
    UncataloguedMachine, RestoreIsolation, MachineFinding, MissingBaseDisk, ResumeBuild, PendingBuild,
    EmptySegment, MissingSegment, UntrackedDirectory, OtherWorkingDisk, PartialDownload, RecreateMissingClone, ResumeDiskRestore
}

public sealed record RecoveryItem(RecoveryKind Kind, Guid Id = default, string? Name = null, string? Detail = null);
public sealed record OwnedSegment(Guid Id, bool InUse);
public sealed record RecoveryInventory(Guid[] Machines, OwnedSegment[] Segments);

public sealed partial class LabService
{
    public async Task<IReadOnlyList<RecoveryItem>> InspectRecoveryAsync()
    {
        using var workspaceLock = store.Lock();
        LabState state;
        try { state = store.Load(); }
        catch (LabException error) when (error.Key is "error.state" or "error.stateVersion")
        {
            return error.Key == "error.state" && store.ReadBackup() is { } backup
                ? [new(RecoveryKind.RestoreCatalog, backup.WorkspaceId, store.BackupPath, error.Detail)]
                : [new(RecoveryKind.CatalogUnreadable, Name: error.Detail, Detail: error.Key)];
        }

        var items = new List<RecoveryItem>();
        if (state.DiskRestore is { } pendingRestore)
            items.Add(new(RecoveryKind.ResumeDiskRestore, pendingRestore.PointId, FindRecoveryPoint(state, pendingRestore.PointId).Name));
        var snapshot = await ObserveStateAsync(state);
        if (snapshot.Host is { } host)
        {
            InspectMachines(state, host, items);
            try { await InspectRegistrationAsync(state, items); }
            catch (LabException error) { items.Add(new(RecoveryKind.HostUnavailable, Detail: error.Detail)); }
        }
        else
        {
            items.Add(new(RecoveryKind.HostUnavailable, Detail: snapshot.HostError?.Detail));
            items.AddRange(state.Provisions.Select(operation => new RecoveryItem(RecoveryKind.PendingProvision,
                operation.Id, FindMachine(state, operation.Id).Name, operation.Iso)));
        }
        InspectBuilds(state, snapshot, items);
        InspectStorage(state, items);
        return items;
    }

    private void InspectMachines(LabState state, HostObservation host, List<RecoveryItem> items)
    {
        foreach (var row in host.Machines)
        {
            var machine = FindMachine(state, row.Id);
            var path = store.Location(state, machine);
            var pending = state.Provisions.SingleOrDefault(item => item.Id == row.Id);
            if (pending is not null)
            {
                var canResume = row is { State: PowerState.Missing } or { State: PowerState.Off, Findings.Length: 0 };
                items.Add(new(canResume ? RecoveryKind.ResumeProvision : RecoveryKind.PendingProvision,
                    row.Id, machine.Name, pending.Iso ?? path.BaseDisk));
            }
            else if (row.State == PowerState.Missing)
            {
                var canRecreate = !machine.Disposable && path.BaseDisk is { } baseDisk && File.Exists(baseDisk);
                items.Add(new(canRecreate ? RecoveryKind.RecreateMissingClone : RecoveryKind.MissingMachine,
                    row.Id, machine.Name, path.Directory));
            }

            foreach (var finding in row.Findings.Where(finding => finding != "audit.missing"))
            {
                var canRepair = finding == "audit.network" && row.State == PowerState.Off && pending is null;
                items.Add(new(canRepair ? RecoveryKind.RestoreIsolation : RecoveryKind.MachineFinding, row.Id, machine.Name, finding));
            }

            if (row.AttachedDisk is not null)
            {
                foreach (var disk in OrdinaryEntries(path.DiskDirectory, folders: false)
                    .Where(file => file.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) &&
                        !file.Equals(row.AttachedDisk, StringComparison.OrdinalIgnoreCase)))
                    items.Add(new(RecoveryKind.OtherWorkingDisk, row.Id, machine.Name, disk));
            }
        }
    }

    private async Task InspectRegistrationAsync(LabState state, List<RecoveryItem> items)
    {
        var inventory = await hypervisor.ObserveRecoveryAsync(state.WorkspaceId);
        foreach (var id in inventory.Machines.Where(id => state.Machines.All(vm => vm.Id != id)))
            items.Add(new(RecoveryKind.UncataloguedMachine, id, $"VMCrate-{id:N}"));
        var expected = state.Machines.Where(vm => vm.PrivateSegment is not null).Select(vm => vm.PrivateSegment!.Value).ToHashSet();
        foreach (var segment in inventory.Segments.Where(item => !item.InUse && !expected.Contains(item.Id)))
            items.Add(new(RecoveryKind.EmptySegment, segment.Id, $"VMCrate-Private-{segment.Id:N}"));
        foreach (var id in expected.Where(id => inventory.Segments.All(item => item.Id != id)))
            items.Add(new(RecoveryKind.MissingSegment, id, $"VMCrate-Private-{id:N}"));
    }

    private static void InspectBuilds(LabState state, LabSnapshot snapshot, List<RecoveryItem> items)
    {
        foreach (var build in state.Builds.Where(build => build.Stage != BuildStage.Complete))
        {
            var canResume = build.Stage switch
            {
                BuildStage.Verify => true,
                BuildStage.Provision => snapshot.Host is not null,
                BuildStage.Install => snapshot.StateOf(build.Id) == PowerState.Running,
                BuildStage.BootTest => snapshot.Host is not null,
                BuildStage.Capture => state.Templates.Any(item => item.Id == build.Id) ||
                    snapshot.StateOf(build.Id) == PowerState.Off,
                _ => false
            };
            items.Add(new(canResume ? RecoveryKind.ResumeBuild : RecoveryKind.PendingBuild,
                build.Id, build.Options.Name, "build." + build.Stage));
        }
    }
}
