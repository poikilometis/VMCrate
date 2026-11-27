using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using VMCrate.Core;

namespace VMCrate.Desktop;

internal sealed record ReleaseUpdate(Guid Id, string Version, string Sha256, string PreviousExecutable)
{
    private const string Repository = "poikilometis/VMCrate";
    internal const string ReleasesUrl = "https://github.com/" + Repository + "/releases/latest";

    internal static async Task<ReleaseUpdate> StageAsync(string workspace, string archive, CancellationToken cancellation)
    {
        const string prefix = "VMCrate-";
        const string suffix = "-win-x64.zip";
        var file = LocalPaths.RequireFile(archive, ".zip");
        var name = Path.GetFileName(file);
        var v = typeof(ReleaseUpdate).Assembly.GetName().Version!;
        if (name.Length <= prefix.Length + suffix.Length || !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal) ||
            !System.Version.TryParse(name[prefix.Length..^suffix.Length], out var version) || version.Build < 0 ||
            version.Revision != -1 || name != $"{prefix}{version}{suffix}" || version < new Version(v.Major, v.Minor, v.Build))
            throw new LabException("error.appUpdate");

        await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length > 1024L * 1024 * 1024)
            throw new LabException("error.appUpdate");
        await VerifyAsync(file, $"v{version}", cancellation);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellation));
        source.Position = 0;
        var previous = LocalPaths.RequireFile(Path.Combine(AppContext.BaseDirectory, "VMCrate.exe"), ".exe");
        var candidate = new ReleaseUpdate(Guid.NewGuid(), version.ToString(), hash, previous);
        var directory = WorkspaceSchemaUpdate.DirectoryFor(workspace, candidate.Id);
        LocalPaths.RequireOrdinaryPath(directory);
        Directory.CreateDirectory(directory);
        try
        {
            var package = Path.Combine(directory, "package");
            await ExtractAsync(source, package, cancellation);
            foreach (var required in new[] { "VMCrate.exe", "VMCrate.dll", "runtime/dotnet.exe", "HyperV/Invoke-HyperV.ps1" })
                LocalPaths.RequireFile(Path.Combine(package, required));
            var packaged = AssemblyName.GetAssemblyName(Path.Combine(package, "VMCrate.dll")).Version;
            if (packaged is null || packaged.Major != version.Major || packaged.Minor != version.Minor || packaged.Build != version.Build)
                throw new LabException("error.appUpdate");
            var temporary = Path.Combine(directory, "release.pending.json");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, candidate, JsonDefaults.Options, cancellation);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(directory, "release.json"));
        }
        catch
        {
            WorkspaceSchemaUpdate.DiscardIncomplete(workspace, candidate.Id);
            throw;
        }
        return candidate;
    }

    private static async Task VerifyAsync(string file, string tag, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("gh.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true };
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        foreach (var arg in new[] { "attestation", "verify", file, "--hostname", "github.com", "--repo", Repository,
            "--signer-workflow", Repository + "/.github/workflows/ci.yml", "--source-ref", "refs/tags/" + tag,
            "--deny-self-hosted-runners" })
            start.ArgumentList.Add(arg);
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception error) { throw new LabException("error.appUpdateGh", inner: error); }
        using var verifier = process ?? throw new LabException("error.appUpdateGh");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var drain = Task.WhenAll(verifier.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token),
            verifier.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token));
        try
        {
            await verifier.WaitForExitAsync(timeout.Token);
            await drain;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new LabException("error.appUpdateProvenance");
        }
        finally
        {
            if (!verifier.HasExited)
                verifier.Kill();
        }
        if (verifier.ExitCode != 0)
            throw new LabException("error.appUpdateProvenance");
    }

    private static async Task ExtractAsync(Stream source, string package, CancellationToken cancellation)
    {
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        if (zip.Entries.Count == 0 || zip.Entries.Count > 15000 || zip.Entries.Sum(entry => entry.Length) > 1536L * 1024 * 1024)
            throw new LabException("error.appUpdate");
        var prefix = package + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(package);
        foreach (var entry in zip.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var name = entry.FullName;
            var parts = name.TrimEnd('/').Split('/');
            if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
                throw new LabException("error.appUpdate");
            var file = LocalPaths.FullPath(Path.Combine(package, name));
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new LabException("error.appUpdate");
            LocalPaths.RequireOrdinaryPath(file);
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(file);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await using var input = entry.Open();
            await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            await input.CopyToAsync(output, cancellation);
        }
    }

    internal static ReleaseUpdate Read(string workspace, Guid id)
    {
        var path = LocalPaths.RequireFile(Path.Combine(WorkspaceSchemaUpdate.DirectoryFor(workspace, id), "release.json"), ".json");
        using var input = File.OpenRead(path);
        if (input.Length > 4096 || JsonSerializer.Deserialize<ReleaseUpdate>(input, JsonDefaults.Options) is not { } candidate ||
            candidate.Id != id || !System.Version.TryParse(candidate.Version, out _) || candidate.Sha256?.Length != 64)
            throw new LabException("error.appUpdate");
        return candidate;
    }

    internal static ReleaseUpdate? Current(string workspace)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        if (parent is null || !Guid.TryParseExact(Path.GetFileName(parent), "N", out var id) ||
            !string.Equals(parent, WorkspaceSchemaUpdate.DirectoryFor(workspace, id), StringComparison.OrdinalIgnoreCase))
            return null;
        return Read(workspace, id);
    }

    internal static string? InstalledWorkspace()
    {
        var package = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        return package.Name == "package" && package.Parent is { Parent: { Name: "app-updates", Parent: { } workspace } } id &&
            Guid.TryParseExact(id.Name, "N", out _) && File.Exists(Path.Combine(id.FullName, "release.json"))
                ? workspace.FullName : null;
    }

    internal string Executable(string workspace) =>
        LocalPaths.RequireFile(Path.Combine(WorkspaceSchemaUpdate.DirectoryFor(workspace, Id), "package", "VMCrate.exe"), ".exe");

    internal static void Start(string executable, string workspace, Guid? update = null)
    {
        var start = new ProcessStartInfo(LocalPaths.RequireFile(executable, ".exe")) { UseShellExecute = false };
        start.Environment["VMCRATE_DATA_DIR"] = workspace;
        if (update is Guid id)
        {
            start.ArgumentList.Add("--workspace-update");
            start.ArgumentList.Add(id.ToString("N"));
        }
        using var process = Process.Start(start) ?? throw new LabException("error.appUpdate");
    }
}
