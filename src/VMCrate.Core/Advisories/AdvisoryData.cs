using System.Text.Json;

namespace VMCrate.Core;

public sealed partial class AdvisoryClient
{
    private async Task<AdvisoryDetail> ReadDetailAsync(string id, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Osv + "vulns/" + Uri.EscapeDataString(id));
        using var document = await ReadAsync(request, cancellation);
        var root = document.RootElement;
        if (String(root, "id") != id)
            throw new LabException("error.advisory");
        var aliases = Array(root, "aliases").Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!).Where(ValidId).ToArray();
        var fixes = new List<PublishedFix>();
        foreach (var affected in Array(root, "affected"))
        {
            if (!affected.TryGetProperty("package", out var package) || String(package, "name") is not string name ||
                String(package, "ecosystem") is not string ecosystem)
                continue;

            foreach (var item in Array(affected, "ranges").SelectMany(range => Array(range, "events")))
            {
                if (String(item, "fixed") is { Length: > 0 and <= 160 } version &&
                    version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '~' or '-' or '_' or '+' or ':'))
                    fixes.Add(new(name, ecosystem, version));
            }
        }
        return new(aliases, fixes);
    }

    private async Task<HashSet<string>?> ReadKevAsync(CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Kev);
            using var document = await ReadAsync(request, cancellation);
            if (!document.RootElement.TryGetProperty("vulnerabilities", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return null;
            return entries.EnumerateArray().Select(row => String(row, "cveID")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested &&
            error is HttpRequestException or IOException or JsonException or LabException or OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<JsonDocument> ReadAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        const int limit = 8 * 1024 * 1024;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var contents = new MemoryStream();
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[32 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (contents.Length + count > limit)
                throw new LabException("error.advisory");
            contents.Write(buffer, 0, count);
        }
        contents.Position = 0;
        return await JsonDocument.ParseAsync(contents, cancellationToken: deadline.Token);
    }

    private static IEnumerable<JsonElement> Array(JsonElement owner, string key) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];

    private static string? String(JsonElement owner, string key) => owner.ValueKind == JsonValueKind.Object &&
        owner.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ValidId(string id) => id is { Length: > 0 and <= 100 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');

    private sealed record PublishedFix(string Package, string Ecosystem, string Version);
    private sealed record AdvisoryDetail(string[] Aliases, List<PublishedFix> Fixes);
}
