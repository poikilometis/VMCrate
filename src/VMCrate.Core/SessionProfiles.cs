using System.Text.Json;

namespace VMCrate.Core;

public sealed record SessionProfile(Guid Id, string Name, Guid TemplateId, int MemoryGiB, int CpuCount);

internal sealed partial class StateStore
{
    private string ProfilesPath => Path.Combine(root, "session-profiles.json");

    internal List<SessionProfile> LoadProfiles()
    {
        var path = ProfilesPath;
        LocalPaths.RequireOrdinaryPath(path);
        if (!File.Exists(path))
            return [];

        try
        {
            using var input = File.OpenRead(path);
            var data = JsonSerializer.Deserialize<ProfileCatalog>(input, JsonDefaults.Options);
            if (data is not { Version: 1, Profiles: not null } ||
                data.Profiles.Any(item => item is null || item.Id == Guid.Empty || item.TemplateId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(item.Name) || item.MemoryGiB is < 2 or > 128 || item.CpuCount is < 1 or > 32) ||
                data.Profiles.Select(item => item.Id).Distinct().Count() != data.Profiles.Count)
                throw new LabException("error.state", path);
            return data.Profiles;
        }
        catch (JsonException error) { throw new LabException("error.state", path, error); }
    }

    internal void SaveProfiles(List<SessionProfile> profiles)
    {
        var path = ProfilesPath;
        LocalPaths.RequireOrdinaryPath(path);
        WriteState(new ProfileCatalog(1, profiles), path, backup: null, overwrite: true);
    }

    private sealed record ProfileCatalog(int Version, List<SessionProfile> Profiles);
}

public sealed partial class LabService
{
    public IReadOnlyList<SessionProfile> ReadSessionProfiles()
    {
        using var workspaceLock = store.Lock();
        return store.LoadProfiles().OrderBy(profile => profile.Name).ToArray();
    }

    public SessionProfile SaveSessionProfile(Guid templateId, MachineOptions options)
    {
        using var workspaceLock = store.Lock();
        var template = FindTemplate(store.Load(), templateId);
        RequirePublishedTemplate(template);
        LocalPaths.RequireFile(store.TemplateDisk(template.Id), ".vhdx");
        var profiles = store.LoadProfiles();
        ValidateResources(options.MemoryGiB, options.CpuCount);
        var profile = new SessionProfile(Guid.NewGuid(), ValidateName(options.Name, profiles.Select(item => item.Name)),
            template.Id, options.MemoryGiB, options.CpuCount);
        profiles.Add(profile);
        store.SaveProfiles(profiles);
        return profile;
    }

    public void DeleteSessionProfile(Guid id)
    {
        using var workspaceLock = store.Lock();
        var profiles = store.LoadProfiles();
        if (profiles.RemoveAll(item => item.Id == id) == 1)
            store.SaveProfiles(profiles);
    }
}
