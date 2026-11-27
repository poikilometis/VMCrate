using System.Text.Json;

namespace VMCrate.Core;

public sealed partial class OfficialImages
{
    private readonly SemaphoreSlim connections = new(6);

    private async Task<ImageCatalogListing> ReadFeedAsync(ImageSource source, ImageFeed feed, CancellationToken cancellation)
    {
        try
        {
            if (feed.Kind == ImageFeedKind.Checksums)
                return await ReadChecksumsAsync(source, feed.Address, cancellation);
            var (address, text) = await ReadAsync(feed.Address, cancellation);
            switch (feed.Kind)
            {
                case ImageFeedKind.Fedora:
                    return WithProvenance(ImageManifestParser.Fedora(source, text),
                        new(MediaVerification.PublisherSha256, source.Id, address));
                case ImageFeedKind.VersionIndex:
                    var versions = ImageManifestParser.VersionDirectories(text)
                        .Select(path => new Uri(address, path + (feed.Manifest ?? "SHA256SUMS")));
                    return await ReadChildrenAsync(source, versions, cancellation);
                case ImageFeedKind.ChecksumIndex:
                    var checksums = ImageManifestParser.PerImageManifests(text)
                        .Where(file => feed.Manifest is null || file.Contains(feed.Manifest, StringComparison.OrdinalIgnoreCase))
                        .Select(file => new Uri(address, file));
                    return await ReadChildrenAsync(source, checksums, cancellation);
                default:
                    throw new LabException("error.imageSource");
            }
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested &&
            error is HttpRequestException or IOException or OperationCanceledException or LabException or JsonException or
                InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return new([], 1);
        }
    }

    private async Task<ImageCatalogListing> ReadChildrenAsync(ImageSource source, IEnumerable<Uri> children, CancellationToken cancellation)
    {
        var results = await Task.WhenAll(children.Select(uri => ReadFeedAsync(source,
            new ImageFeed(ImageFeedKind.Checksums, uri), cancellation)));
        return results.Length == 0 ? new([], 1) :
            new(results.SelectMany(result => result.Images).ToArray(), results.Sum(result => result.FailedFeeds));
    }

    private async Task<ImageCatalogListing> ReadChecksumsAsync(ImageSource source, Uri requested, CancellationToken cancellation)
    {
        await connections.WaitAsync(cancellation);
        try
        {
            var (address, bytes) = await transport.ReadManifestBytesAsync(requested, cancellation);
            var proof = new MediaProvenance(MediaVerification.PublisherSha256, source.Id, address);
            if (source.Id is "kali" or "kali-weekly" && verifier is { Available: true })
            {
                const string fingerprint = "827C8569F2518CC677FECA1AED65462EC8D5E4C5";
                var signature = await transport.ReadManifestBytesAsync(new Uri(address.AbsoluteUri + ".gpg"), cancellation);
                var key = await transport.ReadManifestBytesAsync(new Uri("https://archive.kali.org/archive-key.asc"), cancellation);
                await verifier.VerifyAsync(bytes, signature.Bytes, key.Bytes, fingerprint, cancellation);
                proof = proof with { Verification = MediaVerification.PublisherSignature, Fingerprint = fingerprint };
            }

            return WithProvenance(ImageManifestParser.Checksums(source, address, System.Text.Encoding.UTF8.GetString(bytes)), proof);
        }
        finally { connections.Release(); }
    }

    private static ImageCatalogListing WithProvenance(OfficialImage[] files, MediaProvenance proof) =>
        new(files.Select(image => image with { Provenance = proof }).ToArray(), files.Length == 0 ? 1 : 0);

    private async Task<(Uri Address, string Text)> ReadAsync(Uri address, CancellationToken cancellation)
    {
        await connections.WaitAsync(cancellation);
        try { return await transport.ReadManifestAsync(address, cancellation); }
        finally { connections.Release(); }
    }
}
