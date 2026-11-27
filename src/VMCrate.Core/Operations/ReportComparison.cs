using System.Text.Json;

namespace VMCrate.Core;

public sealed record ReportChange(string Field, string? Before, string? After);

public static class ReportComparison
{
    public static AnalysisReport Read(string path)
    {
        using var input = File.OpenRead(LocalPaths.RequireFile(path, ".json"));
        if (input.Length > 8 * 1024 * 1024)
            throw new LabException("error.reportEmpty");

        try
        {
            using var json = JsonDocument.Parse(input);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new LabException("error.reportEmpty");
            if (root.TryGetProperty("report", out var wrapped))
                root = wrapped;
            var report = root.Deserialize<AnalysisReport>(JsonDefaults.Options);
            if (report is not { Version: 1, Files: not null } || report.Files.Any(item => item is null) ||
                report is { Machine.Findings: null } || (report.Events ?? []).Any(item => item is null))
                throw new LabException("error.reportEmpty");
            return report;
        }
        catch (JsonException error) { throw new LabException("error.reportEmpty", inner: error); }
    }

    public static IReadOnlyList<ReportChange> Compare(AnalysisReport before, AnalysisReport after)
    {
        var older = Fields(before);
        var newer = Fields(after);
        return older.Zip(newer, (first, second) => new ReportChange(first.Field, first.Value, second.Value))
            .Where(item => item.Before != item.After).ToArray();
    }

    private static (string Field, string? Value)[] Fields(AnalysisReport report) =>
    [
        ("label.guest", report.Machine?.Guest.ToString()),
        ("label.template", report.Machine?.Template),
        ("label.isoHash", report.Machine?.TemplateSha256),
        ("label.network", report.Machine?.Network.ToString()),
        ("isolation.power", report.Machine?.Power.ToString()),
        ("report.findings", report.Machine is { } vm ? string.Join(", ", vm.Findings.Order(StringComparer.Ordinal)) : null),
        ("label.files", string.Join(Environment.NewLine, report.Files.OrderBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Sha256, StringComparer.Ordinal).Select(item => $"{item.Name} · {item.Sha256}"))),
        ("label.history", string.Join(Environment.NewLine, (report.Events ?? []).Select(item =>
            $"{item.Operation}" + (item.Code is null ? "" : $" · {item.Code}"))))
    ];
}
