using System.IO;
using VMCrate.Core;
using VMCrate.Windows;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal sealed class LiveWorkspace(string root, IHypervisor host)
{
    internal LabService Lab { get; private set; } = new(root, host, new UdfInboxWriter());
    internal StateStore Store { get; } = new(root);
    internal IHypervisor Host => host;
    internal string Root => root;

    internal void Reopen() => Lab = new LabService(root, host, new UdfInboxWriter());

    internal async Task<VmObservation> ObserveAsync(Guid id)
    {
        var snapshot = await Lab.ObserveAsync();
        return snapshot.Host?.Machines.SingleOrDefault(vm => vm.Id == id)
            ?? throw new InvalidOperationException(snapshot.HostError?.Message ?? "Test VM observation missing.");
    }

    internal async Task<Guid> ImportAsync(string name, GuestFamily family, bool secureBoot, string path)
    {
        await Lab.ImportTemplateAsync(name, family, secureBoot, path);
        return (await Lab.ObserveAsync()).Templates.Single(item => item.Name == name).Id;
    }

    internal async Task VerifyBootAsync(Guid id)
    {
        var state = Store.Load();
        var location = Store.Location(state, state.Machines.Single(vm => vm.Id == id));
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            if (await host.CheckBootAsync(location))
                return;
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
        throw new InvalidOperationException("The prepared guest did not report a heartbeat within 120 seconds.");
    }

    internal async Task CleanAsync()
    {
        if (!File.Exists(Path.Combine(root, "state.json")))
            return;
        var snapshot = await Lab.ObserveAsync();
        Expect(snapshot.Host is not null, snapshot.HostError?.Message ?? "Cleanup requires Hyper-V observation.");
        foreach (var vm in snapshot.Machines)
        {
            var power = snapshot.StateOf(vm.Id);
            if (power is PowerState.Running or PowerState.Saved)
                await Lab.PowerOffAsync(vm.Id);
            await Lab.DeleteMachineAsync(vm.Id);
        }

        foreach (var finding in (await Lab.InspectRecoveryAsync()).Where(item => item.Kind == RecoveryKind.EmptySegment))
            await Lab.RemoveUnusedPrivateSegmentAsync(finding.Id);
        foreach (var template in snapshot.Templates)
            await Lab.DeleteTemplateAsync(template.Id);

        foreach (var job in Lab.ReadBuilds())
        {
            Lab.ForgetBuild(job.Id);
            await Lab.DeleteUnusedInstallerAsync(job.Options.Hash);
        }

        var remaining = await host.ObserveRecoveryAsync(Store.Load().WorkspaceId);
        Expect(remaining.Machines.Length == 0 && remaining.Segments.Length == 0, "A generated host resource remains.");
    }
}
