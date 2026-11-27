using System.ComponentModel;
using System.Diagnostics;
using VMCrate.Core;

namespace VMCrate.Windows;

public sealed class PublisherSignatureVerifier : IManifestSignatureVerifier
{
    private readonly string? gpg = FindExecutable();
    public bool Available => gpg is not null;

    public async Task VerifyAsync(byte[] manifest, byte[] signature, byte[] publicKey, string fingerprint,
        CancellationToken cancellation)
    {
        if (gpg is null)
            throw new LabException("error.imageSignature");

        var home = Directory.CreateTempSubdirectory("vmcrate-pgp-").FullName;
        try
        {
            var data = Path.Combine(home, "SHA256SUMS");
            var detached = Path.Combine(home, "SHA256SUMS.gpg");
            var key = Path.Combine(home, "publisher.asc");
            await File.WriteAllBytesAsync(data, manifest, cancellation);
            await File.WriteAllBytesAsync(detached, signature, cancellation);
            await File.WriteAllBytesAsync(key, publicKey, cancellation);
            // Use the public key as a temporary keyring. GnuPG import may contact a secret-key agent.
            await RunAsync(home, ["--dearmor", "--output", "publisher.gpg", Path.GetFileName(key)], cancellation);
            var status = await RunAsync(home,
                ["--no-default-keyring", "--keyring", "./publisher.gpg", "--trust-model", "always",
                    "--status-fd", "1", "--verify", Path.GetFileName(detached), Path.GetFileName(data)], cancellation);
            if (!MatchesPinnedSigner(status, fingerprint))
                throw new LabException("error.imageSignature");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    internal static bool MatchesPinnedSigner(string status, string fingerprint) =>
        status.Split('\n').Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(fields => fields.Length >= 11 && fields[0] == "[GNUPG:]" && fields[1] == "VALIDSIG" &&
                (fields[2].Equals(fingerprint, StringComparison.OrdinalIgnoreCase) ||
                 (fields.Length >= 12 && fields[11].Equals(fingerprint, StringComparison.OrdinalIgnoreCase))));

    private async Task<string> RunAsync(string home, string[] command, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(gpg!)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = home,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "--no-options", "--batch", "--no-tty", "--no-autostart", "--no-auto-key-retrieve",
                     "--no-auto-key-import", "--homedir", "." }.Concat(command))
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new LabException("error.imageSignature");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                cancellation.ThrowIfCancellationRequested();
                throw new LabException("error.imageSignature");
            }
            await errors;
            if (process.ExitCode != 0)
                throw new LabException("error.imageSignature");
            return await output;
        }
        catch (Win32Exception error) { throw new LabException("error.imageSignature", inner: error); }
    }

    private static string? FindExecutable()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => path.Length > 0);
        return roots.SelectMany(root => new[] { @"GnuPG\bin\gpg.exe", @"Gpg4win\bin\gpg.exe", @"Git\usr\bin\gpg.exe" }
                .Select(relative => Path.Combine(root, relative)))
            .FirstOrDefault(File.Exists);
    }
}
