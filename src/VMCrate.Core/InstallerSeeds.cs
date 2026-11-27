namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task EjectBuildMediaAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var build = state.Builds.SingleOrDefault(job => job.Id == id && job.Stage == BuildStage.Install)
            ?? throw new LabException("error.build");
        var builder = store.Location(state, FindMachine(state, build.Id));
        RequireNoDiskRestore(state, id);
        await hypervisor.EjectInstallerAsync(builder);
        if (build.Options.AnswerFile is not null)
            await hypervisor.SetInboxAsync(builder, null);
    }

    private async Task ArmBuilderSeedAsync(MachineLocation vm, string source, string expected, CancellationToken cancellation)
    {
        LocalPaths.MakeDirectory(vm.InboxDirectory);
        var mounted = Path.Combine(vm.InboxDirectory, $"{vm.Machine.Id:N}.iso");
        LocalPaths.RequireOrdinaryPath(mounted);
        if (!File.Exists(mounted))
        {
            var local = LocalPaths.RequireFile(source, ".iso");
            WorkspaceCapacity.RequireSpace(mounted, new FileInfo(local).Length);
            var stage = Path.Combine(vm.InboxDirectory, $"{Guid.NewGuid():N}.tmp");
            try
            {
                var hash = await FileDigests.CopyAndHashAsync(local, stage, cancellation: cancellation);
                if (hash != expected)
                    throw new LabException("error.isoMismatch");
                File.Move(stage, mounted);
            }
            finally { File.Delete(stage); }
        }
        else if ((await FileDigests.InspectAsync(mounted, cancellation)).Sha256 != expected)
        {
            throw new LabException("error.isoMismatch");
        }

        await hypervisor.ArmInstallerSeedAsync(vm, mounted);
    }
}
