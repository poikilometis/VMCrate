function New-CrateManifestSignature {
    param([Parameter(Mandatory)][string]$Manifest, [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    if (-not $Certificate.HasPrivateKey) { throw 'The signing certificate has no available private key.' }
    $usage = $Certificate.Extensions |
        Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        ForEach-Object EnhancedKeyUsages | Where-Object Value -EQ '1.3.6.1.5.5.7.3.3'
    if (-not $usage) { throw 'The certificate must be authorized for code signing.' }
    $content = [Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes($Manifest))
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new($content, $true)
    $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($Certificate)
    $signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
    $signer.IncludeOption = [Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $cms.ComputeSignature($signer)
    [IO.File]::WriteAllBytes($Destination, $cms.Encode())
}

function Test-CrateManifestSignature {
    param([Parameter(Mandatory)][string]$Manifest, [Parameter(Mandatory)][string]$Signature,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$SignerSha256)
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    $content = [Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes($Manifest))
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new($content, $true)
    $cms.Decode([IO.File]::ReadAllBytes($Signature))
    if ($cms.SignerInfos.Count -ne 1) { throw 'Expected one publisher signature.' }
    $signer = $cms.SignerInfos[0]
    if ($signer.DigestAlgorithm.Value -ne '2.16.840.1.101.3.4.2.1' -or
        $signer.Certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) -ne $SignerSha256) {
        throw 'The signature does not match the trusted publisher certificate.'
    }
    $cms.CheckSignature($true)
}
