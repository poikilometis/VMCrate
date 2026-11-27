using System.Diagnostics;
using System.Text.Json;

namespace VMCrate.Core;

public sealed record RestoreDrillReceipt(string Fingerprint, DateTimeOffset CompletedUtc, int Booted, long ElapsedMs, Guid? JobId = null);

public sealed partial class LabService
{
    public RestoreDrillReceipt? ReadRestoreDrill(string manifest)
    {
        var fingerprint = LabBackup.Read(LocalPaths.RequireFile(manifest, ".json")).Fingerprint();
        var path = DrillReceiptPath(fingerprint);
        LocalPaths.RequireOrdinaryPath(path);
        if (!File.Exists(path))
            return null;
        using var input = File.OpenRead(path);
        if (input.Length > 8192)
            return null;
        return JsonSerializer.Deserialize<RestoreDrillReceipt>(input, AssistantJson.Strict) is { } result &&
               result.Fingerprint == fingerprint ? result : null;
    }

    public async Task<RestoreDrillReceipt> TestLabBackupAsync(string manifest, CancellationToken cancellation = default, Guid? jobId = null)
    {
        if (jobId is not null && ReadRestoreDrill(manifest) is { } earlier && earlier.JobId == jobId)
            return earlier;
        var started = Stopwatch.GetTimestamp();
        var parent = Path.Combine(store.Root, "restore-drills");
        var id = jobId ?? Guid.NewGuid();
        var root = LocalPaths.MakeDirectory(Path.Combine(parent, id.ToString("N")));
        var drill = new LabService(root, hypervisor, inboxWriter);
        LabBackupPreview preview;
        var booted = 0;
        try
        {
            // Reclaim an interrupted drill by its exact workspace before replaying it.
            await drill.CleanDrillAsync();
            cancellation.ThrowIfCancellationRequested();
            preview = drill.PlanDrillRestore(manifest);
            await drill.RestoreLabBackupAsync(manifest, preview, cancellation);
            var snapshot = await drill.ObserveAsync();
            if (snapshot.Host is null || snapshot.Machines.Count != preview.Backup.Machines.Length)
                throw new LabException("error.backupFormat");
            foreach (var vm in snapshot.Machines)
            {
                cancellation.ThrowIfCancellationRequested();
                await drill.IsolateDrillCopyAsync(vm.Id);
                await drill.StartAsync(vm.Id);
                await drill.WaitForDrillBootAsync(vm.Id, cancellation);
                await drill.PowerOffAsync(vm.Id);
                booted++;
            }
        }
        finally
        {
            await drill.CleanDrillAsync();
            LocalPaths.RemoveDirectory(parent, id);
        }

        var receipt = new RestoreDrillReceipt(preview.Fingerprint, DateTimeOffset.UtcNow, booted, Elapsed(started), jobId);
        var target = DrillReceiptPath(preview.Fingerprint);
        LocalPaths.MakeDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + $".{Guid.NewGuid():N}.json";
        try
        {
            OutputFile.WriteNew(temporary, ".json", stream => JsonSerializer.Serialize(stream, receipt, JsonDefaults.Options));
            LocalPaths.RequireOrdinaryPath(target);
            File.Move(temporary, target, overwrite: true);
        }
        finally { File.Delete(temporary); }
        return receipt;
    }

    private LabBackupPreview PlanDrillRestore(string manifest)
    {
        using var locked = store.Lock();
        return PlanBackupRestore(store.Load(), manifest).Preview;
    }

    private async Task IsolateDrillCopyAsync(Guid id)
    {
        using var locked = store.Lock();
        var state = store.Load();
        var machine = FindMachine(state, id);
        await ApplyNetworkAsync(state, machine, machine with { Network = NetworkMode.Offline, PrivateSegment = null, BlueprintId = null });
    }

    private async Task WaitForDrillBootAsync(Guid id, CancellationToken cancellation)
    {
        using var locked = store.Lock();
        var state = store.Load();
        var vm = FindMachine(state, id);
        var location = store.Location(state, vm);
        var start = Stopwatch.GetTimestamp();
        while (!await hypervisor.CheckBootAsync(location))
        {
            if (Stopwatch.GetElapsedTime(start) >= TimeSpan.FromMinutes(2))
                throw new LabException("error.buildBoot", vm.Name);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellation);
        }
        var isolation = await ObserveIsolationAsync(state, vm);
        if (isolation is not { Profile: NetworkMode.Offline, Host: { NetworkPolicyMatches: true, Switches.Length: 0 } })
            throw new LabException("error.isolation");
    }

    private async Task CleanDrillAsync()
    {
        if (!WorkspaceVolume.HasCatalog(store.Root))
            return;
        var snapshot = await ObserveAsync();
        if (snapshot.Host is null)
            throw new LabException("error.host", store.Root);
        foreach (var vm in snapshot.Machines)
        {
            if (snapshot.StateOf(vm.Id) is PowerState.Running or PowerState.Saved or PowerState.Other)
                await PowerOffAsync(vm.Id);
            await DeleteMachineAsync(vm.Id);
        }
        foreach (var segment in (await InspectRecoveryAsync()).Where(item => item.Kind == RecoveryKind.EmptySegment))
            await RemoveUnusedPrivateSegmentAsync(segment.Id);
    }

    private string DrillReceiptPath(string fingerprint) => Path.Combine(store.Root, "restore-checks", fingerprint + ".json");
}
