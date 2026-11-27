namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task<LabBackupPreview> PreviewLabBackupAsync(string manifest, CancellationToken cancellation = default)
    {
        using var workspaceLock = store.Lock();
        var plan = PlanBackupRestore(store.Load(), manifest);
        await VerifyBackupMediaAsync(plan, cancellation);
        return plan.Preview;
    }

    public async Task RestoreLabBackupAsync(string manifest, LabBackupPreview preview, CancellationToken cancellation = default)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var plan = PlanBackupRestore(state, manifest);
        if (plan.Preview.Fingerprint != preview.Fingerprint)
            throw new LabException("error.labChanged");

        var held = new List<FileStream>();
        try
        {
            foreach (var (file, _) in plan.Disks())
                held.Add(File.OpenRead(file));
            await VerifyBackupMediaAsync(plan, cancellation);
            await hypervisor.ObserveAsync([]);
            var templates = await RestoreBackupTemplatesAsync(state, plan, cancellation);
            foreach (var item in plan.Machines)
            {
                cancellation.ThrowIfCancellationRequested();
                if (item.Existing is { } existing)
                {
                    if (state.Provisions.SingleOrDefault(entry => entry.Id == existing.Id) is { } pending)
                        await FinishProvisionAsync(state, store.Location(state, existing), pending);
                    continue;
                }
                var spec = item.Spec;
                var template = templates[spec.TemplateId];
                var machine = NewMachine(state, new(spec.Name, spec.MemoryGiB, spec.CpuCount), template.Guest,
                    template.SecureBoot, template.Id) with
                {
                    BlueprintId = plan.Preview.Backup.BlueprintId, Network = spec.Network,
                    PrivateSegment = item.Segment, TransferId = item.TransferId
                };
                await ProvisionPreparedDiskAsync(state, machine, item.File, spec.Disk.Sha256, spec.Disk.Bytes, null, cancellation);
            }
        }
        finally
        {
            foreach (var handle in held)
                handle.Dispose();
        }
    }

    private async Task<Dictionary<Guid, Template>> RestoreBackupTemplatesAsync(LabState state, BackupRestorePlan plan,
        CancellationToken cancellation)
    {
        var templates = new Dictionary<Guid, Template>();
        foreach (var item in plan.Templates)
        {
            var spec = item.Spec;
            if (item.Existing is { } existing)
            {
                templates.Add(spec.Id, existing);
                continue;
            }
            var id = Guid.NewGuid();
            await StoreTemplateAsync(state, spec.Name, spec.Guest, spec.SecureBoot, item.File, id, spec.Revision,
                cancellation: cancellation, expectedSha256: spec.Disk.Sha256);
            templates.Add(spec.Id, FindTemplate(state, id));
        }
        return templates;
    }

    private async Task VerifyBackupMediaAsync(BackupRestorePlan plan, CancellationToken cancellation)
    {
        foreach (var (file, disk) in plan.Disks())
            await VerifyBackupDiskAsync(file, disk, cancellation);
        foreach (var item in plan.Templates.Where(item => item.Existing is not null))
        {
            var disk = LocalPaths.RequireFile(store.TemplateDisk(item.Existing!.Id), ".vhdx");
            await VerifyBackupDiskAsync(disk, item.Spec.Disk, cancellation);
        }
    }

    private static async Task VerifyBackupDiskAsync(string path, LabBackupDisk disk, CancellationToken cancellation)
    {
        if (new FileInfo(path).Length != disk.Bytes ||
            !(await FileDigests.InspectAsync(path, cancellation)).Sha256.Equals(disk.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.projectIntegrity", Path.GetFileName(path));
    }
}
