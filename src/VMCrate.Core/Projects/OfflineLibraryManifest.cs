using System.Text.Json;
using System.Text.RegularExpressions;

namespace VMCrate.Core;

public sealed record LibraryKitFile(string File, long Bytes, string Sha256);
public sealed record LibraryKitManifest(int Version, LibraryKitFile[] Files);

public static class OfflineLibraryManifest
{
    private static readonly string[] Metadata = ["state.json", "session-profiles.json", "recipes.json", "operations.md"];

    internal static async Task CreateAsync(string root, List<LibraryKitFile> tracked, CancellationToken cancellation)
    {
        foreach (var file in Metadata)
        {
            var evidence = await FileDigests.InspectAsync(SafeFile(root, file), cancellation);
            tracked.Add(new(file, evidence.SizeBytes, evidence.Sha256));
        }
        await OutputFile.WriteNewAsync(Path.Combine(root, "kit.json"), ".json", stream =>
            JsonSerializer.SerializeAsync(stream, new LibraryKitManifest(1, tracked.ToArray()), JsonDefaults.Options, cancellation));
    }

    public static async Task<LibraryKitManifest> VerifyAsync(string root, CancellationToken cancellation = default)
    {
        var directory = LocalPaths.FullPath(root);
        using var source = File.OpenRead(LocalPaths.RequireFile(Path.Combine(directory, "kit.json"), ".json"));
        if (source.Length > 2 * 1024 * 1024)
            throw new LabException("error.projectFormat");
        LibraryKitManifest? manifest;
        try { manifest = await JsonSerializer.DeserializeAsync<LibraryKitManifest>(source, AssistantJson.Strict, cancellation); }
        catch (JsonException error) { throw new LabException("error.projectFormat", inner: error); }

        if (manifest is not { Version: 1, Files.Length: >= 4 } ||
            manifest.Files.Any(file => file is null || string.IsNullOrWhiteSpace(file.File) || file.Bytes < 0 ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit)) ||
            manifest.Files.Select(file => file.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Length)
            throw new LabException("error.projectFormat");

        var checkedFiles = manifest.Files.ToDictionary(file => file.File, StringComparer.Ordinal);
        if (Metadata.Any(file => !checkedFiles.ContainsKey(file)))
            throw new LabException("error.projectFormat");
        foreach (var file in manifest.Files)
        {
            var evidence = await FileDigests.InspectAsync(SafeFile(directory, file.File), cancellation);
            if (evidence.SizeBytes != file.Bytes || !evidence.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) ||
                (file.File.StartsWith("media/", StringComparison.Ordinal) &&
                 !Path.GetFileNameWithoutExtension(file.File).Equals(evidence.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new LabException("error.projectIntegrity", file.File);
        }

        var store = new StateStore(directory);
        var catalog = store.Load();
        if (catalog.Machines.Count != 0 || catalog.Templates.Any(template =>
            !checkedFiles.TryGetValue($"templates/{template.Id:N}/base.vhdx", out var file) || file.Sha256 != template.Sha256) ||
            store.LoadProfiles().Any(profile => catalog.Templates.All(template => template.Id != profile.TemplateId)))
            throw new LabException("error.projectFormat");
        _ = RecipeCatalog.Read(SafeFile(directory, "recipes.json"));
        return manifest;
    }

    private static string SafeFile(string root, string relative)
    {
        if (!Metadata.Contains(relative) &&
            !Regex.IsMatch(relative, @"\A(?:templates/[0-9a-f]{32}/base\.vhdx|media/[0-9A-Fa-f]{64}\.iso)\z"))
            throw new LabException("error.projectFormat");
        return LocalPaths.RequireFile(Path.Combine(root, relative));
    }

    internal static async Task CopyResourceAsync(string root, string file, CancellationToken cancellation)
    {
        using var source = typeof(OfflineLibraryManifest).Assembly.GetManifestResourceStream("VMCrate.Core." + file)
            ?? throw new LabException("error.projectFormat");
        await using var output = File.Open(Path.Combine(root, file), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(output, cancellation);
    }
}
