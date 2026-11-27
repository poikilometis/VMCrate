using System.Text.Json;

namespace VMCrate.Core;

public sealed record LabRecipe(string Id, string Name, string Author, string License, int Version, string MinimumVmCrate,
    string[] Sources, LabBlueprint? Blueprint, string? PublisherId = null, bool PrivateUpdates = false);

public static class RecipeCatalog
{
    public static IReadOnlyList<LabRecipe> Read(string path)
    {
        using var stream = File.OpenRead(LocalPaths.RequireFile(path, ".json"));
        if (stream.Length > 256 * 1024)
            throw new LabException("error.projectFormat");
        try
        {
            var recipes = JsonSerializer.Deserialize<LabRecipe[]>(stream, AssistantJson.Strict);
            if (recipes is not { Length: > 0 and <= 64 } ||
                recipes.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) ||
                    string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Author) ||
                    string.IsNullOrWhiteSpace(item.License) || item.Version < 1 ||
                    !Version.TryParse(item.MinimumVmCrate, out _) || item.Sources is not { Length: > 0 and <= 8 } ||
                    item.Sources.Any(source => !Uri.TryCreate(source, UriKind.Absolute, out var uri) || !CatalogTransport.SafeUri(uri)) ||
                    (item.Blueprint is null) == string.IsNullOrWhiteSpace(item.PublisherId)) ||
                recipes.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != recipes.Length)
                throw new LabException("error.projectFormat");

            foreach (var recipe in recipes)
            {
                if (recipe.Blueprint is { } blueprint)
                    _ = OfflinePlanning.Validate(blueprint);
                else if (!OfficialImages.Sources.Any(source => source.Id == recipe.PublisherId))
                    throw new LabException("error.imageSource");
            }
            return Array.AsReadOnly(recipes);
        }
        catch (JsonException error) { throw new LabException("error.projectFormat", inner: error); }
    }
}
