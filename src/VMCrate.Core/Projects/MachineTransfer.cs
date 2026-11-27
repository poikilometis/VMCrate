using System.Text.Json;

namespace VMCrate.Core;

public sealed record MachineTransfer(int Version, Guid Id, DateTimeOffset CreatedUtc, string Name, GuestFamily Guest,
    bool SecureBoot, int MemoryGiB, int CpuCount, string Sha256, long Bytes)
{
    public const string ManifestName = "vm.json";
    internal const string DiskName = "system.vhdx";
    internal const long MaxBytes = (2L << 40) + (1L << 30);

    public static MachineTransfer Read(string manifest)
    {
        var path = LocalPaths.RequireFile(manifest, ".json");
        if (!Path.GetFileName(path).Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.transfer");
        using var input = File.OpenRead(path);
        if (input.Length > 8192)
            throw new LabException("error.transfer");
        try
        {
            var item = JsonSerializer.Deserialize<MachineTransfer>(input, AssistantJson.Strict);
            if (item is not { Version: 1, Bytes: > 0 and <= MaxBytes, MemoryGiB: >= 2 and <= 128, CpuCount: >= 1 and <= 32 } ||
                item.Id == Guid.Empty || !Enum.IsDefined(item.Guest) || item.Sha256 is not { Length: 64 } ||
                !item.Sha256.All(char.IsAsciiHexDigit) || string.IsNullOrWhiteSpace(item.Name))
                throw new LabException("error.transfer");
            return item;
        }
        catch (JsonException error) { throw new LabException("error.transfer", inner: error); }
    }
}

public sealed partial class LabService
{
    public async Task<MachineTransfer> ExportMachineAsync(Guid id, string directory,
        IProgress<FileProgress>? progress = null, CancellationToken cancellation = default)
    {
        var destination = LocalPaths.FullPath(directory);
        LocalPaths.RequireOrdinaryPath(destination);
        var parent = Path.GetDirectoryName(destination);
        if (parent is null || !Directory.Exists(parent) || Path.Exists(destination))
            throw new LabException("error.outputExists", destination);

        using var workspaceLock = store.Lock();
        var state = store.Load();
        var vm = FindMachine(state, id);
        RequireNoDiskRestore(state, id);
        if (vm.Disposable || vm.Network == NetworkMode.Updates || state.Provisions.Any(item => item.Id == id) ||
            state.Builds.Any(build => build.Id == id))
            throw new LabException("error.transferState");
        var location = store.Location(state, vm);
        if (await ObserveOneAsync(location) is not { State: PowerState.Off, Findings.Length: 0 })
            throw new LabException("error.transferState");

        var key = Guid.NewGuid();
        var stage = LocalPaths.MakeDirectory(Path.Combine(parent, key.ToString("N")));
        try
        {
            var disk = Path.Combine(stage, MachineTransfer.DiskName);
            await hypervisor.ExportWorkingDiskAsync(location, disk, cancellation);
            var evidence = await FileDigests.InspectAsync(LocalPaths.RequireFile(disk, ".vhdx"), cancellation, progress);
            var transfer = new MachineTransfer(1, key, DateTimeOffset.UtcNow, vm.Name, vm.Guest, vm.SecureBoot,
                vm.MemoryGiB, vm.CpuCount, evidence.Sha256, evidence.SizeBytes);
            await OutputFile.WriteNewAsync(Path.Combine(stage, MachineTransfer.ManifestName), ".json",
                stream => JsonSerializer.SerializeAsync(stream, transfer, JsonDefaults.Options, cancellation));
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(stage, destination);
            return transfer;
        }
        finally { LocalPaths.RemoveDirectory(parent, key); }
    }

    public async Task<Guid> ImportMachineAsync(string manifest, string name, IProgress<FileProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var source = LocalPaths.RequireFile(manifest, ".json");
        var transfer = MachineTransfer.Read(source);
        using var workspaceLock = store.Lock();
        var state = store.Load();
        if (state.Machines.SingleOrDefault(vm => vm.TransferId == transfer.Id) is { } imported)
        {
            if (state.Provisions.SingleOrDefault(item => item.Id == imported.Id) is { } pending)
                await FinishProvisionAsync(state, store.Location(state, imported), pending);
            return imported.Id;
        }

        var machine = NewMachine(state, new(name, transfer.MemoryGiB, transfer.CpuCount), transfer.Guest,
            transfer.SecureBoot, templateId: null) with { TransferId = transfer.Id };
        var incoming = LocalPaths.RequireFile(Path.Combine(Path.GetDirectoryName(source)!, MachineTransfer.DiskName), ".vhdx");
        await ProvisionPreparedDiskAsync(state, machine, incoming, transfer.Sha256, transfer.Bytes, progress, cancellation);
        return machine.Id;
    }
}
