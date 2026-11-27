using System.Text.Json;

namespace VMCrate.Core;

public sealed record InventoryPackage(string Name, string Version);
public sealed record PackageInventory(int Version, string Ecosystem, InventoryPackage[] Packages)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SupportsOsv => !Ecosystem.Equals("WindowsCBS", StringComparison.OrdinalIgnoreCase) &&
        !Ecosystem.StartsWith("LinuxLocal:", StringComparison.OrdinalIgnoreCase);

    public static PackageInventory Read(string path)
    {
        var file = LocalPaths.RequireFile(path, ".json");
        if (new FileInfo(file).Length > 1024 * 1024)
            throw new LabException("error.inventory");
        try
        {
            using var input = File.OpenRead(file);
            return JsonSerializer.Deserialize<PackageInventory>(input, AssistantJson.Strict)?.Validated()
                ?? throw new LabException("error.inventory");
        }
        catch (JsonException error)
        {
            throw new LabException("error.inventory", inner: error);
        }
    }

    internal PackageInventory Validated()
    {
        if (Version != 1 || !ValidToken(Ecosystem, 64) || Packages is not { Length: >= 1 and <= 5000 } ||
            Packages.Any(item => item is null || !ValidToken(item.Name, 180) || !ValidToken(item.Version, 128)))
            throw new LabException("error.inventory");
        return this with { Packages = Packages.Distinct().ToArray() };
    }

    private static bool ValidToken(string? text, int maximum) => text is { Length: >= 1 } && text.Length <= maximum &&
        char.IsAscii(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or '~' or '^' or ':' or '/' or '@' or ' ');
}

public sealed record UpdateFinding(string Package, string Installed, string Advisory, bool KnownExploited,
    string[] PublishedFixes);

public sealed record UpdateAdvice(DateTimeOffset CheckedUtc, string Ecosystem, int CheckedPackages,
    bool KevAvailable, int DetailsChecked, int TotalAdvisories, UpdateFinding[] Findings)
{
    public AssistantFact[] Facts() => Findings.Take(44).Select((item, index) =>
            new AssistantFact($"advisory-{index + 1}", $"{Ecosystem} {item.Package}={item.Installed}; " +
                $"OSV {item.Advisory}; CISA KEV match={item.KnownExploited}; published fixed versions={string.Join(", ", item.PublishedFixes)}"))
        .Prepend(new AssistantFact("scope", $"Inventory checked at {CheckedUtc:u}; packages={CheckedPackages}; " +
            $"OSV details checked={DetailsChecked}/{TotalAdvisories}; KEV feed available={KevAvailable}")).ToArray();
}
