using System.Net;
using System.Net.Http;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class AdvisoryChecks
{
    internal static async Task ExactInventory()
    {
        var pages = 0;
        using var client = new HttpClient(new Fixture(async (request, cancellation) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "raw.githubusercontent.com")
                return """{"vulnerabilities":[{"cveID":"CVE-2026-1234"}]}""";
            Expect(uri.Host == "api.osv.dev", "Unexpected external destination.");
            if (uri.AbsolutePath.EndsWith("/querybatch"))
            {
                var sent = await request.Content!.ReadAsStringAsync(cancellation);
                Expect(sent.Contains("Debian:13") && sent.Contains("1.2.3-1") && sent.Contains("fixture"), "Exact version was not queried.");
                if (++pages == 1)
                    return """{"results":[{"vulns":[{"id":"OSV-TEST"}],"next_page_token":"page2"}]}""";
                Expect(sent.Contains("page2"), "A paginated package result was lost.");
                return """{"results":[{}]}""";
            }

            Expect(uri.AbsolutePath == "/v1/vulns/OSV-TEST", "Unexpected advisory details address.");
            return """
            {"id":"OSV-TEST","aliases":["CVE-2026-1234"],"affected":[
              {"package":{"name":"fixture","ecosystem":"Debian:13"},"ranges":[{"events":[{"fixed":"1.2.4-1"}]}]},
              {"package":{"name":"another","ecosystem":"Debian:13"},"ranges":[{"events":[{"fixed":"99.0"}]}]}
            ]}
            """;
        }));
        var inventory = new PackageInventory(1, "Debian:13", [new("fixture", "1.2.3-1")]);
        var report = await new AdvisoryClient(client).CheckAsync(inventory);
        var found = report.Findings.Single();
        Expect(found.KnownExploited && found.PublishedFixes.SequenceEqual(["1.2.4-1"]) && pages == 2,
            "KEV alias, pagination or publisher fix mismatch.");
        await ErrorAsync("error.inventory", () => new AdvisoryClient(client)
            .CheckAsync(inventory with { Packages = [new("fixture", "a\nsecret")] }));
    }

    private sealed class Fixture(Func<HttpRequestMessage, CancellationToken, Task<string>> text) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await text(request, cancellationToken)) };
    }
}
