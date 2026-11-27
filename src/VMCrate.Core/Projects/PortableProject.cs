using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace VMCrate.Core;

public sealed record ProjectDisk(string Name, GuestFamily Guest, bool SecureBoot, string? Revision,
    string Sha256, long Bytes, bool Included);
public sealed record ProjectDiskPreview(ProjectDisk Disk, string? LocalName);
public sealed record ProjectPreview(LabBlueprint Blueprint, ProjectDiskPreview[] Disks, string Fingerprint)
{
    public bool Ready => Disks.All(item => item.LocalName is not null || item.Disk.Included);
    public long ImportBytes => Disks.Where(item => item.LocalName is null && item.Disk.Included).Sum(item => item.Disk.Bytes);
}

internal sealed record ProjectManifest(int Version, LabBlueprint Blueprint, ProjectDisk[] Disks)
{
    internal const string Entry = "project.json";
    internal static string DiskEntry(ProjectDisk disk) => $"templates/{disk.Sha256.ToLowerInvariant()}.vhdx";
    internal string Fingerprint() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, JsonDefaults.Options)));

    internal static ProjectManifest Read(ZipArchive archive)
    {
        if (archive.Entries.Count is < 1 or > 65 ||
            archive.Entries.Select(item => item.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count ||
            archive.GetEntry(Entry) is not { Length: > 0 and <= 512 * 1024 } manifest)
            throw new LabException("error.projectFormat");

        try
        {
            using var input = manifest.Open();
            var project = JsonSerializer.Deserialize<ProjectManifest>(input, AssistantJson.Strict);
            if (project is null || project.Version != 1 || project.Blueprint is null ||
                project.Disks is not { Length: >= 1 and <= 64 })
                throw new LabException("error.projectFormat");

            LabService.ValidateBlueprintShape(project.Blueprint);
            project.ValidateDisks(archive);
            return project;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw new LabException("error.projectFormat", inner: error);
        }
    }

    private void ValidateDisks(ZipArchive archive)
    {
        var names = Blueprint.Machines.Select(machine => machine.Template).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = new HashSet<string>(StringComparer.Ordinal) { Entry };
        foreach (var disk in Disks)
        {
            if (disk is null || !names.Remove(disk.Name) || !LabService.ValidProjectName(disk.Name) ||
                (!string.IsNullOrEmpty(disk.Revision) && !LabService.ValidProjectName(disk.Revision)) ||
                !Enum.IsDefined(disk.Guest) || disk.Bytes is <= 0 or > (2L << 40) ||
                disk.Sha256 is not { Length: 64 } || !disk.Sha256.All(char.IsAsciiHexDigit))
                throw new LabException("error.projectFormat");
            if (!disk.Included)
                continue;
            var path = DiskEntry(disk);
            if (archive.GetEntry(path)?.Length != disk.Bytes)
                throw new LabException("error.projectFormat");
            expected.Add(path);
        }
        if (names.Count != 0 || !expected.SetEquals(archive.Entries.Select(entry => entry.FullName)))
            throw new LabException("error.projectFormat");
    }
}
