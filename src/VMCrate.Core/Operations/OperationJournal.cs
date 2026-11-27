namespace VMCrate.Core;

public enum RecordedOperation { Prepare, Start, PowerOff, Inbox, EjectInbox, Reset, Remove, Build, ProxyHost, ProxyGuest, VerifyRevision }

public sealed record OperationEntry(Guid Id, Guid Resource, DateTimeOffset TimeUtc, RecordedOperation Operation,
    string? Code = null, long? ElapsedMs = null);

public sealed partial class LabService
{
    public IReadOnlyList<OperationEntry> ReadOperations(Guid? resource = null)
    {
        using var workspaceLock = store.Lock();
        return store.Load().Operations.Where(item => resource is null || item.Resource == resource).ToArray();
    }

    private static void RecordOperation(LabState state, Guid resource, RecordedOperation operation,
        string? code = null, long? elapsedMs = null)
    {
        state.Operations.Add(new(Guid.NewGuid(), resource, DateTimeOffset.UtcNow, operation, code, elapsedMs));
        if (state.Operations.Count > 512)
            state.Operations.RemoveRange(0, state.Operations.Count - 512);
    }

    public void RecordGuestProxy(Guid id, ProxyGuestTrace trace, ProxyTarget? target = null)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        _ = ProxyBuilder(state, id);
        RecordProxyGuest(state, id, trace, target);
    }
}
