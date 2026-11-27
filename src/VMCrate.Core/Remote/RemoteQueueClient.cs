using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace VMCrate.Core;

public sealed class RemoteQueueClient : IDisposable
{
    private readonly HttpClient client;
    private readonly X509Certificate2? identity;

    public RemoteQueueClient(string host, string serverSha256, string clientSha256)
    {
        var address = QueueAddress(host);
        if (!CertificatePins.IsSha256(serverSha256))
            throw new LabException("error.remoteCertificate");

        identity = CertificatePins.FindPrivate(clientSha256, CertificatePins.ClientAuthentication);
        var transport = new HttpClientHandler
        {
            AllowAutoRedirect = false, UseProxy = false, CheckCertificateRevocationList = true,
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                errors == SslPolicyErrors.None && certificate is not null &&
                CertificatePins.ActiveFor(certificate, CertificatePins.ServerAuthentication) &&
                CertificatePins.Matches(certificate, serverSha256)
        };
        transport.ClientCertificates.Add(identity);
        client = ConfigureClient(address, transport);
    }

    internal RemoteQueueClient(Uri address, HttpMessageHandler handler) => client = ConfigureClient(address, handler);

    public Task<RemoteRecipe[]> RecipesAsync(CancellationToken token = default) =>
        GetAsync<RemoteRecipe[]>("/recipes", token);

    public Task<RemoteJob[]> JobsAsync(CancellationToken token = default) =>
        GetAsync<RemoteJob[]>("/jobs", token);

    public async Task<RemoteJob> SubmitAsync(RemoteRequest request, CancellationToken token = default)
    {
        using var response = await client.PostAsJsonAsync(RemoteProtocol.Prefix + "/jobs", request, RemoteProtocol.Json, token);
        return await ReadAsync<RemoteJob>(response, token);
    }

    public async Task<RemoteJob> RetryAsync(Guid id, CancellationToken token = default)
    {
        using var response = await client.PostAsync(RemoteProtocol.Prefix + $"/jobs/{id:D}/retry", null, token);
        return await ReadAsync<RemoteJob>(response, token);
    }

    internal static Uri QueueAddress(string host)
    {
        if (!Uri.TryCreate(host.Trim(), UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps ||
            address.Host.Length == 0 || address.UserInfo.Length != 0 || address.Query.Length != 0 ||
            address.Fragment.Length != 0 || address.AbsolutePath != "/")
            throw new LabException("error.remoteAddress");
        return address;
    }

    private static HttpClient ConfigureClient(Uri address, HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = address, Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024
        };
        client.DefaultRequestHeaders.Add(RemoteProtocol.Header, RemoteProtocol.Version);
        return client;
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken token)
    {
        using var response = await client.GetAsync(RemoteProtocol.Prefix + path, token);
        return await ReadAsync<T>(response, token);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadFromJsonAsync<RemoteFailure>(RemoteProtocol.Json, token);
            var code = problem?.Code;
            throw new LabException(code is { Length: <= 64 } && code.StartsWith("error.", StringComparison.Ordinal) &&
                code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '.') ? code : "error.remoteJob");
        }
        return await response.Content.ReadFromJsonAsync<T>(RemoteProtocol.Json, token) ?? throw new LabException("error.remoteJob");
    }

    public void Dispose()
    {
        client.Dispose();
        identity?.Dispose();
    }
}
