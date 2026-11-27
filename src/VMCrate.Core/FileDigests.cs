using System.Security.Cryptography;

namespace VMCrate.Core;

public sealed record FileProgress(long ProcessedBytes, long TotalBytes);

internal static class FileDigests
{
    internal static async Task<string> CopyAndHashAsync(string source, string destination,
        IProgress<FileProgress>? progress = null, CancellationToken cancellation = default)
    {
        using var input = Open(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await ReadAsync(input, output, progress, cancellation);
        output.Flush(flushToDisk: true);
        return hash;
    }

    internal static async Task<FileEvidence> InspectAsync(string path, CancellationToken cancellation = default,
        IProgress<FileProgress>? progress = null)
    {
        using var input = Open(path);
        var hash = await ReadAsync(input, null, progress, cancellation);
        return new FileEvidence(Path.GetFileName(path), input.Length, hash);
    }

    private static async Task<string> ReadAsync(FileStream input, FileStream? output,
        IProgress<FileProgress>? progress, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var total = input.Length;
        long processed = 0;
        var lastUpdate = Environment.TickCount64;
        progress?.Report(new(0, total));
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            hash.AppendData(buffer, 0, count);
            if (output is not null)
                await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
            processed += count;
            if (progress is not null && Environment.TickCount64 - lastUpdate >= 150)
            {
                progress.Report(new(processed, total));
                lastUpdate = Environment.TickCount64;
            }
        }
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new(processed, total));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
