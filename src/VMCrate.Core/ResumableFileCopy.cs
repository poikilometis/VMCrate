using System.Security.Cryptography;

namespace VMCrate.Core;

internal static class ResumableFileCopy
{
    internal static long RemainingBytes(string source, string staging)
    {
        LocalPaths.RequireOrdinaryPath(staging);
        var complete = new FileInfo(source).Length;
        return File.Exists(staging) ? Math.Max(0, complete - new FileInfo(staging).Length) : complete;
    }

    internal static async Task<string> CopyAsync(string source, string staging, IProgress<FileProgress>? progress,
        CancellationToken cancellation)
    {
        LocalPaths.RequireOrdinaryPath(staging);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, true);
        using var output = new FileStream(staging, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 256 * 1024, true);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        var prior = new byte[buffer.Length];
        long offset = 0;
        var limit = Math.Min(input.Length, output.Length);
        var lastReport = Environment.TickCount64;
        while (offset < limit)
        {
            var count = (int)Math.Min(buffer.Length, limit - offset);
            await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellation);
            await output.ReadExactlyAsync(prior.AsMemory(0, count), cancellation);
            if (!buffer.AsSpan(0, count).SequenceEqual(prior.AsSpan(0, count)))
                break;
            digest.AppendData(buffer, 0, count);
            offset += count;
            Report();
        }

        output.SetLength(offset);
        output.Position = input.Position = offset;
        progress?.Report(new(offset, input.Length));
        int read;
        while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
            digest.AppendData(buffer, 0, read);
            offset += read;
            Report();
        }
        cancellation.ThrowIfCancellationRequested();
        output.Flush(flushToDisk: true);
        progress?.Report(new(offset, input.Length));
        return Convert.ToHexStringLower(digest.GetHashAndReset());

        void Report()
        {
            if (Environment.TickCount64 - lastReport < 750)
                return;
            progress?.Report(new(offset, input.Length));
            lastReport = Environment.TickCount64;
        }
    }
}
