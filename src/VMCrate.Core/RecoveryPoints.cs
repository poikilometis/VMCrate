namespace VMCrate.Core;

public sealed record RecoveryPointScope(Guid? MachineId, string? BlueprintId = null);
public sealed record RecoveryPointDisk(Guid MachineId, Guid TemplateId, string ParentSha256, string Sha256, long Bytes);

public sealed record DiskRecoveryPoint(Guid Id, string Name, DateTimeOffset CreatedUtc, RecoveryPointScope Scope,
    RecoveryPointDisk[] Disks)
{
    public long Bytes => Disks.Sum(item => item.Bytes);
}

internal sealed record DiskRestoreEntry(Guid MachineId, Guid Previous, Guid Replacement);
internal sealed record PendingDiskRestore(Guid PointId, DiskRestoreEntry[] Entries);

internal static class RecoveryPointCatalog
{
    internal static bool Valid(LabState state)
    {
        var points = state.RecoveryPoints;
        if (points is null || points.Any(point => point is null || point.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(point.Name) || point.Scope is null ||
            (point.Scope.MachineId is null) == string.IsNullOrWhiteSpace(point.Scope.BlueprintId) ||
            point.Disks is not { Length: >= 1 and <= 64 } || point.Disks.Any(disk => disk is null ||
                disk.MachineId == Guid.Empty || disk.TemplateId == Guid.Empty || disk.Bytes is <= 0 or > (2L << 40) ||
                !ValidHash(disk.Sha256) || !ValidHash(disk.ParentSha256)) ||
            point.Disks.Select(disk => disk.MachineId).Distinct().Count() != point.Disks.Length) ||
            points.Select(point => point.Id).Distinct().Count() != points.Count)
            return false;

        if (state.DiskRestore is not { } pending)
            return true;
        var active = points.SingleOrDefault(point => point.Id == pending.PointId);
        return active is not null && pending.Entries is { Length: > 0 } &&
            pending.Entries.All(entry => entry is not null && entry.Previous != Guid.Empty &&
                entry.Replacement != Guid.Empty && entry.Previous != entry.Replacement &&
                state.Machines.Any(vm => vm.Id == entry.MachineId)) &&
            pending.Entries.Length == active.Disks.Length &&
            pending.Entries.Select(entry => entry.MachineId).ToHashSet().SetEquals(active.Disks.Select(disk => disk.MachineId));
    }

    private static bool ValidHash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

public sealed partial class LabService
{
    public IReadOnlyList<DiskRecoveryPoint> ReadRecoveryPoints(RecoveryPointScope scope)
    {
        using var workspaceLock = store.Lock();
        return store.Load().RecoveryPoints.Where(item => item.Scope == scope).OrderByDescending(item => item.CreatedUtc).ToArray();
    }

    public async Task<DiskRecoveryPoint> CaptureRecoveryPointAsync(RecoveryPointScope scope, string label,
        CancellationToken cancellation = default)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var name = ValidateName(label, state.RecoveryPoints.Where(item => item.Scope == scope).Select(item => item.Name));
        var members = PointMachines(state, scope);
        var observations = await ReadPointDisksAsync(state, members);
        foreach (var member in members)
            RequireNoDiskRestore(state, member.Id);
        var id = Guid.NewGuid();
        var parent = PointDirectory();
        var directory = LocalPaths.MakeDirectory(Path.Combine(parent, id.ToString("N")));
        var disks = new List<RecoveryPointDisk>(members.Length);
        var held = new List<FileStream>(members.Length);
        try
        {
            foreach (var source in observations.Values)
                held.Add(File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read));
            var parents = await VerifyPointParentsAsync(state, members, cancellation);
            WorkspaceCapacity.RequireSpace(directory, observations.Values.Sum(disk => new FileInfo(disk).Length));
            foreach (var vm in members)
            {
                var source = observations[vm.Id];
                var copy = PointDisk(id, vm.Id);
                var hash = await FileDigests.CopyAndHashAsync(source, copy, cancellation: cancellation);
                File.SetAttributes(copy, File.GetAttributes(copy) | FileAttributes.ReadOnly);
                var template = vm.TemplateId!.Value;
                disks.Add(new(vm.Id, template, parents[template], hash, new FileInfo(copy).Length));
            }
            if (!(await ReadPointDisksAsync(state, members)).OrderBy(item => item.Key)
                .SequenceEqual(observations.OrderBy(item => item.Key)))
                throw new LabException("error.labChanged");

