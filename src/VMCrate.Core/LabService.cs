namespace VMCrate.Core;

public sealed partial class LabService(string root, IHypervisor hypervisor, IInboxWriter inboxWriter)
{
    private readonly StateStore store = new(root);

    public async Task<LabSnapshot> ObserveAsync()
    {
        using var workspaceLock = store.Lock();
        return await ObserveStateAsync(store.Load());
    }

    private async Task<LabSnapshot> ObserveStateAsync(LabState state)
    {
        var locations = state.Machines.Select(machine => store.Location(state, machine)).ToArray();
        var templates = state.Templates.Where(template => template.Published).ToArray();
        var pending = state.DiskRestore is not null || state.Provisions.Count != 0 || state.Builds.Any(build => build.Stage != BuildStage.Complete);
        try
        {
            var observed = await hypervisor.ObserveAsync(locations);
            var needsReview = observed.Machines.Any(vm => vm.State == PowerState.Missing ||
                vm.Findings.Contains("audit.network") || vm.Findings.Contains("audit.disk"));
            return new LabSnapshot(templates, state.Machines, observed, null, RecoverySuggested: pending || needsReview);
        }
        catch (LabException error)
        {
            return new LabSnapshot(templates, state.Machines, null, error, RecoverySuggested: pending);
        }
    }

    public Task PowerOffAsync(Guid id) => WithMachineAsync(id, hypervisor.PowerOffAsync, RecordedOperation.PowerOff);
    public Task OpenConsoleAsync(Guid id) => WithMachineAsync(id, hypervisor.OpenConsoleAsync);
    public Task EjectInstallerAsync(Guid id) => WithMachineAsync(id, hypervisor.EjectInstallerAsync);
    public Task EjectInboxAsync(Guid id) => WithMachineAsync(id, vm => hypervisor.SetInboxAsync(vm, null), RecordedOperation.EjectInbox);

    public Task ResetDiskAsync(Guid id) => WithMachineAsync(id, ResetDiskCoreAsync, RecordedOperation.Reset);

    private async Task ResetDiskCoreAsync(MachineLocation vm)
    {
        if (vm.Machine.Disposable)
            throw new LabException("error.sessionEnded");
        if (vm.BaseDisk is null)
            throw new LabException("error.noTemplate");

        LocalPaths.RequireFile(vm.BaseDisk, ".vhdx");
        LocalPaths.MakeDirectory(vm.DiskDirectory);
        await hypervisor.ResetDiskAsync(vm, vm.NewDiskPath());
    }

    public async Task SetNetworkAsync(Guid id, NetworkMode network)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var machine = FindMachine(state, id);
        if (machine.Disposable && network != NetworkMode.Offline)
            throw new LabException("error.sessionOffline");
        if (network == NetworkMode.Updates || machine.Network == NetworkMode.Updates)
            throw new LabException("error.updateBuilder");
        RequireNoDiskRestore(state, id);
        if (!Enum.IsDefined(network) || (machine.BlueprintId is not null && machine.Network != network))
            throw new LabException("error.blueprintConflict", machine.Name);

        // A successful host change followed by a failed save is detected before the next boot.
        await ApplyNetworkAsync(state, machine, machine with { Network = network });
    }

    public async Task DeleteMachineAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var machine = FindMachine(state, id);
        var location = store.Location(state, machine);
        await DeleteMachineCoreAsync(state, location);
    }

    public Task SendFilesAsync(Guid id, IReadOnlyList<string> files) =>
        WithMachineAsync(id, vm => SendFilesCoreAsync(vm, files), RecordedOperation.Inbox);

    private async Task<FileEvidence[]> SendFilesCoreAsync(MachineLocation vm, IReadOnlyList<string> files, bool record = false)
    {
        var sources = ValidateInboxFiles(files);

        var directory = LocalPaths.MakeDirectory(vm.InboxDirectory);
        var mediaId = Guid.NewGuid();
        var stage = LocalPaths.MakeDirectory(Path.Combine(directory, mediaId.ToString("N")));
        var iso = Path.Combine(directory, $"{mediaId:N}.iso");
        var copied = new List<FileEvidence>();
        try
        {
            inboxWriter.Write(sources, stage, iso);
            if (record)
            {
                foreach (var source in sources)
                    copied.Add(await FileDigests.InspectAsync(Path.Combine(stage, Path.GetFileName(source))));
            }
        }
        catch
        {
            File.Delete(iso);
            throw;
        }
        finally
        {
            LocalPaths.RemoveDirectory(directory, mediaId);
        }

        // The image may already be mounted if the host response is lost.
        await hypervisor.SetInboxAsync(vm, iso);
        return copied.ToArray();
    }

    private static string[] ValidateInboxFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            throw new LabException("error.file");
        var sources = files.Select(file => LocalPaths.RequireFile(file)).ToArray();
        if (sources.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new LabException("error.duplicateFiles");
        return sources;
    }

    private async Task WithMachineAsync(Guid id, Func<MachineLocation, Task> action, RecordedOperation? recorded = null)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var location = store.Location(state, FindMachine(state, id));
        if (recorded != RecordedOperation.PowerOff)
            RequireNoDiskRestore(state, id);
        LocalPaths.RequireOrdinaryPath(location.Directory);
        await action(location);
        if (recorded is { } operation)
        {
            RecordOperation(state, id, operation);
            store.Save(state);
        }
    }

    private static Machine FindMachine(LabState state, Guid id) =>
        state.Machines.FirstOrDefault(machine => machine.Id == id) ?? throw new LabException("error.machine");

    private static Template FindTemplate(LabState state, Guid id) =>
        state.Templates.FirstOrDefault(template => template.Id == id) ?? throw new LabException("error.template");
}
