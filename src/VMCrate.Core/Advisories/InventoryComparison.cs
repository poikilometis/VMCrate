namespace VMCrate.Core;

public sealed record PackageDifference(string Name, string[] Before, string[] After);

public static class InventoryComparison
{
    public static IReadOnlyList<PackageDifference> Compare(PackageInventory before, PackageInventory after)
    {
        before = before.Validated();
        after = after.Validated();
        if (!before.Ecosystem.Equals(after.Ecosystem, StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.inventory");

        var older = Versions(before);
        var newer = Versions(after);
        return older.Keys.Union(newer.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(name => new PackageDifference(name, older.GetValueOrDefault(name, []), newer.GetValueOrDefault(name, [])))
            .Where(item => !item.Before.SequenceEqual(item.After)).ToArray();
    }

    private static Dictionary<string, string[]> Versions(PackageInventory input) => input.Packages.GroupBy(item => item.Name,
        StringComparer.Ordinal).ToDictionary(group => group.Key,
        group => group.Select(item => item.Version).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
}
