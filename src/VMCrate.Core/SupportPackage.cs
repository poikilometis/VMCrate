using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace VMCrate.Core;

public sealed record SupportHost(bool Windows11X64, int? WindowsBuild, HyperVReadiness HyperV, bool SandboxAvailable,
    bool? FirmwareVirtualization, bool? RestartRequired, int? LogicalProcessors, long? FreeMemoryBytes);

public sealed record SupportVm(GuestFamily Guest, bool SecureBoot, int MemoryGiB, int CpuCount, NetworkMode Profile,
    bool TemplateClone, bool Disposable, PowerState State, int? Findings);

public sealed record SupportBuild(GuestFamily Guest, int DiskGiB, BuildStage Stage, SupportBuildStep[] History);
public sealed record SupportBuildStep(BuildStage Stage, long? ElapsedMs, bool? Succeeded, string? Code);
public sealed record SupportOperation(RecordedOperation Operation, long? ElapsedMs, string? Code);

public sealed record SupportPackage(int Version, string VmCrateVersion, DateTimeOffset CreatedUtc, SupportHost Host,
    bool CatalogReadable, HostDiagnosticReport? Diagnostics, SupportVm? Machine, SupportBuild? Build, SupportOperation[] Operations)
{
    public string Preview() => JsonSerializer.Serialize(this, JsonDefaults.Options);

    public void Export(string path) => OutputFile.WriteNew(path, ".zip", stream =>
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        using var entry = archive.CreateEntry("support.json").Open();
        using var writer = new StreamWriter(entry, new UTF8Encoding(false));
        writer.Write(Preview());
    });
}

public sealed partial class LabService
{
    public async Task<SupportPackage> BuildSupportPackageAsync(HostReadiness readiness, Guid? resource = null)
    {
        using var workspaceLock = store.Lock();
        var capacity = readiness.Capacity;
        var host = new SupportHost(readiness.Windows11X64, capacity?.WindowsBuild, readiness.HyperV,
            readiness.SandboxAvailable, capacity?.FirmwareVirtualization, capacity?.RestartRequired,
            capacity?.LogicalProcessors, capacity?.FreeMemoryBytes);
        var assembly = typeof(LabService).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? assembly.GetName().Version!.ToString(3);
        var created = DateTimeOffset.UtcNow;
        LabState state;
        try { state = store.Load(); }
        catch (LabException error) when (error.Key is "error.state" or "error.stateVersion")
        {
            return new(1, version, created, host, false, null, null, null, []);
        }

        var vm = state.Machines.SingleOrDefault(item => item.Id == resource);
        var build = resource is null ? state.Builds.LastOrDefault() : state.Builds.SingleOrDefault(item => item.Id == resource);
        var diagnostics = await DiagnoseAsync(state, vm?.Proxy?.Address);
        var observed = vm is null ? null : diagnostics.Snapshot.Host?.Machines.SingleOrDefault(item => item.Id == vm.Id);
        var machine = vm is null ? null : new SupportVm(vm.Guest, vm.SecureBoot, vm.MemoryGiB, vm.CpuCount, vm.Network,
            vm.TemplateId is not null, vm.Disposable, observed?.State ?? PowerState.Unavailable, observed?.Findings.Length);
        var builder = build is null ? null : new SupportBuild(build.Options.Installer.Guest, build.Options.Installer.DiskGiB,
            build.Stage, build.History.Select((step, index) => new SupportBuildStep(step.Stage, step.ElapsedMs,
                index == 0 && step.ElapsedMs is null ? null : step.ErrorKey is null, SupportCode(step.ErrorKey))).ToArray());

        var operations = state.Operations.Where(item => resource is null || item.Resource == resource).TakeLast(30)
            .Select(item => new SupportOperation(item.Operation, item.ElapsedMs, SupportCode(item.Code))).ToArray();
        return new(1, version, created, host, true, diagnostics.Summarize(), machine, builder, operations);
    }

    private static string? SupportCode(string? code) => code switch
    {
        "error.host" or "error.backend" or "error.timeout" or "error.operation" or "error.off" or
        "error.isolation" or "error.build" or "error.buildBoot" or "error.buildSpace" or "error.isoMismatch" or
        "error.updateSwitch" or "error.updateAcl" or "error.updateBuilder" or "error.guestProbe" or
        "error.proxyBuilder" or "error.proxyDirect" or "status.cancelled" => code,
        _ when Enum.GetNames<BuildStage>().Contains(code) || Enum.GetNames<ProbeCheck>().Contains(code) ||
               Enum.GetNames<ProxyResult>().Contains(code) => code,
        _ => null
    };
}
