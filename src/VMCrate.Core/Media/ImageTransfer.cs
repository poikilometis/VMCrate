using System.Net;

namespace VMCrate.Core;

internal sealed record ImageTransfer(long Offset, long? Total, long? ResponseEnd)
{
    internal static ImageTransfer Describe(HttpResponseMessage response, long requested, long? knownTotal)
    {
        var headers = response.Content.Headers;
        if (headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new LabException("error.imageRange");

        var length = headers.ContentLength;
        long offset;
        long? end;
        long? total;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = headers.ContentRange;
            if (range?.Unit != "bytes" || range.From != requested || range.To is not long last ||
                range.Length is not long remoteTotal || last < requested || last >= remoteTotal ||
                (length is not null && length != last - requested + 1))
                throw new LabException("error.imageRange");
            offset = requested;
            end = last + 1;
            total = remoteTotal;
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            offset = 0;
            end = length;
            total = length ?? knownTotal;
        }
        else
            throw new LabException("error.imageRange");

        if (total is <= 0 or > InstallerDownload.MaximumBytes ||
            (knownTotal is not null && total is not null && knownTotal != total))
            throw new LabException("error.imageRange");
        return new(offset, total, end);
    }

    internal async Task WriteAsync(HttpContent content, string path, IProgress<DownloadProgress>? progress,
        CancellationTokenSource idle)
    {
        idle.CancelAfter(TimeSpan.FromSeconds(30));
        await using var input = await content.ReadAsStreamAsync(idle.Token);
        using var output = new FileStream(path, Offset == 0 ? FileMode.Create : FileMode.Open,
            FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        if (output.Length != Offset)
            throw new LabException("error.imageRange");
        output.Position = Offset;
        var received = Offset;
        var lastReport = Environment.TickCount64 - 150;
        var buffer = new byte[128 * 1024];
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            var count = await input.ReadAsync(buffer, idle.Token);
            if (count == 0)
                break;
            if (received + count > (ResponseEnd ?? Total ?? InstallerDownload.MaximumBytes))
                throw new LabException("error.imageRange");
            await output.WriteAsync(buffer.AsMemory(0, count), idle.Token);
            received += count;
            if (Environment.TickCount64 - lastReport >= 150)
            {
                progress?.Report(new(received, Total));
                lastReport = Environment.TickCount64;
            }
        }

        output.Flush(flushToDisk: true);
        if (received == 0 || (Total is not null && received != Total))
            throw new LabException("error.imageIncomplete");
    }
}
