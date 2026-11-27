using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace VMCrate.Core;

internal sealed class CatalogTransport(HttpClient client)
{
    private const int MetadataLimit = 2 * 1024 * 1024;
    internal static readonly CatalogTransport Shared = new(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(12), UseCookies = false
    }) { Timeout = Timeout.InfiniteTimeSpan });

    internal async Task<(Uri Address, string Text)> ReadManifestAsync(Uri uri, CancellationToken cancellation)
    {
        var (address, bytes) = await ReadManifestBytesAsync(uri, cancellation);
        return (address, Encoding.UTF8.GetString(bytes));
    }

    internal async Task<(Uri Address, byte[] Bytes)> ReadManifestBytesAsync(Uri uri, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await OpenAsync(uri, metadata: true, 0, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MetadataLimit)
            throw new LabException("error.imageSource");

        using var output = new MemoryStream();
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[32 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (output.Length + count > MetadataLimit)
                throw new LabException("error.imageSource");
            output.Write(buffer, 0, count);
        }
        return (response.RequestMessage?.RequestUri ?? uri, output.ToArray());
    }

    internal async Task<HttpResponseMessage> OpenAsync(Uri uri, bool metadata, long offset, CancellationToken cancellation)
    {
        var publisher = uri.IdnHost;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!SafeUri(uri) || (metadata && !SamePublisher(publisher, uri.IdnHost)))
                throw new LabException("error.imageRedirect");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            request.Headers.UserAgent.ParseAdd("VMCrate/1");
            if (offset > 0)
                request.Headers.Range = new RangeHeaderValue(offset, null);

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
                return response;

            var next = response.Headers.Location;
            response.Dispose();
            if (next is null)
                break;
            uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
        }
        throw new LabException("error.imageRedirect");
    }

    internal static bool SafeUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.IsDefaultPort &&
        uri.HostNameType == UriHostNameType.Dns && uri.IdnHost.Contains('.') &&
        !uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

    private static bool SamePublisher(string original, string redirected) => original == redirected ||
        (original.EndsWith(".opensuse.org", StringComparison.Ordinal) && redirected.EndsWith(".opensuse.org", StringComparison.Ordinal));
}
