using System.IO;
using VMCrate.Core;
using VMCrate.Windows;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static partial class LiveAcceptance
{
    private static async Task InstallerRecoveryAsync(LiveWorkspace workspace, MediaFixture windows, MediaFixture linux)
    {
        var sources = new[] { (windows, GuestFamily.Windows), (linux, GuestFamily.Linux) };
        foreach (var (media, family) in sources)
        {
            var installer = new InstallerOptions(new($"Acceptance {family}", 4, 2), family,
                family == GuestFamily.Windows, media.Iso, 64);
            var seed = family == GuestFamily.Linux ? Path.Combine(workspace.Root, "kali-seed.iso") : null;
            var seedHash = seed is null ? null : await KaliSetupSeed.CreateAsync(media.Iso, media.Sha256, seed,
                "fixture", "Fixture-local_9284!");
            var job = workspace.Lab.QueueBuild(new TemplateBuildOptions($"Acceptance {family}", null, installer, media.Sha256,
                seed, BootSeedHash: seedHash));
            using var interrupted = new CancellationTokenSource();
            try { await workspace.Lab.ContinueBuildAsync(job.Id, new InterruptCopy(interrupted), interrupted.Token); }
            catch (OperationCanceledException) when (interrupted.IsCancellationRequested) { }
            Expect(workspace.Lab.ReadBuilds().Single(item => item.Id == job.Id).Stage == BuildStage.Verify &&
                File.Exists(Path.Combine(workspace.Store.Media, $"{job.Id:N}.copying")), "Interrupted ISO copy was not retained.");
            await workspace.Lab.ContinueBuildAsync(job.Id);
            workspace.Reopen();
            Expect(workspace.Lab.ReadBuilds().Single(item => item.Id == job.Id).Stage == BuildStage.Provision,
                "The build lost its stage on reopen.");
            await workspace.Lab.ContinueBuildAsync(job.Id);
            Expect(await workspace.Host.UsesMediaAsync(workspace.Store.InstallerMedia(media.Sha256)), "Installer DVD missing.");
            Expect((await workspace.ObserveAsync(job.Id)).Findings.Length == 0, "The offline installer failed its host audit.");
            if (seed is not null)
            {
                var state = workspace.Store.Load();
                var inbox = workspace.Store.Location(state, state.Machines.Single(vm => vm.Id == job.Id)).InboxDirectory;
                Expect(await workspace.Host.UsesMediaAsync(Path.Combine(inbox, $"{job.Id:N}.iso")), "Generated Kali seed DVD is missing.");
            }
            await workspace.Lab.StartAsync(job.Id);
            Expect((await workspace.ObserveAsync(job.Id)).State == PowerState.Running, "Installer did not start.");
            await workspace.Lab.PowerOffAsync(job.Id);
            await workspace.Lab.EjectInstallerAsync(job.Id);
            Expect(!await workspace.Host.UsesMediaAsync(workspace.Store.InstallerMedia(media.Sha256)), "Installer DVD was not ejected.");
            await workspace.Lab.DeleteMachineAsync(job.Id);
            Console.WriteLine($"PASS {family} installer start, disk layout, and Offline policy");
        }

        // Corrupt only the catalog of this test workspace and restore the real previous copy.
        var catalog = Path.Combine(workspace.Root, "state.json");
        File.WriteAllText(catalog, "{acceptance-fixture");
        var restore = (await workspace.Lab.InspectRecoveryAsync()).Single();
        Expect(restore.Kind == RecoveryKind.RestoreCatalog, "Catalog recovery did not offer its backup.");
        workspace.Lab.RestoreCatalogBackup(restore.Id);
        var saved = Directory.GetFiles(workspace.Root, "state.failed-*.json").Single();
        Expect(File.ReadAllText(saved) == "{acceptance-fixture", "The damaged test catalog was not preserved.");
        Console.WriteLine("PASS Previous catalog restored in the isolated test workspace");
    }

    private sealed class InterruptCopy(CancellationTokenSource cancellation) : IProgress<FileProgress>
    {
        public void Report(FileProgress progress)
        {
            if (progress.ProcessedBytes > 0)
                cancellation.Cancel();
        }
    }

    private static async Task PublisherResumeAsync(LiveWorkspace workspace, MediaFixture linux)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var verifier = new PublisherSignatureVerifier();
        var listing = await new OfficialImages(verifier).ListAsync("kali", timeout.Token);
        var image = listing.Images.SingleOrDefault(item => item.Sha256 == linux.Sha256)
            ?? throw new InvalidOperationException("The publisher feed did not return the checked local Kali installer.");
        if (verifier.Available)
            Expect(image.Provenance?.Verification == MediaVerification.PublisherSignature, "The publisher bytes were not signature-checked.");
        Directory.CreateDirectory(workspace.Store.Media);
        var partial = Path.Combine(workspace.Store.Media, $"{image.Sha256}.partial");
        File.Copy(linux.Iso, partial);
        const int tail = 4 * 1024 * 1024;
        var total = new FileInfo(partial).Length;
        using (var file = File.OpenWrite(partial))
            file.SetLength(total - tail);

        var progress = new RangeProgress(total - tail);
        var verified = await workspace.Lab.DownloadInstallerAsync(image with { Bytes = total }, progress, timeout.Token);
        Expect(progress.Transferred && File.Exists(verified.Path) && verified.Sha256 == image.Sha256,
            "Publisher transfer was not resumed and verified.");
        var cache = await workspace.Lab.DownloadInstallerAsync(image, cancellation: timeout.Token);
        Expect(cache == verified, "The publisher ISO was not reused.");
        await workspace.Lab.DeleteUnusedInstallerAsync(image.Sha256);
    }

    private sealed class RangeProgress(long offset) : IProgress<DownloadProgress>
    {
        internal bool Transferred { get; private set; }

        public void Report(DownloadProgress value)
        {
            if (value.Verifying)
                return;
            Expect(value.ReceivedBytes > offset, "Publisher ignored the Range request.");
            Transferred = true;
        }
    }
}
