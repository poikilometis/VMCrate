using System.Net;

namespace VMCrate.Core;

public sealed partial class LabService
{
    public Task<CachedInstaller> DownloadInstallerAsync(OfficialImage image, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellation = default) => new InstallerDownload(store, CatalogTransport.Shared)
        .FetchAsync(image, progress, cancellation);

    public async Task<string> CalculateLocalIsoDigestAsync(string path, CancellationToken cancellation = default) =>
        (await FileDigests.InspectAsync(LocalPaths.RequireFile(path, ".iso"), cancellation)).Sha256;
}

internal sealed class InstallerDownload(StateStore store, CatalogTransport transport)
{
    internal const long MaximumBytes = 128L * 1024 * 1024 * 1024;

    internal async Task<CachedInstaller> FetchAsync(OfficialImage image, IProgress<DownloadProgress>? progress,
        CancellationToken cancellation)
    {
        if (!CatalogTransport.SafeUri(image.Download) || image.Sha256.Length != 64 ||
            !image.Sha256.All(char.IsAsciiHexDigit) || image.Bytes is <= 0 or > MaximumBytes)
            throw new LabException("error.imageSource");
        var hash = image.Sha256.ToLowerInvariant();
        var provenance = image.Provenance ?? new MediaProvenance(MediaVerification.PublisherSha256, image.Source.Id);

        LocalPaths.MakeDirectory(store.Media);
        var cache = store.InstallerMedia(hash);
        var partial = Path.Combine(store.Media, hash + ".partial");
        using var download = TakeDownloadLock(Path.Combine(store.Media, hash + ".download.lock"));
        LocalPaths.RequireOrdinaryPath(cache);
        LocalPaths.RequireOrdinaryPath(partial);
        if (File.Exists(cache))
        {
            await VerifyAsync(cache, hash, progress, cancellation);
            return new(cache, hash, provenance with { VerifiedUtc = File.GetLastWriteTimeUtc(cache) });
        }

        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > MaximumBytes || (image.Bytes is long expected && offset > expected))
        {
            File.Delete(partial);
            offset = 0;
        }
        if (offset == 0 || offset != image.Bytes)
            await TransferAsync(image, partial, offset, progress, cancellation);

        if (image.Bytes is long total && new FileInfo(partial).Length != total)
            throw new LabException("error.imageIncomplete");
        try { await VerifyAsync(partial, hash, progress, cancellation); }
        catch (LabException error) when (error.Key == "error.isoMismatch")
        {
            File.Delete(partial);
            throw;
        }

        using var workspace = store.Lock();
        LocalPaths.RequireOrdinaryPath(cache);
        File.Move(partial, cache);
        File.SetAttributes(cache, File.GetAttributes(cache) | FileAttributes.ReadOnly);
        return new(cache, hash, provenance with { VerifiedUtc = File.GetLastWriteTimeUtc(cache) });
    }

    private async Task TransferAsync(OfficialImage image, string partial, long offset,
        IProgress<DownloadProgress>? progress, CancellationToken cancellation)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        idle.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await transport.OpenAsync(image.Download, metadata: false, offset, idle.Token);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0 &&
            response.Content.Headers.ContentRange?.Length == offset)
            return;
        response.EnsureSuccessStatusCode();
        var transfer = ImageTransfer.Describe(response, offset, image.Bytes);
        if (transfer.Total is long total)
        {
            var existingBytes = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            WorkspaceCapacity.RequireSpace(partial, Math.Max(0, total - existingBytes));
        }
        await transfer.WriteAsync(response.Content, partial, progress, idle);
    }

    private static async Task VerifyAsync(string path, string hash, IProgress<DownloadProgress>? progress,
        CancellationToken cancellation)
    {
        var length = new FileInfo(path).Length;
        progress?.Report(new(length, length, Verifying: true));
        var evidence = await FileDigests.InspectAsync(path, cancellation);
        if (evidence.Sha256 != hash)
            throw new LabException("error.isoMismatch", evidence.Sha256);
    }

    private static FileStream TakeDownloadLock(string path)
    {
        LocalPaths.RequireOrdinaryPath(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            throw new LabException("error.imageBusy", inner: error);
        }
    }
}
