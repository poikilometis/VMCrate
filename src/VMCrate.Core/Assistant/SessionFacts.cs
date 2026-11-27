namespace VMCrate.Core;

public static class SessionFacts
{
    public static AssistantFact[] Build(AnalysisReport report)
    {
        var facts = new List<AssistantFact>();
        if (report.Machine is { } vm)
        {
            facts.Add(new("session-state", $"Guest={vm.Guest}; VM power={vm.Power}; network={vm.Network}; observed={vm.ObservedUtc:u}"));
            if (vm.Findings.Length > 0)
                facts.Add(new("session-audit", "Configuration audit: " + string.Join(", ", vm.Findings)));
            if (vm.TemplateSha256 is { } hash)
                facts.Add(new("template-sha256", $"Template SHA-256={hash}"));
        }
        facts.AddRange(report.Files.Take(12).Select((file, index) =>
            new AssistantFact($"file-{index + 1}", $"Bytes={file.SizeBytes}; SHA-256={file.Sha256}")));
        facts.AddRange((report.Events ?? []).TakeLast(28).Select((entry, index) =>
            new AssistantFact($"event-{index + 1}", $"{entry.TimeUtc:u} {entry.Operation}; result={entry.Code ?? "recorded"}")));
        return facts.ToArray();
    }
}
