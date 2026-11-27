using System.Net.Http.Json;
using System.Text.Json;

namespace VMCrate.Core;

public sealed partial class AdvisoryClient
{
    private const string Osv = "https://api.osv.dev/v1/";
    private const string Kev = "https://raw.githubusercontent.com/cisagov/kev-data/develop/known_exploited_vulnerabilities.json";
    private static readonly HttpClient Shared = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };
    private readonly HttpClient client;

    public AdvisoryClient() : this(Shared) { }
    internal AdvisoryClient(HttpClient client) => this.client = client;

    public async Task<UpdateAdvice> CheckAsync(PackageInventory input, CancellationToken cancellation = default)
    {
        var inventory = input.Validated();
        if (!inventory.SupportsOsv)
            throw new LabException("inventory.localHint");
        var hits = new List<PackageHit>();
        foreach (var batch in inventory.Packages.Chunk(100))
            await ReadBatchAsync(inventory.Ecosystem, batch, hits, cancellation);

        var kev = await ReadKevAsync(cancellation);
        var ids = hits.Select(item => item.Advisory).Distinct(StringComparer.Ordinal).ToArray();
        var detailIds = ids.OrderByDescending(id => kev?.Contains(id) == true).Take(100).ToArray();
        var details = new System.Collections.Concurrent.ConcurrentDictionary<string, AdvisoryDetail>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(detailIds, new ParallelOptions
        {
            CancellationToken = cancellation, MaxDegreeOfParallelism = 5
        }, async (id, token) =>
        {
            try { details.TryAdd(id, await ReadDetailAsync(id, token)); }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is HttpRequestException or IOException or JsonException or LabException or OperationCanceledException) { }
        });

        var findings = hits.Distinct().Select(hit =>
        {
            details.TryGetValue(hit.Advisory, out var detail);
            var exploited = kev is not null && (kev.Contains(hit.Advisory) || detail?.Aliases.Any(kev.Contains) == true);
            var fixes = detail?.Fixes.Where(fix => fix.Package == hit.Package.Name && fix.Ecosystem == inventory.Ecosystem)
                .Select(fix => fix.Version).Distinct(StringComparer.Ordinal).Take(24).ToArray() ?? [];
            return new UpdateFinding(hit.Package.Name, hit.Package.Version, hit.Advisory, exploited, fixes);
        }).OrderByDescending(item => item.KnownExploited).ThenBy(item => item.Package, StringComparer.Ordinal)
            .ThenBy(item => item.Advisory, StringComparer.Ordinal).ToArray();
        return new(DateTimeOffset.UtcNow, inventory.Ecosystem, inventory.Packages.Length, kev is not null, details.Count, ids.Length, findings);
    }

    private async Task ReadBatchAsync(string ecosystem, InventoryPackage[] packages, List<PackageHit> hits,
        CancellationToken cancellation)
    {
        var current = packages.Select(package => (Package: package, Token: (string?)null)).ToArray();
        for (var page = 0; current.Length > 0 && page < 40; page++)
        {
            var queries = current.Select(item => new
            {
                package = new { name = item.Package.Name, ecosystem },
                version = item.Package.Version,
                page_token = item.Token
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, Osv + "querybatch")
            {
                Content = JsonContent.Create(new { queries }, options: new JsonSerializerOptions { DefaultIgnoreCondition =
                    System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })
            };
            using var document = await ReadAsync(request, cancellation);
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() != current.Length)
                throw new LabException("error.advisory");

            var next = new List<(InventoryPackage Package, string? Token)>();
            for (var index = 0; index < current.Length; index++)
            {
                var row = results[index];
                var (package, _) = current[index];
                foreach (var vulnerability in Array(row, "vulns"))
                {
                    if (String(vulnerability, "id") is { } id && ValidId(id))
                        hits.Add(new(package, id));
                }
                if (String(row, "next_page_token") is { Length: > 0 and < 4096 } nextToken)
                    next.Add((package, nextToken));
            }
            current = next.ToArray();
        }
        if (current.Length > 0)
            throw new LabException("error.advisory");
    }

    private sealed record PackageHit(InventoryPackage Package, string Advisory);
}
