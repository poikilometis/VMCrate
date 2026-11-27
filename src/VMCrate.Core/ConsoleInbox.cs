using System.Text;

namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task SendTextToInboxAsync(Guid id, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 256 * 1024)
            throw new LabException("error.inboxText");
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var vm = FindMachine(state, id);
        RequireNoDiskRestore(state, id);
        var parent = Path.Combine(store.Root, "inbox-staging");
        var stage = Guid.NewGuid();
        var directory = LocalPaths.MakeDirectory(Path.Combine(parent, stage.ToString("N")));
        try
        {
            var path = Path.Combine(directory, "VMCrate-note.txt");
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
            var files = await SendFilesCoreAsync(store.Location(state, vm), [path], record: vm.Disposable);
            if (vm.Disposable)
                state.Machines[state.Machines.IndexOf(vm)] = vm with
                {
                    SessionFiles = (vm.SessionFiles ?? []).Concat(files).Distinct().ToArray()
                };
            RecordOperation(state, id, RecordedOperation.Inbox);
            store.Save(state);
        }
        finally { LocalPaths.RemoveDirectory(parent, stage); }
    }
}
