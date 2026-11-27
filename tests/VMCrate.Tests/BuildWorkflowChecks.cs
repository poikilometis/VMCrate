using System.IO;
using System.Security.Cryptography;
using System.Text;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class BuildWorkflowChecks
{
    internal static async Task Lifecycle()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        var iso = temp.File("setup.iso", "checked installer");
        var answer = temp.File("Autounattend.xml", "<unattend />");
        var job = lab.QueueBuild(Options(iso, Hash("checked installer"), answer));
        await lab.ContinueBuildAsync(job.Id);
        Expect(lab.ReadBuilds().Single().Stage == BuildStage.Provision, "The installer was not verified.");
        var cache = lab.InspectStorage().Media.Single();
        await ErrorAsync("error.inUse", () => lab.DeleteUnusedInstallerAsync(cache.Sha256));

        backend.FailCreate = true;
        await ErrorAsync("error.backend", () => lab.ContinueBuildAsync(job.Id));
        Expect(lab.ReadBuilds().Single().History.Last().ErrorKey == "error.backend", "The failure was not recorded.");
        backend.FailCreate = false;
        lab = new LabService(temp.Root, backend, new FakeInbox());
        await lab.ContinueBuildAsync(job.Id);
        Expect(backend.Machines.Single().Key == job.Id && backend.Machines[job.Id].Inbox is not null,
            "Resume did not reuse the builder identity or attach the answer file.");

        await ErrorAsync("error.buildBoot", () => lab.ContinueBuildAsync(job.Id));
        backend.BootVerified = true;
        await lab.ContinueBuildAsync(job.Id);
        await lab.ContinueBuildAsync(job.Id);
        await lab.ContinueBuildAsync(job.Id);
        var result = (await lab.ObserveAsync()).Templates.Single();
        Expect(result.Id == job.Id && result.Revision == "r1" && result.CreatedUtc is not null,
            "The build did not publish exactly one revision.");
        Expect(lab.ReadBuilds().Single().Stage == BuildStage.Complete, "The job is not complete.");
        backend.MediaAttached = true;
        await ErrorAsync("error.inUse", () => lab.DeleteUnusedInstallerAsync(cache.Sha256));
        backend.MediaAttached = false;
        await lab.DeleteUnusedInstallerAsync(cache.Sha256);
        Expect(lab.InspectStorage().Media.Count == 0 && File.Exists(iso), "Cleanup removed the wrong file.");
    }

    internal static async Task BadMedia()
    {
        using var temp = new TemporaryDirectory();
        var lab = new LabService(temp.Root, new FakeHypervisor(), new FakeInbox());
        var job = lab.QueueBuild(Options(temp.File("setup.iso", "incorrect"), Hash("correct")));
        await ErrorAsync("error.isoMismatch", () => lab.ContinueBuildAsync(job.Id));
        Expect(lab.ReadBuilds().Single().Stage == BuildStage.Verify && lab.InspectStorage().Media.Count == 0,
            "Unverified media reached the cache or advanced the job.");
        Expect((await lab.ObserveAsync()).Machines.Count == 0, "Unverified media reached a VM.");
        lab.ForgetBuild(job.Id);
        Expect(lab.ReadBuilds().Count == 0, "A rejected draft cannot be removed.");
    }

    internal static async Task CancelAndResume()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor { BootVerified = true };
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        var iso = temp.File("setup.iso", "known installer");
        var job = lab.QueueBuild(Options(iso, Hash("known installer")));
        var space = lab.EstimateBuildSpace(iso, job.Options.Hash, 64);
        Expect(space.CacheBytes == new FileInfo(iso).Length &&
            space.FullDiskGrowthBytes == space.CacheBytes + 128L * 1024 * 1024 * 1024, "Build space estimate lost a copy.");
        await CancelStageAsync(lab, job.Id, BuildStage.Verify);
        Expect(lab.InspectStorage().Media.Count == 0 && !Directory.EnumerateFiles(Path.Combine(temp.Root, "media")).Any(),
            "Cancelled verification left an unverified ISO.");
        await lab.ContinueBuildAsync(job.Id);
        Expect(lab.EstimateBuildSpace(iso, job.Options.Hash, 64).CacheBytes == 0, "Cache reuse was omitted.");
        await lab.ContinueBuildAsync(job.Id);
        await lab.ContinueBuildAsync(job.Id);
        await CancelStageAsync(lab, job.Id, BuildStage.Capture);
        Expect((await lab.ObserveAsync()).Templates.Count == 0 && File.Exists(backend.Machines[job.Id].Disk),
            "Cancelled capture lost the builder disk or published a template.");
        await lab.ContinueBuildAsync(job.Id);
        Expect((await lab.ObserveAsync()).Templates.Single().Id == job.Id, "Retry published the wrong template.");
    }

    private static async Task CancelStageAsync(LabService lab, Guid id, BuildStage stage)
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            await lab.ContinueBuildAsync(id, new CancelOnProgress(cancellation), cancellation.Token);
            throw new InvalidOperationException("File stage ignored cancellation.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        var job = lab.ReadBuilds().Single();
        Expect(job.Stage == stage && job.History.Last().ErrorKey == "status.cancelled",
            "Cancelled file stage advanced or lost its history.");
    }

    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => cancellation.Cancel();
    }

    private static TemplateBuildOptions Options(string iso, string hash, string? answer = null) =>
        new("Test baseline", "r1", new InstallerOptions(new MachineOptions("Test", 4, 2), GuestFamily.Windows, true, iso, 64), hash, answer);

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
