using System.Globalization;
using System.Text;

namespace VMCrate.Core;

public sealed partial class LabService
{
    public async Task MountLinuxIsolationProbeAsync(Guid id)
    {
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var vm = FindMachine(state, id);
        await RequireLinuxProbeAsync(state, vm);
        var root = Path.Combine(store.Root, "probe");
        var probeId = Guid.NewGuid();
        var folder = LocalPaths.MakeDirectory(Path.Combine(root, probeId.ToString("N")));
        var path = Path.Combine(folder, "VMCrate-isolation.sh");
        try
        {
            using var resource = typeof(LabService).Assembly.GetManifestResourceStream("VMCrate.Core.LinuxIsolationProbe.sh")!;
            using var reader = new StreamReader(resource);
            await File.WriteAllTextAsync(path, (await reader.ReadToEndAsync()).ReplaceLineEndings("\n"), new UTF8Encoding(false));
            await SendFilesCoreAsync(store.Location(state, vm), [path]);
        }
        finally { LocalPaths.RemoveDirectory(root, probeId); }
    }

    public async Task<IsolationObservation> ReadLinuxIsolationAsync(Guid id, string result)
    {
        var reading = ParseLinuxIsolation(result);
        using var workspaceLock = store.Lock();
        var state = store.Load();
        var snapshot = await RequireLinuxProbeAsync(state, FindMachine(state, id));
        return snapshot with { Guest = reading, GuestReported = true, GuestObservedUtc = DateTimeOffset.UtcNow };
    }

    internal static GuestNetworkCheck ParseLinuxIsolation(string result)
    {
        var fields = result.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields is not ["VMCRATE_ISOLATION1", _, _, _, _])
            throw new LabException("error.guestProbe");
        var counts = new int[4];
        for (var i = 0; i < counts.Length; i++)
        {
            if (!int.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out counts[i]) || counts[i] > 65535)
                throw new LabException("error.guestProbe");
        }
        return new(counts[0], counts[1] + counts[2], null, counts[1], counts[2], counts[3]);
    }

    private async Task<IsolationObservation> RequireLinuxProbeAsync(LabState state, Machine vm)
    {
        if (vm.Guest != GuestFamily.Linux || vm.Network == NetworkMode.Updates)
            throw new LabException("error.guestProbe");
        var measured = await ObserveIsolationAsync(state, vm);
        if (measured.Host is not { State: PowerState.Running, NetworkPolicyMatches: true })
            throw new LabException("error.isolation");
        return measured;
    }
}
