namespace VMCrate.Core;

public enum GuestFamily { Windows, Linux }
public enum NetworkMode { Offline, Private, Updates }
public enum PowerState { Off, Running, Saved, Other, Missing, Unavailable }

public sealed record Template(Guid Id, string Name, GuestFamily Guest, bool SecureBoot, string? Sha256 = null, string? Revision = null, DateTimeOffset? CreatedUtc = null, RevisionLink? Lineage = null,
    DateTimeOffset? BootVerifiedUtc = null, MediaProvenance? Media = null, bool Published = true, bool Preferred = false);
public sealed record Machine(
    Guid Id, string Name, GuestFamily Guest, bool SecureBoot, Guid? TemplateId,
    int MemoryGiB, int CpuCount, NetworkMode Network,
    Guid? PrivateSegment = null, string? BlueprintId = null,
    bool Disposable = false, bool SessionStarted = false, UpdateProxy? Proxy = null, FileEvidence[]? SessionFiles = null,
    Guid? TransferId = null);

public sealed record VmObservation(Guid Id, PowerState State, string[] Findings, string? AttachedDisk = null);
public sealed record HostObservation(bool EnhancedSession, VmObservation[] Machines);

public sealed record LabSnapshot(
    IReadOnlyList<Template> Templates, IReadOnlyList<Machine> Machines,
    HostObservation? Host, LabException? HostError, LabException? CleanupError = null, bool RecoverySuggested = false)
{
    public TemplateBuild[] Builds { get; init; } = [];
    public OperationEntry[] RecentOperations { get; init; } = [];

    public PowerState StateOf(Guid id) =>
        Host?.Machines.FirstOrDefault(vm => vm.Id == id)?.State ?? PowerState.Unavailable;
}

public sealed record MachineLocation(Machine Machine, Guid WorkspaceId, string Directory, string? BaseDisk, string? OutboxDisk = null)
{
    public string DiskDirectory => Path.Combine(Directory, "disks");
    public string InboxDirectory => Path.Combine(Directory, "inbox");
    public string NewDiskPath() => Path.Combine(DiskDirectory, $"{Guid.NewGuid():N}.vhdx");
}

public sealed record MachineOptions(string Name, int MemoryGiB, int CpuCount);
public sealed record InstallerOptions(
    MachineOptions Machine, GuestFamily Guest, bool SecureBoot, string IsoPath, int DiskGiB);

public sealed class LabException(string key, string? detail = null, Exception? inner = null)
    : Exception(detail ?? key, inner)
{
    public string Key { get; } = key;
    public string? Detail { get; } = detail;
}
