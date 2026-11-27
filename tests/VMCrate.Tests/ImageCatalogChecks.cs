using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class ImageCatalogChecks
{
    private const string Hash = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";

    internal static async Task PublisherFeeds()
    {
        using var client = new HttpClient(new Fixture(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "releases.ubuntu.com" && uri.AbsolutePath == "/")
                return new(HttpStatusCode.OK) { Content = new StringContent(
                    """<a href="24.04/">ok</a><a href="26.04/">redirect</a><a href="../wrong/">ignore</a>""") };
            if (uri.AbsolutePath == "/26.04/SHA256SUMS")
            {
                var external = new HttpResponseMessage(HttpStatusCode.Found);
                external.Headers.Location = new Uri("https://outside.example/hash");
                return external;
            }
            Expect(uri.AbsolutePath == "/24.04/SHA256SUMS", $"Unexpected publisher URL {uri}.");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent($"{Hash} *ubuntu-24.04-desktop-amd64.iso\n" +
                    $"{Hash}  ubuntu-24.04-desktop-arm64.iso\n{Hash}  ../ubuntu-amd64.iso")
            };
        }));
        var catalog = new OfficialImages(new CatalogTransport(client));
        var listing = await catalog.ListAsync("ubuntu");
        var iso = listing.Images.Single();
        Expect(listing.FailedFeeds == 1 && iso.Download.Host == "releases.ubuntu.com" &&
            iso.FileName == "ubuntu-24.04-desktop-amd64.iso", "Failed or foreign feeds polluted the catalog.");
        Expect((await catalog.ListAsync("windows11")).Images.Length == 0, "Windows publisher requires a browser selection.");

        var rocky = OfficialImages.Sources.Single(source => source.Id == "rocky");
        var declared = ImageManifestParser.Checksums(rocky, rocky.Feeds![0].Address,
            $"SHA256 (Rocky-10-x86_64-boot.iso) = {Hash}\n").Single();
        Expect(declared.Download.AbsolutePath.EndsWith("Rocky-10-x86_64-boot.iso", StringComparison.Ordinal),
            "BSD checksums were not parsed.");
        var fedora = OfficialImages.Sources.Single(source => source.Id == "fedora");
        var variants = ImageManifestParser.Fedora(fedora, $$"""
        [
          {"arch":"x86_64","link":"https://download.fedoraproject.org/pub/Fedora-x86_64.iso","sha256":"{{Hash}}","size":"123"},
          {"arch":"x86_64","link":"https://not-fedoraproject.org/pub/Fedora-x86_64.iso","sha256":"{{Hash}}","size":"123"}
        ]
        """);
        Expect(variants.Single().Bytes == 123, "Fedora size or publisher checks failed.");
    }

    internal static async Task SignedManifest()
    {
        var bytes = Encoding.UTF8.GetBytes($"{Hash} *kali-linux-test-installer-amd64.iso\r\n");
        using var client = new HttpClient(new Fixture(request => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("/SHA256SUMS", StringComparison.Ordinal)
                ? bytes : [0, 255, 7])
        }));
        var verifier = new SignatureFixture(bytes);
        var catalog = new OfficialImages(new CatalogTransport(client), verifier);
        var result = await catalog.ListAsync("kali");
        Expect(result.Images.Single().Provenance is { Verification: MediaVerification.PublisherSignature,
            Fingerprint: "827C8569F2518CC677FECA1AED65462EC8D5E4C5" }, "Signed metadata was not retained.");
        verifier.Reject = true;
        result = await catalog.ListAsync("kali");
        Expect(result.Images.Length == 0 && result.FailedFeeds == 1, "An invalid signature was downgraded to a checksum.");
    }

    private sealed class SignatureFixture(byte[] bytes) : IManifestSignatureVerifier
    {
        public bool Available => true;
        public bool Reject { get; set; }

        public Task VerifyAsync(byte[] manifest, byte[] signature, byte[] publicKey, string fingerprint,
            CancellationToken cancellation)
        {
            Expect(bytes.SequenceEqual(manifest) && signature.SequenceEqual(new byte[] { 0, 255, 7 }),
                "The publisher bytes changed before verification.");
            if (Reject)
                throw new LabException("error.imageSignature");
            return Task.CompletedTask;
        }
    }

    internal static async Task ResumesAndVerifies()
    {
        using var temp = new TemporaryDirectory();
        var calls = 0;
        var payload = Encoding.UTF8.GetBytes("synthetic official image");
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        using var client = new HttpClient(new Fixture(request =>
        {
            calls++;
            if (calls == 1)
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload[..6]) { Headers = { ContentLength = payload.Length } } };
            Expect(request.Headers.Range?.Ranges.Single().From == 6, "The partial file was not resumed.");
            var content = new ByteArrayContent(payload[6..]);
            content.Headers.ContentRange = new ContentRangeHeaderValue(6, payload.Length - 1, payload.Length);
            return new(HttpStatusCode.PartialContent) { Content = content };
        }));
        var store = new StateStore(temp.Root);
        var download = new InstallerDownload(store, new CatalogTransport(client));
        var source = OfficialImages.Sources.Single(row => row.Id == "kali");
        var image = new OfficialImage("fixture-amd64.iso", new Uri("https://kali.download/fixture-amd64.iso"), hash, payload.Length, source);
        await ErrorAsync("error.imageIncomplete", () => download.FetchAsync(image, null, default));
        Expect(!File.Exists(store.InstallerMedia(hash)), "The short download reached the verified cache.");
        var verified = await download.FetchAsync(image, null, default);
        Expect(File.ReadAllBytes(verified.Path).SequenceEqual(payload), "Bytes were corrupted after resume.");
        Expect(File.GetAttributes(verified.Path).HasFlag(FileAttributes.ReadOnly), "Cached ISO is writable.");
        Expect(await download.FetchAsync(image, null, default) == verified && calls == 2, "A verified ISO was not reused.");

        using var invalid = new HttpClient(new Fixture(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var unverified = new InstallerDownload(store, new CatalogTransport(invalid));
        await ErrorAsync("error.isoMismatch", () => unverified.FetchAsync(image with { Sha256 = Hash }, null, default));
        Expect(!File.Exists(store.InstallerMedia(Hash)), "A mismatched ISO was published.");
    }

    private sealed class Fixture(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = reply(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
