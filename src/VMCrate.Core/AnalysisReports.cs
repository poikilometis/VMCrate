using System.Text.Json;

namespace VMCrate.Core;

public sealed record FileEvidence(string Name, long SizeBytes, string Sha256);

public sealed record VmEvidence(
    Guid Id, string Name, GuestFamily Guest, string? Blueprint, NetworkMode Network, PowerState Power,
    string? Template, string? TemplateSha256, bool? EnhancedSessionEnabled, string[] Findings, DateTimeOffset ObservedUtc);

public sealed record AnalysisReport(int Version, DateTimeOffset CreatedUtc, VmEvidence? Machine, FileEvidence[] Files,
    OperationEntry[]? Events = null);

public sealed record TemplateIntegrity(string Sha256, bool? Matches);

public sealed partial class LabService
{
    public async Task ExportReportAsync(string destination, Guid? machineId, IReadOnlyList<string> files)
    {
        var report = await BuildReportAsync(machineId, files);
        OutputFile.WriteNew(destination, ".json", output => JsonSerializer.Serialize(output, report, JsonDefaults.Options));
    }

    public async Task<AnalysisReport> BuildReportAsync(Guid? machineId, IReadOnlyList<string> files)
    {
        if (machineId is null && files.Count == 0)
            throw new LabException("error.reportEmpty");

        var evidence = new List<FileEvidence>();
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase))
            evidence.Add(await FileDigests.InspectAsync(LocalPaths.RequireFile(path)));

        var report = machineId is Guid id
            ? await InspectForReportAsync(id) : new AnalysisReport(1, DateTimeOffset.UtcNow, null, []);
        return report with { Files = evidence.Concat(report.Files).Distinct().ToArray() };
    }

    public async Task<TemplateIntegrity> VerifyTemplateAsync(Guid id, CancellationToken cancellation = default,
        IProgress<FileProgress>? progress = null)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var template = FindTemplate(state, id);
        var path = LocalPaths.RequireFile(store.TemplateDisk(id), ".vhdx");
        var digest = (await FileDigests.InspectAsync(path, cancellation, progress)).Sha256;
        if (template.Sha256 is not null)
            return new TemplateIntegrity(digest, string.Equals(template.Sha256, digest, StringComparison.OrdinalIgnoreCase));

        state.Templates[state.Templates.IndexOf(template)] = template with { Sha256 = digest };
        store.Save(state);
        return new TemplateIntegrity(digest, null);
    }

    private async Task<AnalysisReport> InspectForReportAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var machine = state.Machines.SingleOrDefault(row => row.Id == id);
        if (machine is null)
            return state.ArchivedSessions.SingleOrDefault(report => report.Machine?.Id == id)
                ?? throw new LabException("error.machine");
        var location = store.Location(state, machine);
        var template = machine.TemplateId is Guid templateId ? FindTemplate(state, templateId) : null;
        HostObservation? host;
        try
        {
            host = await hypervisor.ObserveAsync([location]);
        }
        catch (LabException)
        {
            host = null;
        }

        var observed = host?.Machines.SingleOrDefault(vm => vm.Id == id);
        var vm = new VmEvidence(id, machine.Name, machine.Guest, machine.BlueprintId, machine.Network,
            observed?.State ?? PowerState.Unavailable, template?.Name, template?.Sha256,
            host?.EnhancedSession, observed?.Findings ?? ["audit.unavailable"], DateTimeOffset.UtcNow);
        return new AnalysisReport(1, DateTimeOffset.UtcNow, vm, machine.SessionFiles ?? [],
            state.Operations.Where(item => item.Resource == id).ToArray());
    }
}
