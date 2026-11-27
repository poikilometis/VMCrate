using System.IO;
using System.Text;
using VMCrate.Core;
using VMCrate.Windows;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class KaliSeedChecks
{
    private const string FixturePassword = "Example-fixture_82Z!";

    internal static async Task PasswordAndMedia()
    {
        const string expected = "$6$rounds=5000$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1";
        var hash = Sha512Crypt.Hash("Hello world!", "saltstring", 5000);
        Expect(hash == expected, "SHA-512 crypt output differs from the standard test vector.");
        var initrd = Encoding.UTF8.GetString(KaliSetupSeed.CreateInitramfs("fixture", Sha512Crypt.Hash(FixturePassword)));
        Expect(initrd.StartsWith("070701") && initrd.Contains("passwd/user-password-crypted password $6$") &&
            initrd.Contains("netcfg/enable boolean false") && !initrd.Contains(FixturePassword),
            "The answer file must configure a no-network install and contain only a password hash.");

        using var temporary = new TemporaryDirectory();
        var input = temporary.File("source.iso", "something else");
        var output = Path.Combine(temporary.Root, "output.iso");
        await ErrorAsync("error.isoMismatch", () => KaliSetupSeed.CreateAsync(input, new string('0', 64), output, "fixture", FixturePassword));
        Expect(!File.Exists(output), "An unverified installer must not produce boot media.");
    }

    internal static int VerifyPublisher(string manifest, string signature, string publicKey)
    {
        try
        {
            const string fingerprint = "827C8569F2518CC677FECA1AED65462EC8D5E4C5";
            var verifier = new PublisherSignatureVerifier();
            var data = File.ReadAllBytes(manifest);
            var signed = File.ReadAllBytes(signature);
            var key = File.ReadAllBytes(publicKey);
            verifier.VerifyAsync(data, signed, key, fingerprint, default).GetAwaiter().GetResult();
            data[0] ^= 1;
            ErrorAsync("error.imageSignature", () => verifier.VerifyAsync(data, signed, key, fingerprint, default))
                .GetAwaiter().GetResult();
            Console.WriteLine("PASS Kali publisher signature verified and changed manifest rejected");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL Kali publisher signature · {error}");
            return 1;
        }
    }

    internal static int VerifyInstallerMedia(string path, string sha256, string output)
    {
        try
        {
            var digest = KaliSetupSeed.CreateAsync(path, sha256, output, "fixture", FixturePassword).GetAwaiter().GetResult();
            using var stream = File.OpenRead(output);
            var iso = new Iso9660(stream);
            var initrd = Encoding.UTF8.GetString(iso.ReadFile("vmcrate.img", 16 * 1024));
            Expect(iso.HasEfiDefaultEntry() && initrd.Contains("passwd/username string fixture") && !initrd.Contains(FixturePassword),
                "The generated boot media did not have the EFI entry and hashed answers.");
            Console.WriteLine($"PASS Kali EFI seed media · {digest}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL Kali seed media · {error}");
            return 1;
        }
    }
}
