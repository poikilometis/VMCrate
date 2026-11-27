using System.Text.Json;

namespace VMCrate.Core;

internal sealed record LabState(Guid WorkspaceId, int Version, List<Template> Templates, List<Machine> Machines)
{
    internal const int CurrentVersion = 7;
    public List<DiskRecoveryPoint> RecoveryPoints { get; init; } = [];
    public PendingDiskRestore? DiskRestore { get; set; }
    public List<OperationEntry> Operations { get; init; } = [];
    public List<AnalysisReport> ArchivedSessions { get; init; } = [];
    public List<PendingProvision> Provisions { get; init; } = [];
    public List<TemplateBuild> Builds { get; init; } = [];
}

internal sealed partial class StateStore
{
    private readonly string root;
    private readonly string statePath;
    private readonly string lockPath;

    internal StateStore(string root)
    {
        this.root = LocalPaths.FullPath(root);
        statePath = Path.Combine(this.root, "state.json");
        lockPath = Path.Combine(this.root, "workspace.lock");
    }

    internal FileStream Lock()
    {
        LocalPaths.MakeDirectory(root);
        LocalPaths.RequireOrdinaryPath(lockPath);
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        {
            throw new LabException("error.busy", lockPath, ex);
        }
    }

    internal LabState Load()
    {
        LocalPaths.RequireOrdinaryPath(statePath);
        if (File.Exists(statePath))
            return Read(statePath);
        LocalPaths.RequireOrdinaryPath(BackupPath);
        if (File.Exists(BackupPath))
            throw new LabException("error.state", statePath);

        return new LabState(Guid.NewGuid(), LabState.CurrentVersion, [], []);
    }

    private static LabState Read(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Any(property =>
                property.Name.Equals("version", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt32(out var version) && version > LabState.CurrentVersion))
                throw new LabException("error.stateVersion", path);
            var state = root.Deserialize<LabState>(JsonDefaults.Options);
            if (state is null || state.WorkspaceId == Guid.Empty || state.Version is < 1 ||
                state.Templates is null || state.Machines is null || state.Builds is null ||
                state.Builds.Any(build => build is null || build.Id == Guid.Empty || build.Options is null || build.History is null) ||
                state.Builds.Select(build => build.Id).Distinct().Count() != state.Builds.Count ||
                state.Templates.Any(template => template is null || template.Id == Guid.Empty) ||
                state.Machines.Any(machine => machine is null || machine.Id == Guid.Empty ||
                    (machine.SessionStarted && !machine.Disposable) ||
                    (machine.Network == NetworkMode.Updates && (machine.Proxy is null || machine.TemplateId is not null ||
                        machine.BlueprintId is not null || machine.PrivateSegment is not null)) ||
                    (machine.Network != NetworkMode.Updates && machine.Proxy is not null) ||
                    (machine.Disposable && (machine.TemplateId is null || machine.Network != NetworkMode.Offline ||
                        machine.BlueprintId is not null || machine.PrivateSegment is not null))) ||
                state.Templates.Select(template => template.Id).Distinct().Count() != state.Templates.Count ||
                state.Machines.Select(machine => machine.Id).Distinct().Count() != state.Machines.Count ||
                state.Machines.Any(machine => machine.TransferId == Guid.Empty) ||
                state.Machines.Where(machine => machine.TransferId is not null).GroupBy(machine => machine.TransferId).Any(group => group.Skip(1).Any()) ||
                state.Machines.Any(machine => machine.TemplateId is Guid id && state.Templates.All(t => t.Id != id)) ||
                state.Operations is null || state.Operations.Count > 512 ||
                state.Operations.Any(item => item is null || item.Resource == Guid.Empty) ||
                state.ArchivedSessions is null || state.ArchivedSessions.Count > 64 ||
                state.ArchivedSessions.Any(report => report?.Machine is null || report.Files is null) ||
                !RecoveryPointCatalog.Valid(state) ||
                state.Provisions is null ||
                state.Provisions.Any(item => item is null || !state.Machines.Any(item.Matches)) ||
                state.Provisions.Select(item => item.Id).Distinct().Count() != state.Provisions.Count)
                throw new LabException("error.state", path);
            return state with { Version = LabState.CurrentVersion };
        }
        catch (JsonException ex)
        {
            throw new LabException("error.state", path, ex);
        }
    }

    internal void Save(LabState state)
    {
        LocalPaths.RequireOrdinaryPath(statePath);
        LocalPaths.RequireOrdinaryPath(BackupPath);
        WriteState(state, statePath, File.Exists(statePath) ? BackupPath : null);
    }

    private void WriteState<T>(T state, string destination, string? backup, bool overwrite = false)
    {
        var temporary = Path.Combine(root, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, state, JsonDefaults.Options);
                output.Flush(flushToDisk: true);
            }
            if (backup is null)
                File.Move(temporary, destination, overwrite);
            else
                File.Replace(temporary, destination, backup, ignoreMetadataErrors: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal string Root => root;
    internal string Media => Path.Combine(root, "media");
    internal string InstallerMedia(string hash)
    {
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
            throw new LabException("error.isoHash");
        return Path.Combine(Media, hash.ToLowerInvariant() + ".iso");
    }

    internal string Templates => Path.Combine(root, "templates");
    internal string Machines => Path.Combine(root, "machines");
    internal string TemplateDisk(Guid id) => Path.Combine(Templates, id.ToString("N"), "base.vhdx");

    internal MachineLocation Location(LabState state, Machine machine) =>
        new(machine, state.WorkspaceId, Path.Combine(Machines, machine.Id.ToString("N")),
            machine.TemplateId is Guid templateId ? TemplateDisk(templateId) : null);
}
