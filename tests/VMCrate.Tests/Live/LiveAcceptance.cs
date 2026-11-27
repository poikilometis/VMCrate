using System.IO;
using System.Text.Json;
using VMCrate.Core;
using VMCrate.Windows;
using static VMCrate.Tests.Checks;

namespace VMCrate.Tests;

internal static partial class LiveAcceptance
{
    private sealed record MediaFixture(string Iso, string Sha256, string Vhdx);
    private sealed record Fixtures(MediaFixture Windows, MediaFixture Linux);

    internal static async Task<int> RunAsync(string run, string fixturePath, bool checkWindowsGuest = false)
    {
        var root = LocalPaths.FullPath(run);
        LocalPaths.RequireOrdinaryPath(root);
        Directory.CreateDirectory(root);
        using var marker = new FileStream(Path.Combine(root, "acceptance.lock"), FileMode.CreateNew, FileAccess.Write);

        var host = new PowerShellHypervisor();
        var readiness = await host.ReadHostReadinessAsync();
        string[] required = ["installer-and-recovery", "clones-and-private-lab", "publisher-resume"];
        if (readiness.HyperV != HyperVReadiness.Ready)
        {
            WriteCompatibility(root, readiness, "Blocked", [], required);
            Console.WriteLine($"BLOCKED Hyper-V readiness {readiness.HyperV} · {readiness.Detail}");
            return 2;
        }
        Console.WriteLine($"PASS Hyper-V host access · Windows Sandbox available {readiness.SandboxAvailable}");

        using var input = File.OpenRead(LocalPaths.RequireFile(fixturePath, ".json"));
        var data = JsonSerializer.Deserialize<Fixtures>(input, JsonDefaults.Options)
            ?? throw new InvalidOperationException("Missing Hyper-V media fixtures.");
        var windows = await ValidateAsync(data.Windows);
        var linux = await ValidateAsync(data.Linux);
        var bytes = new[] { windows.Iso, linux.Iso, windows.Vhdx, linux.Vhdx }.Sum(file => new FileInfo(file).Length)
            + new FileInfo(linux.Iso).Length;
        Expect(new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace > bytes + 32L * 1024 * 1024 * 1024,
            "Insufficient space for disposable disks and validated media.");

        var credentials = checkWindowsGuest ? ReadWindowsGuestCredentials() : null;
        var checkedGroups = new List<string>();
        if (await RunCaseAsync(root, required[0], host, workspace => InstallerRecoveryAsync(workspace, windows, linux)) == 1)
            checkedGroups.Add(required[0]);
        if (await RunCaseAsync(root, required[1], host, workspace => CloneLifecycleAsync(workspace, windows, linux, credentials)) == 1)
            checkedGroups.Add(required[1]);
        if (await RunCaseAsync(root, required[2], host, workspace => PublisherResumeAsync(workspace, linux)) == 1)
            checkedGroups.Add(required[2]);
        var passed = checkedGroups.Count;
        if (credentials is not null && checkedGroups.Contains(required[1]))
            checkedGroups.Add("windows-guest-observations");
        WriteCompatibility(root, readiness, passed == required.Length ? "Passed" : "Failed", checkedGroups.ToArray(), required);
        Console.WriteLine($"{passed}/3 automated Hyper-V acceptance groups passed.");
        if (credentials is null)
            Console.WriteLine("PENDING Windows guest inbox, route and CBS inventory observations. Use -CheckWindowsGuest on the test host.");
        Console.WriteLine("PENDING guest console installation, Linux guest inbox, peer isolation and proxy checks, Windows Sandbox UI.");
        return passed == 3 ? 0 : 1;
    }

    private static async Task<MediaFixture> ValidateAsync(MediaFixture item)
    {
        var iso = LocalPaths.RequireFile(item.Iso, ".iso");
        var disk = LocalPaths.RequireFile(item.Vhdx, ".vhdx");
        var evidence = await FileDigests.InspectAsync(iso);
        Expect(evidence.Sha256.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase), $"Official SHA-256 mismatch for {evidence.Name}.");
        Console.WriteLine($"PASS Official ISO SHA-256 · {evidence.Name}");
        return new(iso, evidence.Sha256, disk);
    }

    private static async Task<int> RunCaseAsync(string root, string name, IHypervisor host, Func<LiveWorkspace, Task> check)
    {
        var directory = Path.Combine(root, name);
        Expect(!Directory.Exists(directory), "Live acceptance requires an empty test directory.");
        var workspace = new LiveWorkspace(directory, host);
        Console.WriteLine($"RUN {name}");
        var passed = true;
        try { await check(workspace); }
        catch (Exception error)
        {
            Console.WriteLine($"FAIL {name} · {error.Message}");
            passed = false;
        }
        try { await workspace.CleanAsync(); }
        catch (Exception error)
        {
            Console.WriteLine($"FAIL {name} cleanup · {error.Message}");
            passed = false;
        }
        if (passed)
            Console.WriteLine($"PASS {name}");
        return passed ? 1 : 0;
    }
}
