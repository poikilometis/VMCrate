namespace VMCrate.Core;

public sealed record SessionReportChoice(Guid Id, string Name, bool Archived);

public sealed partial class LabService
{
    public IReadOnlyList<SessionReportChoice> ReadSessionReportChoices()
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        return state.Machines.Where(machine => machine.Disposable)
            .Select(machine => new SessionReportChoice(machine.Id, machine.Name, false))
            .Concat(state.ArchivedSessions.AsEnumerable().Reverse().Select(report =>
                new SessionReportChoice(report.Machine!.Id, report.Machine!.Name, true))).ToArray();
    }

    private static void ArchiveSession(LabState state, Machine machine)
    {
        if (!machine.Disposable)
            return;
        var time = DateTimeOffset.UtcNow;
        var template = state.Templates.SingleOrDefault(item => item.Id == machine.TemplateId);
        var evidence = new VmEvidence(machine.Id, machine.Name, machine.Guest, null, machine.Network, PowerState.Missing,
            template?.Name, template?.Sha256, null, [], time);
        state.ArchivedSessions.Add(new(1, time, evidence, machine.SessionFiles ?? [],
            state.Operations.Where(item => item.Resource == machine.Id).ToArray()));
        if (state.ArchivedSessions.Count > 64)
            state.ArchivedSessions.RemoveAt(0);
    }
}
