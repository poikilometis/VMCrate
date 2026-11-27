using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using VMCrate.Core;

namespace VMCrate.Host;

internal sealed record RecipeSettings(string Id, string Pins);
internal sealed record AccountSettings(string Id, string ClientCertificateSha256, string Workspace, RecipeSettings[] Recipes);
internal sealed record HostSettings(int Version, string ListenAddress, int Port, string ServerCertificateSha256,
    string QueueDirectory, string RecipeCatalog, AccountSettings[] Accounts);

internal sealed record AllowedRecipe(RemoteRecipe Summary, LabBlueprint Blueprint, BlueprintLock Pins, string Fingerprint);
internal sealed record QueueAccount(string Id, string ClientCertificateSha256, string Workspace,
    IReadOnlyDictionary<string, AllowedRecipe> Recipes);

internal sealed record QueueSettings(IPAddress ListenAddress, int Port, string CertificateSha256, string QueueDirectory,
    QueueAccount[] Accounts)
{
    internal static QueueSettings Read(string path)
    {
        var config = LocalPaths.RequireFile(path, ".json");
        using var input = File.OpenRead(config);
        if (input.Length > 128 * 1024)
            throw Error();
        var settings = JsonSerializer.Deserialize<HostSettings>(input, RemoteProtocol.Json);
        if (settings is not { Version: 1, Port: >= 1 and <= 65535, Accounts.Length: >= 1 and <= 64 } ||
            !IPAddress.TryParse(settings.ListenAddress, out var address) || !PrivateAddress(address) ||
            !CertificatePins.IsSha256(settings.ServerCertificateSha256))
            throw Error();
        var baseDirectory = Path.GetDirectoryName(config)!;
        string Resolve(string value) => !string.IsNullOrWhiteSpace(value)
            ? LocalPaths.FullPath(Path.GetFullPath(value, baseDirectory)) : throw Error();

        var directory = WorkspaceVolume.Inspect(Resolve(settings.QueueDirectory)).Directory;
        var catalog = RecipeCatalog.Read(Resolve(settings.RecipeCatalog)).Where(recipe => recipe.Blueprint is not null)
            .ToDictionary(recipe => recipe.Id, StringComparer.Ordinal);
        var accounts = settings.Accounts.Select(account => ReadAccount(account, catalog, Resolve)).ToArray();
        if (accounts.Select(account => account.Id).Distinct(StringComparer.Ordinal).Count() != accounts.Length ||
            accounts.Select(account => account.ClientCertificateSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != accounts.Length ||
            accounts.Select(account => account.Workspace).Append(directory).AnyOverlappingDirectory())
            throw Error();

        return new(address, settings.Port, settings.ServerCertificateSha256, directory, accounts);
    }

    internal QueueAccount? ForCertificate(X509Certificate2? certificate)
    {
        if (certificate is null || !CertificatePins.ActiveFor(certificate, CertificatePins.ClientAuthentication))
            return null;
        var hash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        return Accounts.SingleOrDefault(account => account.ClientCertificateSha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    private static QueueAccount ReadAccount(AccountSettings? item, IReadOnlyDictionary<string, LabRecipe> catalog, Func<string, string> resolve)
    {
        if (item is not { Id.Length: >= 1 and <= 32, Recipes.Length: >= 1 and <= 32 } ||
            item.Id.Any(c => !(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) ||
            !CertificatePins.IsSha256(item.ClientCertificateSha256))
            throw Error();
        var workspace = WorkspaceVolume.Inspect(resolve(item.Workspace));
        if (!workspace.ExistingCatalog || item.Recipes.Any(recipe => recipe?.Id is not { Length: >= 1 and <= 64 }) ||
            item.Recipes.Select(recipe => recipe.Id).Distinct(StringComparer.Ordinal).Count() != item.Recipes.Length)
            throw Error();
        var recipes = item.Recipes.ToDictionary(definition => definition.Id, definition =>
        {
            if (!catalog.TryGetValue(definition.Id, out var recipe) || recipe.Blueprint is not { } blueprint ||
                recipe.MinimumVmCrate is not { } minimum || Version.Parse(minimum) > typeof(LabService).Assembly.GetName().Version)
                throw Error();
            var pins = OfflinePlanning.ReadLock(resolve(definition.Pins));
            OfflinePlanning.ValidateLock(blueprint, pins);
            var binding = new { Workspace = workspace.Directory.ToUpperInvariant(), recipe.Id, recipe.Version, Pins = pins };
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(binding, JsonDefaults.Options)));
            return new AllowedRecipe(new(recipe.Id, recipe.Name, recipe.Version, OfflinePlanning.Validate(blueprint)), blueprint, pins, fingerprint);
        }, StringComparer.Ordinal);
        if (recipes.Values.Select(recipe => recipe.Blueprint.Id).Distinct(StringComparer.Ordinal).Count() != recipes.Count)
            throw Error();
        return new(item.Id, item.ClientCertificateSha256, workspace.Directory, recipes);
    }

    private static bool PrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;
        var bytes = address.GetAddressBytes();
        return bytes is [10, _, _, _] or [192, 168, _, _] or [172, >= 16 and <= 31, _, _];
    }

    private static LabException Error() => new("error.remoteConfig");
}

internal static class QueueDirectories
{
    internal static bool AnyOverlappingDirectory(this IEnumerable<string> directories)
    {
        var paths = directories.Select(path => Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return paths.Zip(paths.Skip(1)).Any(pair => pair.Second.StartsWith(pair.First, StringComparison.OrdinalIgnoreCase));
    }
}