            cancellation.ThrowIfCancellationRequested();
            var point = new DiskRecoveryPoint(id, name, DateTimeOffset.UtcNow, scope, disks.ToArray());
            state.RecoveryPoints.Add(point);
            store.Save(state);
            return point;
        }
        catch
        {
            LocalPaths.RemoveDirectory(parent, id);
            throw;
        }
        finally
        {
            foreach (var disk in held)
                disk.Dispose();
        }
    }

    public void DeleteRecoveryPoint(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        if (state.DiskRestore?.PointId == id)
            throw new LabException("error.recoveryRequired");
        var point = FindRecoveryPoint(state, id);
        // Commit the catalog first so interrupted cleanup remains an unreferenced copy.
        state.RecoveryPoints.Remove(point);
        store.Save(state);
        LocalPaths.RemoveDirectory(PointDirectory(), id);
    }

    private static Machine[] PointMachines(LabState state, RecoveryPointScope scope)
    {
        var members = scope switch
        {
            { MachineId: Guid id, BlueprintId: null } => new[] { FindMachine(state, id) },
            { MachineId: null, BlueprintId: { Length: > 0 } lab } => GetLabMembers(state, lab),
            _ => throw new LabException("error.labChanged")
        };
        if (members.Any(vm => vm.Disposable || vm.TemplateId is null || vm.Network == NetworkMode.Updates ||
                state.Provisions.Any(provision => provision.Id == vm.Id)))
            throw new LabException("error.noTemplate");
        return members;
    }

    private async Task<Dictionary<Guid, string>> ReadPointDisksAsync(LabState state, Machine[] members)
    {
        var observations = await GetLabObservationsAsync(state, members);
        var disks = new Dictionary<Guid, string>();
        foreach (var member in members)
        {
            if (!observations.TryGetValue(member.Id, out var observed) ||
                observed is not { State: PowerState.Off, AttachedDisk: not null, Findings.Length: 0 })
                throw new LabException("error.labState", member.Name);
            var location = store.Location(state, member);
            var disk = LocalPaths.RequireFile(observed.AttachedDisk, ".vhdx");
            if (!Path.GetDirectoryName(disk)!.Equals(location.DiskDirectory, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(disk), "N", out _))
                throw new LabException("error.layout", member.Name);
            disks.Add(member.Id, disk);
        }
        return disks;
    }

    private async Task<Dictionary<Guid, string>> VerifyPointParentsAsync(LabState state, Machine[] members,
        CancellationToken cancellation)
    {
        var hashes = new Dictionary<Guid, string>();
        foreach (var id in members.Select(vm => vm.TemplateId!.Value).Distinct())
        {
            var template = FindTemplate(state, id);
            var actual = await FileDigests.InspectAsync(LocalPaths.RequireFile(store.TemplateDisk(id), ".vhdx"), cancellation);
            if (template.Sha256 is not null && !actual.Sha256.Equals(template.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new LabException("error.projectIntegrity", template.Name);
            hashes.Add(id, actual.Sha256);
        }
        return hashes;
    }

    private static DiskRecoveryPoint FindRecoveryPoint(LabState state, Guid id) =>
        state.RecoveryPoints.SingleOrDefault(point => point.Id == id) ?? throw new LabException("error.labChanged");

    private static void RequireNoDiskRestore(LabState state, Guid machine)
    {
        if (state.DiskRestore?.Entries.Any(entry => entry.MachineId == machine) == true)
            throw new LabException("error.recoveryRequired");
    }

    private string PointDirectory() => Path.Combine(store.Root, "recovery-points");
    private string PointDisk(Guid point, Guid machine) => Path.Combine(PointDirectory(), point.ToString("N"), $"{machine:N}.vhdx");
}
