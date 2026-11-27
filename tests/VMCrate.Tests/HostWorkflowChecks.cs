using System.IO;
using System.Text.Json;
using VMCrate.Core;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static class HostWorkflowChecks
{
    internal static async Task DiagnosticsAndProxy()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        await lab.CreateInstallerAsync(new InstallerOptions(new("Builder", 4, 2), GuestFamily.Windows, true,
            temp.File("setup.iso", "fixture"), 64));
        var builder = (await lab.ObserveAsync()).Machines.Single().Id;
        var spec = UpdateProxy.Create(Guid.NewGuid(), "8.8.4.4", 8443);
        await ErrorAsync("error.updateEndpoint", () => Task.FromResult(UpdateProxy.Create(spec.SwitchId, "10.0.0.5", 443)));
        await lab.SetUpdateProxyAsync(builder, spec);
        var after = (await lab.ObserveAsync()).Machines.Single();
        Expect(after.Network == NetworkMode.Updates && after.Proxy == spec, "The proxy policy was not saved.");
        await ErrorAsync("error.updateBuilder", () => lab.SetNetworkAsync(builder, NetworkMode.Private));
        await ErrorAsync("error.guestProbe", () => lab.CheckGuestNetworkAsync(builder, "fixture", "fixture", null));
        await lab.SetUpdateProxyAsync(builder, null);
        Expect((await lab.ObserveAsync()).Machines.Single().Network == NetworkMode.Offline, "Disabling proxy did not detach egress.");

        var report = await lab.DiagnoseAsync(spec.Address);
        Expect(report.Network?.IsTunnel == true && report.Snapshot.Host is not null, "Network diagnostics missing.");
        var path = Path.Combine(temp.Root, "diagnostics.json");
        report.Export(path);
        var output = File.ReadAllText(path);
        Expect(!output.Contains("fixture tunnel") && !output.Contains("192.0.2.1") && !output.Contains(temp.Root),
            "Export includes host-local identifiers.");
        try
        {
            await lab.CheckGuestNetworkAsync(builder, "fixture", "fixture", "8.8.4.4");
            throw new InvalidOperationException("A non-peer target reached the guest probe.");
        }
        catch (LabException error) when (error.Key == "error.guestProbe") { }
    }

    internal static async Task CleanSessionAndStorage()
    {
        using var temp = new TemporaryDirectory();
        var backend = new FakeHypervisor();
        var lab = new LabService(temp.Root, backend, new FakeInbox());
        await lab.ImportTemplateAsync("Base", GuestFamily.Linux, true, temp.File("base.vhdx", "baseline"));
        var template = (await lab.ObserveAsync()).Templates.Single();
        var input = temp.File("ticket.txt", "synthetic report input");
        var id = await lab.PrepareCleanSessionAsync(template.Id, new MachineOptions("Disposable", 4, 2), [input]);
        var draft = backend.Machines[id];
        Expect(draft.Power == PowerState.Off && draft.Inbox != null && draft.Location.Machine.Disposable,
            "Preparing a clean session did not stage its read-only media.");
        var usage = lab.InspectStorage();
        Expect(usage.Templates.Single().Dependents.SequenceEqual(new[] { "Disposable" }) &&
            usage.Machines.Single().InboxBytes > 0, "Storage graph did not include the session's base or inbox.");

        await lab.StartAsync(id);
        await ErrorAsync("error.updateBuilder", () => lab.SetUpdateProxyAsync(id, UpdateProxy.Create(Guid.NewGuid(), "8.8.4.4", 443)));
        var report = Path.Combine(temp.Root, "session.json");
        await lab.ExportReportAsync(report, id, []);
        var evidence = JsonSerializer.Deserialize<AnalysisReport>(File.ReadAllText(report), JsonDefaults.Options)!;
        Expect(evidence.Files.Single().Name == "ticket.txt", "The report lost the staged file fingerprint.");
        await lab.EndDisposableAsync(id);
        Expect(lab.InspectStorage().Templates.Single().Dependents.Count == 0 && File.Exists(input),
            "Closing the session affected a base or original file.");
    }
}
