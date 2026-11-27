using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace VMCrate.Core;

public sealed record BlueprintResources(int Machines, int MemoryGiB, int Processors, int PrivateSegments);
public sealed record PinnedTemplate(string Name, string? Revision, string Sha256, long Bytes);
public sealed record BlueprintLock(int Version, string BlueprintSha256, PinnedTemplate[] Templates);
public sealed record OfflineTemplate(string Name, string? Revision, string? Sha256, long? Bytes, string Status);
public sealed record OfflineBlueprintPlan(string Id, BlueprintResources Resources, OfflineTemplate[] Templates,
    string[] NameConflicts)
{
    public bool Ready => NameConflicts.Length == 0 && Templates.All(template => template.Status == "Available");
}

public sealed record OfflineProjectCheck(string Id, BlueprintResources Resources, ProjectDisk[] Dependencies,
    long VerifiedBytes)
{
    public bool SelfContained => Dependencies.All(item => item.Included);
}

public static class OfflinePlanning
{
    public static BlueprintResources Validate(LabBlueprint blueprint)
    {
        LabService.ValidateBlueprintShape(blueprint);
        return new(blueprint.Machines.Length, blueprint.Machines.Sum(vm => vm.MemoryGiB),
            blueprint.Machines.Sum(vm => vm.CpuCount), blueprint.Machines.Select(vm => vm.Segment).OfType<string>().Distinct().Count());
    }

    public static async Task<OfflineBlueprintPlan> PlanAsync(string workspace, LabBlueprint blueprint, BlueprintLock? pins = null,
        CancellationToken cancellation = default)
    {
        var resources = Validate(blueprint);
        var names = blueprint.Machines.Select(vm => vm.Template).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (pins is not null)
            RequireMatchingLock(blueprint, names, pins);

        var root = WorkspaceVolume.Inspect(workspace);
        if (!root.ExistingCatalog)
            return new(blueprint.Id, resources, names.Select(name => new OfflineTemplate(name, null, null, null, "Missing")).ToArray(), []);

        var store = new StateStore(root.Directory);
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var templates = new List<OfflineTemplate>();
        foreach (var name in names)
        {
            var template = state.Templates.SingleOrDefault(item => item.Published && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var path = template is null ? null : store.TemplateDisk(template.Id);
            if (template is null || !File.Exists(path))
            {
                templates.Add(new(name, null, null, null, "Missing"));
                continue;
            }

            var evidence = await FileDigests.InspectAsync(LocalPaths.RequireFile(path!, ".vhdx"), cancellation);
            var expected = pins?.Templates.Single(item => name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
            var matches = (template.Sha256 is null || template.Sha256.Equals(evidence.Sha256, StringComparison.OrdinalIgnoreCase)) &&
                (expected is null || (expected.Sha256.Equals(evidence.Sha256, StringComparison.OrdinalIgnoreCase) &&
                    expected.Bytes == evidence.SizeBytes && expected.Revision == template.Revision));
            templates.Add(new(name, template.Revision, evidence.Sha256, evidence.SizeBytes, matches ? "Available" : "Mismatch"));
        }

        var collisions = blueprint.Machines.Where(spec =>
        {
            var name = spec.Name.Trim();
            var existing = state.Machines.SingleOrDefault(vm => vm.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var template = state.Templates.SingleOrDefault(item => item.Published &&
                item.Name.Equals(spec.Template, StringComparison.OrdinalIgnoreCase));
            return existing is not null && (template is null ||
                LabService.DescribeBlueprintMachine(state.WorkspaceId, blueprint.Id, spec, template, existing.Id) !=
                (existing with { Name = name }));
        }).Select(spec => spec.Name.Trim()).ToArray();
        return new(blueprint.Id, resources, templates.ToArray(), collisions);
    }

    public static async Task SaveLockAsync(string path, string workspace, LabBlueprint blueprint,
        CancellationToken cancellation = default)
    {
        var plan = await PlanAsync(workspace, blueprint, cancellation: cancellation);
        var pins = Pin(blueprint, plan);
        await OutputFile.WriteNewAsync(path, ".json", stream => JsonSerializer.SerializeAsync(stream, pins, JsonDefaults.Options, cancellation));
    }

    public static BlueprintLock Pin(LabBlueprint blueprint, OfflineBlueprintPlan plan)
    {
        if (!plan.Ready || plan.Id != blueprint.Id)
            throw new LabException("error.projectIntegrity");
        var pins = new BlueprintLock(1, Fingerprint(blueprint),
            plan.Templates.Select(item => new PinnedTemplate(item.Name, item.Revision, item.Sha256!, item.Bytes!.Value)).ToArray());
        ValidateLock(blueprint, pins);
        return pins;
    }

    public static void ValidateLock(LabBlueprint blueprint, BlueprintLock pins)
    {
        Validate(blueprint);
        RequireMatchingLock(blueprint, blueprint.Machines.Select(vm => vm.Template)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), pins);
    }

    public static BlueprintLock ReadLock(string path)
    {
        var file = LocalPaths.RequireFile(path, ".json");
        using var input = File.OpenRead(file);
        if (input.Length > 128 * 1024)
            throw new LabException("error.projectFormat");
        try
        {
            return JsonSerializer.Deserialize<BlueprintLock>(input, AssistantJson.Strict)
                ?? throw new LabException("error.projectFormat");
        }
        catch (JsonException error) { throw new LabException("error.projectFormat", inner: error); }
    }

    public static async Task<OfflineProjectCheck> CheckArchiveAsync(string path, CancellationToken cancellation = default)
    {
        using var zip = ZipFile.OpenRead(LocalPaths.RequireFile(path, ".zip"));
        var project = ProjectManifest.Read(zip);
        long bytes = 0;
        foreach (var disk in project.Disks.Where(item => item.Included).DistinctBy(item => item.Sha256.ToLowerInvariant()))
        {
            await using var input = zip.GetEntry(ProjectManifest.DiskEntry(disk))!.Open();
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellation));
            if (!actual.Equals(disk.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new LabException("error.projectIntegrity", disk.Name);
            bytes += disk.Bytes;
        }
        return new(project.Blueprint.Id, Validate(project.Blueprint), project.Disks, bytes);
    }

    private static string Fingerprint(LabBlueprint blueprint) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(blueprint, JsonDefaults.Options)));

    internal static void RequireMatchingLock(LabBlueprint blueprint, string[] names, BlueprintLock pins)
    {
        if (pins.Version != 1 || pins.BlueprintSha256 != Fingerprint(blueprint) ||
            pins.Templates is null || pins.Templates.Length != names.Length || pins.Templates.Any(item => item is null ||
                string.IsNullOrWhiteSpace(item.Name) || item.Sha256 is not { Length: 64 } ||
                !item.Sha256.All(char.IsAsciiHexDigit) || item.Bytes <= 0) ||
            !pins.Templates.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(names))
            throw new LabException("error.projectChanged");
    }
}
