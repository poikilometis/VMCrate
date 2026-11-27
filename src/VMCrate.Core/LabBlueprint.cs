using System.Text.Json;
using System.Text.Json.Serialization;

namespace VMCrate.Core;

public sealed record BlueprintMachine(string Name, string Template, int MemoryGiB, int CpuCount, string? Segment = null);

public sealed record LabBlueprint(int Version, string Id, BlueprintMachine[] Machines)
{
    private static readonly JsonSerializerOptions Options = new(JsonDefaults.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static LabBlueprint Read(string path)
    {
        var source = LocalPaths.RequireFile(path, ".json");
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 256 * 1024)
            throw new LabException("error.blueprint", Path.GetFileName(source));

        try
        {
            return JsonSerializer.Deserialize<LabBlueprint>(input, Options)
                ?? throw new LabException("error.blueprint");
        }
        catch (JsonException error)
        {
            throw new LabException("error.blueprint", error.Message, error);
        }
    }
}

public sealed record BlueprintVmPreview(
    string Name, string Template, int MemoryGiB, int CpuCount, string? Segment, bool AlreadyCreated);

public sealed record BlueprintPreview(IReadOnlyList<BlueprintVmPreview> Machines);
