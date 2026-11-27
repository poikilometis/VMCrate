using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VMCrate.Core;

namespace VMCrate.Windows;

public static partial class KaliSetupSeed
{
    private const string Loader = "search --no-floppy --file --set=seed /vmcrate.cfg\nsource ($seed)/vmcrate.cfg\n";

    public static async Task<string> CreateAsync(string sourceIso, string expectedSha256, string destination,
        string user, string password, CancellationToken cancellation = default)
    {
        if (!UserName().IsMatch(user) || user is "root" or "nobody" || password.Length is < 14 or > 128 ||
            password.Any(character => !char.IsAscii(character) || char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new LabException("error.kaliAccount");
        var source = LocalPaths.RequireFile(sourceIso, ".iso");
        expectedSha256 = expectedSha256.Trim();
        if (expectedSha256.Length != 64 || expectedSha256.Any(ch => !char.IsAsciiHexDigit(ch)))
            throw new LabException("error.isoHash");

        await using var handle = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(handle, cancellation));
        if (!digest.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.isoMismatch", digest);

        var iso = new Iso9660(handle);
        foreach (var file in new[] { "install.amd/vmlinuz", "install.amd/initrd.gz", "simple-cdd/default.preseed" })
            iso.RequireFile(file);
        if (!Encoding.UTF8.GetString(iso.ReadFile(".disk/info", 4096)).StartsWith("Kali", StringComparison.OrdinalIgnoreCase))
            throw new LabException("error.kaliLayout");
        var originalLoader = iso.ReadFile("EFI/kali/grub.cfg", 64 * 1024);
        var boot = iso.ReadFile("boot/grub/efi.img", 16 * 1024 * 1024);
        PatchBootImage(boot, originalLoader);
        cancellation.ThrowIfCancellationRequested();

        var workspace = Directory.CreateTempSubdirectory("vmcrate-kali-");
        try
        {
            var disc = workspace.CreateSubdirectory("disc");
            var efi = disc.CreateSubdirectory("EFI").CreateSubdirectory("kali");
            File.WriteAllText(Path.Combine(efi.FullName, "grub.cfg"), Loader);
            File.WriteAllText(Path.Combine(disc.FullName, "vmcrate.cfg"), BootCommand.ReplaceLineEndings("\n") + "\n");
            File.WriteAllBytes(Path.Combine(disc.FullName, "vmcrate.img"), CreateInitramfs(user, Sha512Crypt.Hash(password)));
            var bootImage = Path.Combine(workspace.FullName, "efi.img");
            File.WriteAllBytes(bootImage, boot);
            var image = Path.Combine(workspace.FullName, "seed.iso");
            try { UdfInboxWriter.WriteDirectory(disc.FullName, image, "VMCRATE_SEED", bootImage); }
            catch (COMException error) { throw new LabException("error.kaliLayout", inner: error); }

            using var output = File.OpenRead(image);
            if (!new Iso9660(output).HasEfiDefaultEntry())
                throw new LabException("error.kaliLayout");
            output.Position = 0;
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(output, cancellation));
            output.Position = 0;
            await OutputFile.WriteNewAsync(destination, ".iso", target => output.CopyToAsync(target, cancellation));
            return sha256;
        }
        finally
        {
            LocalPaths.RequireOrdinaryPath(workspace.FullName);
            workspace.Delete(recursive: true);
        }
    }

    private static void PatchBootImage(Span<byte> image, ReadOnlySpan<byte> original)
    {
        var offset = image.IndexOf(original);
        if (original.Length <= Loader.Length || offset < 0 || image[(offset + 1)..].IndexOf(original) >= 0)
            throw new LabException("error.kaliLayout");
        var match = image.Slice(offset, original.Length);
        match.Fill((byte)' ');
        Encoding.ASCII.GetBytes(Loader, match);
        match[^1] = (byte)'\n';
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,30}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UserName();
}
