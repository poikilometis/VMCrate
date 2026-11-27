using System.Security.Cryptography;
using System.Text;

namespace VMCrate.Core;

public sealed partial class LabService
{
    private BackupRestorePlan PlanBackupRestore(LabState state, string manifest)
    {
        var source = LocalPaths.RequireFile(manifest, ".json");
        var backup = LabBackup.Read(source);
        var fingerprint = backup.Fingerprint();
        var directory = Path.GetDirectoryName(source)!;
        var templates = backup.Templates.Select(spec => PlanBackupTemplate(state, directory, spec)).ToArray();
        var machines = backup.Machines.Select(spec => PlanBackupMachine(state, directory, backup.BlueprintId,
            fingerprint, spec, templates.Single(item => item.Spec.Id == spec.TemplateId).Existing)).ToArray();

        var restored = machines.Select(item => item.Existing?.Id).OfType<Guid>().ToHashSet();
        if (state.Machines.Any(vm => vm.BlueprintId == backup.BlueprintId && !restored.Contains(vm.Id)))
            throw new LabException("error.backupConflict", backup.BlueprintId);
        var segments = machines.Select(vm => vm.Segment).OfType<Guid>().ToHashSet();
        if (state.Machines.Any(vm => vm.PrivateSegment is Guid segment && segments.Contains(segment) && !restored.Contains(vm.Id)))
            throw new LabException("error.backupConflict", backup.BlueprintId);

        var bytes = templates.Where(item => item.Existing is null).Sum(item => item.Spec.Disk.Bytes) +
            machines.Where(item => item.Existing is null).Sum(item => item.Spec.Disk.Bytes);
        WorkspaceCapacity.RequireSpace(store.Root, bytes);
        return new(new(fingerprint, backup, bytes, WorkspaceCapacity.FreeBytes(store.Root), restored.Count), templates, machines);
    }

    private static BackupTemplatePlan PlanBackupTemplate(LabState state, string directory, LabBackupTemplate spec)
    {
        var name = ValidateName(spec.Name, []);
        if (spec.Revision is { } revision)
            _ = ValidateName(revision, []);
        var existing = state.Templates.SingleOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && (!existing.Published || existing.Guest != spec.Guest || existing.SecureBoot != spec.SecureBoot ||
                !string.Equals(existing.Sha256, spec.Disk.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new LabException("error.backupConflict", name);
        if (existing is null && state.Builds.Any(item => item.Options.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new LabException("error.backupConflict", name);
        return new(spec, LocalPaths.RequireFile(LabBackup.TemplatePath(directory, spec.Id), ".vhdx"), existing);
    }

    private static BackupMachinePlan PlanBackupMachine(LabState state, string directory, string group,
        string fingerprint, LabBackupMachine spec, Template? template)
    {
        var transfer = BackupKey(fingerprint, spec.Id);
        // Private switches are named by segment ID, so a second workspace needs its own switches.
        Guid? segment = spec.PrivateSegment is Guid original ? BackupKey($"{state.WorkspaceId:N}/{fingerprint}", original) : null;
        var same = state.Machines.SingleOrDefault(vm => vm.TransferId == transfer);
        var name = ValidateName(spec.Name, state.Machines.Where(vm => vm.Id != same?.Id).Select(vm => vm.Name));
        if (same is not null && (template is null || same.TemplateId != template.Id || same.Name != name ||
            same.BlueprintId != group || same.PrivateSegment != segment || same.Network != spec.Network ||
            same.MemoryGiB != spec.MemoryGiB || same.CpuCount != spec.CpuCount || same.Disposable))
            throw new LabException("error.backupConflict", name);
        return new(spec, LocalPaths.RequireFile(LabBackup.MachinePath(directory, spec.Id), ".vhdx"), transfer, segment, same);
    }

    private static Guid BackupKey(string scope, Guid id) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}/{id:N}")).AsSpan(0, 16));

    private sealed record BackupTemplatePlan(LabBackupTemplate Spec, string File, Template? Existing);
    private sealed record BackupMachinePlan(LabBackupMachine Spec, string File, Guid TransferId, Guid? Segment, Machine? Existing);
    private sealed record BackupRestorePlan(LabBackupPreview Preview, BackupTemplatePlan[] Templates, BackupMachinePlan[] Machines)
    {
        internal IEnumerable<(string File, LabBackupDisk Disk)> Disks() =>
            Templates.Select(item => (item.File, item.Spec.Disk)).Concat(Machines.Select(item => (item.File, item.Spec.Disk)));
    }
}
