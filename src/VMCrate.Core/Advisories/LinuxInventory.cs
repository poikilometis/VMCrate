namespace VMCrate.Core;

public sealed partial class LabService
{
    private const string LinuxCollector = "VMCrate.Core.LinuxInventory.sh";

    public static void SaveLinuxInventoryCollector(string path) => OutputFile.WriteNew(path, ".sh", WriteLinuxCollector);

    public async Task MountLinuxInventoryCollectorAsync(Guid template, Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var vm = FindMachine(state, id);
        if (vm is not { Guest: GuestFamily.Linux, Disposable: false, Network: NetworkMode.Offline } ||
            vm.TemplateId != template || state.Provisions.Any(item => item.Id == id))
            throw new LabException("error.linuxInventory");

        RequireNoDiskRestore(state, id);
        var isolation = await ObserveIsolationAsync(state, vm);
        if (isolation.Host is not { State: PowerState.Off or PowerState.Running, NetworkPolicyMatches: true })
            throw new LabException("error.isolation");

        var parent = Path.Combine(store.Root, "inbox-staging");
        var stage = Guid.NewGuid();
        var directory = LocalPaths.MakeDirectory(Path.Combine(parent, stage.ToString("N")));
        try
        {
            var script = Path.Combine(directory, "VMCrate-inventory.sh");
            SaveLinuxInventoryCollector(script);
            await SendFilesCoreAsync(store.Location(state, vm), [script]);
            RecordOperation(state, id, RecordedOperation.Inbox);
            store.Save(state);
        }
        finally { LocalPaths.RemoveDirectory(parent, stage); }
    }

    private static void WriteLinuxCollector(Stream output)
    {
        using var input = typeof(LabService).Assembly.GetManifestResourceStream(LinuxCollector)!;
        using var reader = new StreamReader(input);
        using var writer = new StreamWriter(output, leaveOpen: true);
        writer.Write(reader.ReadToEnd().ReplaceLineEndings("\n"));
    }
}
