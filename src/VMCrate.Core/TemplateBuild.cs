namespace VMCrate.Core;

public enum BuildStage { Verify, Provision, Install, Capture, Complete, BootTest }

public sealed record TemplateBuildOptions(string Name, string? Revision, InstallerOptions Installer,
    string Hash, string? AnswerFile, Guid? PreviousTemplateId = null, RevisionLink? Lineage = null,
    MediaProvenance? Media = null, string? BootSeedHash = null, bool VerifyCloneOnPublish = false,
    bool RevisionTrial = false);

public sealed record BuildEvent(DateTimeOffset TimeUtc, BuildStage Stage, string? ErrorKey = null, long? ElapsedMs = null);
public sealed record TemplateBuild(Guid Id, TemplateBuildOptions Options, BuildStage Stage, BuildEvent[] History)
{
    public BuildProxyObservation[] ProxyChecks { get; init; } = [];
}

public sealed partial class LabService
{
    public IReadOnlyList<TemplateBuild> ReadBuilds()
    {
        using var workspaceLock = store.Lock();
        return store.Load().Builds.ToArray();
    }

    public void ForgetBuild(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var job = state.Builds.SingleOrDefault(item => item.Id == id) ?? throw new LabException("error.build");
        if (job.Stage != BuildStage.Complete && state.Machines.Any(machine => machine.Id == id))
            throw new LabException("error.inUse");
        if (state.Templates.Any(template => template.Id == job.Id && !template.Published))
            throw new LabException("error.inUse");
        var partial = Path.Combine(store.Media, $"{id:N}.copying");
        LocalPaths.RequireOrdinaryPath(partial);
        if (File.Exists(partial))
            File.Delete(partial);
        state.Builds.Remove(job);
        store.Save(state);
    }

    public TemplateBuild QueueBuild(TemplateBuildOptions options)
    {
        using var workspaceLock = store.Lock();
        if (options.RevisionTrial)
            throw new LabException("error.revision");
        return QueueBuild(store.Load(), options);
    }

    public TemplateBuild QueueRevisionTrial(Guid sourceId, string name, string revision, MachineOptions resources)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var source = FindTemplate(state, sourceId);
        RequirePublishedTemplate(source);
        if (source.Sha256 is not { } hash || !IsSha256(hash))
            throw new LabException("error.projectIntegrity", source.Name);

        return QueueBuild(state, new(name, revision, new(resources, source.Guest, source.SecureBoot, "", 0),
            hash, null, sourceId, Media: source.Media, VerifyCloneOnPublish: true, RevisionTrial: true));
    }

    private TemplateBuild QueueBuild(LabState state, TemplateBuildOptions options)
    {
        var name = ValidateName(options.Name,
            state.Templates.Select(template => template.Name).Concat(state.Builds.Select(job => job.Options.Name)));
        ValidateResources(options.Installer.Machine.MemoryGiB, options.Installer.Machine.CpuCount);
        if (!options.RevisionTrial && options.Installer.DiskGiB is < 16 or > 2048)
            throw new LabException("error.diskSize");

        var hash = options.Hash.Trim().ToLowerInvariant();
        if (!IsSha256(hash))
            throw new LabException("error.isoHash");
        var id = Guid.NewGuid();
        var iso = options.RevisionTrial ? "" : LocalPaths.RequireFile(options.Installer.IsoPath, ".iso");
        var revision = string.IsNullOrWhiteSpace(options.Revision) ? null : ValidateName(options.Revision, []);
        RevisionLink? lineage = null;
        if (options.PreviousTemplateId is Guid previousId)
        {
            var parent = FindTemplate(state, previousId);
            if (revision is null || parent.Guest != options.Installer.Guest)
                throw new LabException("error.revision");
            lineage = new(parent.Lineage?.SeriesId ?? parent.Id, parent.Id);
            if (state.Templates.Any(template => (template.Lineage?.SeriesId ?? template.Id) == lineage.SeriesId &&
                    string.Equals(template.Revision, revision, StringComparison.OrdinalIgnoreCase)) ||
                state.Builds.Any(job => job.Options.Lineage?.SeriesId == lineage.SeriesId &&
                    string.Equals(job.Options.Revision, revision, StringComparison.OrdinalIgnoreCase)))
                throw new LabException("error.revision");
        }
        var answer = CheckAnswerFile(options);
        var seedHash = answer is not null && options.Installer.Guest == GuestFamily.Linux
            ? options.BootSeedHash!.Trim().ToLowerInvariant() : null;
        var machineName = $"{name[..Math.Min(48, name.Length)]} · {id.ToString("N")[..8]}";
        var installer = options.Installer with { IsoPath = iso, Machine = options.Installer.Machine with { Name = machineName } };
        var stage = options.RevisionTrial ? BuildStage.Provision : BuildStage.Verify;
        var normalized = options with { Name = name, Revision = revision, Installer = installer, Hash = hash,
            AnswerFile = answer, Lineage = lineage, BootSeedHash = seedHash };
        var build = new TemplateBuild(id, normalized, stage, [new BuildEvent(DateTimeOffset.UtcNow, stage)]);
        state.Builds.Add(build);
        store.Save(state);
        return build;
    }

    private static string? CheckAnswerFile(TemplateBuildOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AnswerFile))
            return null;
        if (options.Installer.Guest == GuestFamily.Linux)
        {
            if (options.Installer.SecureBoot || options.BootSeedHash?.Trim() is not { } hash || !IsSha256(hash))
                throw new LabException("error.bootSeed");
            return LocalPaths.RequireFile(options.AnswerFile, ".iso");
        }

        var file = LocalPaths.RequireFile(options.AnswerFile, ".xml");
        if (!Path.GetFileName(file).Equals("Autounattend.xml", StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.answerFile");
        return file;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
}
