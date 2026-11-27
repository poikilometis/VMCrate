using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VMCrate.Core;

public static class CertificatePins
{
    public const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public static bool Matches(X509Certificate2 certificate, string sha256) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256).Equals(sha256, StringComparison.OrdinalIgnoreCase);

    public static bool ActiveFor(X509Certificate2 certificate, string usage)
    {
        DateTime now = DateTime.UtcNow;
        return certificate.NotBefore.ToUniversalTime() <= now && now < certificate.NotAfter.ToUniversalTime() &&
            certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == usage));
    }

    public static X509Certificate2 FindPrivate(string hash, string usage)
    {
        if (!IsSha256(hash))
            throw new LabException("error.remoteCertificate");
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadOnly);
        var certificates = store.Certificates;
        X509Certificate2? selected = null;
        foreach (var certificate in certificates)
        {
            if (selected is null && certificate.HasPrivateKey && Matches(certificate, hash) && ActiveFor(certificate, usage))
                selected = certificate;
            else
                certificate.Dispose();
        }
        return selected ?? throw new LabException("error.remoteCertificate");
    }
}
