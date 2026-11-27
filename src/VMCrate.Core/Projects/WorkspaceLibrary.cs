namespace VMCrate.Core;

public sealed record LibraryCopyPlan(string Destination, int Templates, int Images, int Profiles, long Bytes);

public sealed partial class LabService
{
    public LibraryCopyPlan PreviewLibraryCopy(string destination)
    {
        using var workspaceLock = store.Lock();
        var target = GetLibraryTarget(destination);
        var state = store.Load();
        var files = ReadLibraryFiles(state);
        return new(target, files.Count(item => item.TemplateId is not null), files.Count(item => item.TemplateId is null),
            store.LoadProfiles().Count, files.Sum(item => new FileInfo(item.Source).Length));
    }

    public async Task CopyLibraryAsync(LibraryCopyPlan preview, CancellationToken cancellation = default)
    {
        using var workspaceLock = store.Lock();
        var target = GetLibraryTarget(preview.Destination);
        var parent = Path.GetDirectoryName(target)!;
        var state = store.Load();
        var files = ReadLibraryFiles(state);
        var profiles = store.LoadProfiles();
        var bytes = files.Sum(item => new FileInfo(item.Source).Length);
        if (files.Count(item => item.TemplateId is not null) != preview.Templates || files.Count(item => item.TemplateId is null) != preview.Images ||
            profiles.Count != preview.Profiles || bytes != preview.Bytes)
            throw new LabException("error.projectChanged");
        WorkspaceCapacity.RequireSpace(target, bytes);

        var id = Guid.NewGuid();
        var stage = LocalPaths.MakeDirectory(Path.Combine(parent, id.ToString("N")));
        var result = new StateStore(stage);
        var templates = new List<Template>();
        var manifestFiles = new List<LibraryKitFile>();
        try
        {
            foreach (var file in files)
            {
                var path = Path.Combine(stage, file.RelativePath);
                LocalPaths.MakeDirectory(Path.GetDirectoryName(path)!);
                var hash = await FileDigests.CopyAndHashAsync(file.Source, path, cancellation: cancellation);
                if (file.Hash is not null && !file.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new LabException("error.projectIntegrity");
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                manifestFiles.Add(new(file.RelativePath.Replace(Path.DirectorySeparatorChar, '/'), new FileInfo(path).Length, hash));
                if (file.TemplateId is Guid template)
                    templates.Add(FindTemplate(state, template) with { Sha256 = hash });
            }

            result.Save(new LabState(Guid.NewGuid(), LabState.CurrentVersion, templates, []));
            result.SaveProfiles(profiles.Where(profile => templates.Any(template => template.Id == profile.TemplateId)).ToList());
            await OfflineLibraryManifest.CopyResourceAsync(stage, "recipes.json", cancellation);
            await OfflineLibraryManifest.CopyResourceAsync(stage, "operations.md", cancellation);
            await OfflineLibraryManifest.CreateAsync(stage, manifestFiles, cancellation);
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
        }
        finally { LocalPaths.RemoveDirectory(parent, id); }
    }

    private string GetLibraryTarget(string destination)
    {
        var target = Path.TrimEndingDirectorySeparator(LocalPaths.FullPath(destination));
        LocalPaths.RequireOrdinaryPath(target);
        var parent = Path.GetDirectoryName(target);
        var prefix = Path.TrimEndingDirectorySeparator(store.Root) + Path.DirectorySeparatorChar;
        if (parent is null || !Directory.Exists(parent) || Path.Exists(target) ||
            target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.workspaceFolder", target);
        return target;
    }

    private LibraryFile[] ReadLibraryFiles(LabState state)
    {
        var templates = state.Templates.Where(template => template.Published).Select(template => new LibraryFile(
            LocalPaths.RequireFile(store.TemplateDisk(template.Id), ".vhdx"), Path.Combine("templates", template.Id.ToString("N"), "base.vhdx"),
            template.Sha256, template.Id));
        var media = ListMedia(store.Media, ".iso").Where(item => IsSha256(Path.GetFileNameWithoutExtension(item.Name)))
            .Select(item => new LibraryFile(item.FullName, Path.Combine("media", item.Name), Path.GetFileNameWithoutExtension(item.Name), null));
        return templates.Concat(media).ToArray();
    }

    private sealed record LibraryFile(string Source, string RelativePath, string? Hash, Guid? TemplateId);
}
