using System.Security.Cryptography;
using System.Text.Json;

namespace VMCrate.Core;

public sealed record LabBackupDisk(string Sha256, long Bytes)
{
    internal bool Valid => Sha256 is { Length: 64 } && Sha256.All(char.IsAsciiHexDigit) &&
        Bytes is > 0 and <= MachineTransfer.MaxBytes;
}

public sealed record LabBackupTemplate(Guid Id, string Name, GuestFamily Guest, bool SecureBoot, string? Revision, LabBackupDisk Disk);
public sealed record LabBackupMachine(Guid Id, string Name, Guid TemplateId, int MemoryGiB, int CpuCount,
    NetworkMode Network, Guid? PrivateSegment, LabBackupDisk Disk);

public sealed record LabBackup(int Version, Guid Id, string BlueprintId, DateTimeOffset CreatedUtc,
    LabBackupTemplate[] Templates, LabBackupMachine[] Machines)
{
    public const string ManifestName = "lab-backup.json";

    internal static LabBackup Read(string path)
    {
        if (!Path.GetFileName(path).Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.backupFormat");
        using var input = File.OpenRead(path);
        if (input.Length > 256 * 1024)
            throw new LabException("error.backupFormat");
        try
        {
            var backup = JsonSerializer.Deserialize<LabBackup>(input, AssistantJson.Strict);
            if (backup is null || !backup.IsValid())
                throw new LabException("error.backupFormat");
            return backup;
        }
        catch (JsonException error) { throw new LabException("error.backupFormat", inner: error); }
    }

    internal bool IsValid()
    {
        if (Version != 1 || Id == Guid.Empty || BlueprintId is not { Length: >= 1 and <= 32 } ||
            !char.IsAsciiLetterOrDigit(BlueprintId[0]) || BlueprintId.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
            Templates is not { Length: >= 1 and <= 64 } || Machines is not { Length: >= 1 and <= 64 })
            return false;
        if (Templates.Any(item => item is null || item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Name) ||
                !Enum.IsDefined(item.Guest) || item.Disk is not { Valid: true }) ||
            Machines.Any(item => item is null || item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Name) ||
                item.MemoryGiB is < 2 or > 128 || item.CpuCount is < 1 or > 32 || item.Disk is not { Valid: true } ||
                item.Network is not (NetworkMode.Offline or NetworkMode.Private) || item.PrivateSegment == Guid.Empty ||
                item is { Network: NetworkMode.Private, PrivateSegment: null }))
            return false;
        return Templates.Select(item => item.Id).Distinct().Count() == Templates.Length &&
            Machines.Select(item => item.Id).Distinct().Count() == Machines.Length &&
            Templates.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Templates.Length &&
            Machines.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Machines.Length &&
            Templates.Select(item => item.Id).ToHashSet().SetEquals(Machines.Select(item => item.TemplateId));
    }

    internal string Fingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, JsonDefaults.Options)));

    internal static string TemplatePath(string directory, Guid id) => DiskPath(directory, "templates", id);
    internal static string MachinePath(string directory, Guid id) => DiskPath(directory, "machines", id);
    private static string DiskPath(string directory, string kind, Guid id) => Path.Combine(directory, kind, $"{id:N}.vhdx");
}

public sealed record LabBackupPreview(string Fingerprint, LabBackup Backup, long RequiredBytes, long? FreeBytes, int AlreadyRestored);
