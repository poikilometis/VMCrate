namespace VMCrate.Core;

public enum MediaVerification { LocalSha256, PublisherSha256, PublisherSignature }

public sealed record MediaProvenance(MediaVerification Verification, string? Publisher = null,
    Uri? Manifest = null, string? Fingerprint = null, DateTimeOffset? VerifiedUtc = null);

public interface IManifestSignatureVerifier
{
    bool Available { get; }

    Task VerifyAsync(byte[] manifest, byte[] signature, byte[] publicKey, string fingerprint,
        CancellationToken cancellation);
}
