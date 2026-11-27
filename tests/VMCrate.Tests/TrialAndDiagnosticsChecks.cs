using System.IO;
using System.Security.Cryptography;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class TrialAndDiagnosticsChecks
{
    internal static async Task BuildDiagnostics()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor { BootVerified = true };
        backend.ProxyRoute = backend.ProxyRoute with { RouteConflicts = 0 };
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        var iso = temp.File("setup.iso", "iso fixture");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(iso)));
        var job = lab.QueueBuild(new("Builder", null, new(new("Builder", 4, 2), GuestFamily.Linux, false, iso, 32), hash, null));
        await lab.ContinueBuildAsync(job.Id);
        await lab.ContinueBuildAsync(job.Id);
        await lab.SetUpdateProxyAsync(job.Id, UpdateProxy.Create(Guid.NewGuid(), "203.0.113.7", 8080));
        await lab.StartAsync(job.Id);
        backend.ProxyRoute = backend.ProxyRoute with { Interface = "second fixture interface", IsTunnel = false };
        backend.HostProxyTcp = ProbeCheck.Failed;
        await lab.ContinueBuildAsync(job.Id);

        var resumed = new LabService(temp.Root, backend, new FakeInbox()).ReadBuilds().Single();
        var comparison = BuildProxyComparison.From(resumed);
        Expect(comparison is { RouteChanged: true, Baseline: { Passed: true, Source: BuildProxySource.Start },
            Current: { Passed: false, Source: BuildProxySource.Continue } }, "The build did not persist comparable probe results.");
        var evidence = DiagnosticFacts.BuildFailure(resumed);
        Expect(evidence.Any(fact => fact.Id == "proxy-before") && evidence.Any(fact => fact.Id == "proxy-now") &&
            evidence.All(fact => !fact.Text.Contains("fixture interface") && !fact.Text.Contains("203.0.113.7")),
            "AI evidence is missing context or includes private route details.");
        var sample = ProxyGuestTrace.Parse("VMCRATE_PROXY2 1 1 1 0 200 206 0 2048 1000");
        Expect(sample.KiBPerSecond == 2 && sample.Result == ProxyResult.Reachable, "The sample was not parsed correctly.");
        await ErrorAsync("error.proxyResult", Run(() => ProxyGuestTrace.Parse("VMCRATE_PROXY2 1 1 1 0 200 206 0 999999 1000")));
    }

    internal static async Task RevisionTrial()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor { BootVerified = true };
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        await lab.ImportTemplateAsync("Base", GuestFamily.Linux, false, temp.File("source.vhdx", "original"));
        var source = (await lab.ObserveAsync()).Templates.Single();
        var existing = await lab.CreateCloneAsync(source.Id, new("Existing", 4, 2));
        var trial = lab.QueueRevisionTrial(source.Id, "Updated", "r2", new("Trial", 4, 2));
        backend.FailAfterCreate = true;
        await ErrorAsync("error.backend", () => lab.ContinueBuildAsync(trial.Id));
        backend.FailAfterCreate = false;
        var disk = backend.Machines[trial.Id].Disk;
        await lab.ContinueBuildAsync(trial.Id);
        Expect(backend.Machines[trial.Id].Disk == disk && File.ReadAllText(disk) == "original" &&
            backend.Machines[trial.Id].Installer is null, "A trial should resume the checked standalone copy.");
        await lab.StartAsync(trial.Id);
        await lab.RestartGuestAsync(trial.Id);
        Expect(backend.GuestShutdowns == 1 && backend.Machines[trial.Id].Power == PowerState.Running,
            "Guest restart did not use the guest shutdown path.");
        File.WriteAllText(disk, "updated");
        await lab.ContinueBuildAsync(trial.Id);
        await lab.PowerOffAsync(trial.Id);
        await lab.ContinueBuildAsync(trial.Id);
        await ErrorAsync("error.revision", () => lab.PreferRevisionAsync(trial.Id));
        await lab.ContinueBuildAsync(trial.Id);
        await lab.PreferRevisionAsync(trial.Id);
        var done = await lab.ObserveAsync();
        Expect(done.Templates.First() is { Preferred: true, BootVerifiedUtc: not null } candidate &&
            candidate.Id == trial.Id && candidate.Lineage?.PreviousId == source.Id && candidate.Sha256 != source.Sha256,
            "The new candidate was not tested or made preferred.");
        Expect(done.Machines.Single(item => item.Id == existing.Id).TemplateId == source.Id &&
            File.ReadAllText(Path.Combine(temp.Root, "templates", source.Id.ToString("N"), "base.vhdx")) == "original",
            "The trial changed the previous revision or a pinned VM.");
    }
}
