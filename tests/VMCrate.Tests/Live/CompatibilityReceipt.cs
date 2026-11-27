using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Reflection;
using VMCrate.Core;

namespace VMCrate.Tests;

internal static partial class LiveAcceptance
{
    private static void WriteCompatibility(string root, HostReadiness readiness, string outcome, string[] passed,
        string[] required)
    {
        var build = typeof(LabService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0];
        var report = new
        {
            Version = 1, VmCrate = build, RecordedUtc = DateTimeOffset.UtcNow,
            WindowsBuild = readiness.Capacity?.WindowsBuild,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            readiness.HyperV, readiness.SandboxAvailable, Outcome = outcome, Passed = passed,
            Required = required
        };
        OutputFile.WriteNew(Path.Combine(root, "compatibility.json"), ".json",
            stream => JsonSerializer.Serialize(stream, report, JsonDefaults.Options));
    }
}
