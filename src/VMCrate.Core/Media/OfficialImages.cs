using System.Text.Json;

namespace VMCrate.Core;

public enum ImageFeedKind { Checksums, VersionIndex, ChecksumIndex, Fedora }

public sealed record ImageFeed(ImageFeedKind Kind, Uri Address, string? Manifest = null);

public sealed record ImageSource(string Id, string Name, Uri Page, GuestFamily Guest, bool SecureBoot, ImageFeed[]? Feeds = null)
{
    public bool Automatic => Feeds is { Length: > 0 };
}

public sealed record OfficialImage(string FileName, Uri Download, string Sha256, long? Bytes, ImageSource Source,
    MediaProvenance? Provenance = null);
public sealed record ImageCatalogListing(OfficialImage[] Images, int FailedFeeds);
public sealed record DownloadProgress(long ReceivedBytes, long? TotalBytes, bool Verifying = false);
public sealed record CachedInstaller(string Path, string Sha256, MediaProvenance? Provenance = null);

public sealed partial class OfficialImages
{
    private readonly CatalogTransport transport;
    private readonly IManifestSignatureVerifier? verifier;

    public static IReadOnlyList<ImageSource> Sources { get; } = ReadSources();
    public OfficialImages(IManifestSignatureVerifier? verifier = null) : this(CatalogTransport.Shared, verifier) { }
    internal OfficialImages(CatalogTransport transport, IManifestSignatureVerifier? verifier = null)
    {
        this.transport = transport;
        this.verifier = verifier;
    }

    public async Task<ImageCatalogListing> ListAsync(string sourceId, CancellationToken cancellation = default)
    {
        var source = Sources.SingleOrDefault(item => item.Id == sourceId) ?? throw new LabException("error.imageSource");
        var feeds = source.Feeds ?? [];
        var manifests = await Task.WhenAll(feeds.Select(feed => ReadFeedAsync(source, feed, cancellation)));
        var images = manifests.SelectMany(result => result.Images)
            .DistinctBy(image => image.Sha256)
            .OrderByDescending(image => image.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(images, manifests.Sum(result => result.FailedFeeds));
    }

    private static IReadOnlyList<ImageSource> ReadSources()
    {
        using var stream = typeof(OfficialImages).Assembly.GetManifestResourceStream("VMCrate.Core.ImageSources.json")!;
        return Array.AsReadOnly(JsonSerializer.Deserialize<ImageSource[]>(stream, JsonDefaults.Options)!);
    }
}
