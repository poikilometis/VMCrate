using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VMCrate.Core;

public enum BuildProxySource { Start, Continue, Host, Guest }

public sealed record BuildProxyObservation(DateTimeOffset AtUtc, BuildStage Stage, BuildProxySource Source,
    ProbeCheck Route, bool? Tunnel, bool? PolicyMatches, ProbeCheck Tcp, bool RouteConflict, string? RouteFingerprint,
    long ElapsedMs, ProxyGuestTrace? Guest = null, ProxyTarget? Target = null)
{
    internal const int HistoryLimit = 24;
    [JsonIgnore]
    public bool Passed => Guest is { } guest ? guest.Result == ProxyResult.Reachable
        : Route == ProbeCheck.Passed && PolicyMatches == true && Tcp == ProbeCheck.Passed && !RouteConflict;
}

public sealed record BuildProxyComparison(BuildProxyObservation Current, BuildProxyObservation Baseline, bool? RouteChanged)
{
    public static BuildProxyComparison? From(TemplateBuild build)
    {
        if (build.ProxyChecks.LastOrDefault() is not { } current)
            return null;
        var prior = build.ProxyChecks[..^1].Where(item => (item.Guest is null) == (current.Guest is null) &&
            item.Target == current.Target).ToArray();
        var baseline = prior.LastOrDefault(item => item.Passed) ?? prior.LastOrDefault();
        if (baseline is null)
            return null;
        bool? changed = current.RouteFingerprint is { } route && baseline.RouteFingerprint is { } previous
            ? !route.Equals(previous, StringComparison.Ordinal) : null;
        return new(current, baseline, changed);
    }
}

public sealed partial class LabService
{
    private async Task ObserveBuildProxyAsync(LabState state, Machine builder, BuildProxySource source)
    {
        if (builder is not { Network: NetworkMode.Updates, Proxy: { } proxy } ||
            state.Builds.All(job => job.Id != builder.Id || job.Stage == BuildStage.Complete))
            return;
        var started = Stopwatch.GetTimestamp();
        ProxyHostTrace trace;
        try { trace = await MeasureHostProxyAsync(state, builder, proxy); }
        catch (LabException) { trace = new(PowerState.Unavailable, false, null, null, ProbeCheck.Unknown); }
        RecordProxyHost(state, builder.Id, source, trace, Elapsed(started));
    }

    private void RecordProxyHost(LabState state, Guid id, BuildProxySource source, ProxyHostTrace trace, long elapsed)
    {
        var network = trace.Network;
        var route = network is null ? ProbeCheck.Unknown : network.RouteKnown ? ProbeCheck.Passed : ProbeCheck.Failed;
        // Store a comparable route identifier without sharing addresses or interface names with the assistant.
        var fingerprint = route == ProbeCheck.Passed ? Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            network!.Interface, network.SourceAddress, network.IsTunnel, Switch = trace.InternalSwitch?.Id
        }))) : null;
        AddBuildProxyCheck(state, id, stage => new(DateTimeOffset.UtcNow, stage, source, route, network?.IsTunnel,
            trace.State == PowerState.Unavailable ? null : trace.PolicyMatches, trace.ProxyTcp,
            network?.OverlappingNats == true || network?.RouteConflicts > 0, fingerprint, elapsed));
        RecordOperation(state, id, RecordedOperation.ProxyHost, trace.ProxyTcp.ToString(), elapsed);
        store.Save(state);
    }

    private void RecordProxyGuest(LabState state, Guid id, ProxyGuestTrace trace, ProxyTarget? target)
    {
        var host = state.Builds.SingleOrDefault(job => job.Id == id)?.ProxyChecks.LastOrDefault(item => item.Guest is null);
        AddBuildProxyCheck(state, id, stage => new(DateTimeOffset.UtcNow, stage, BuildProxySource.Guest,
            host?.Route ?? ProbeCheck.Unknown, host?.Tunnel, host?.PolicyMatches, host?.Tcp ?? ProbeCheck.Unknown,
            host?.RouteConflict == true, host?.RouteFingerprint, trace.ElapsedMs, trace, target));
        RecordOperation(state, id, RecordedOperation.ProxyGuest, trace.Result.ToString(), trace.ElapsedMs);
        store.Save(state);
    }

    private static void AddBuildProxyCheck(LabState state, Guid id, Func<BuildStage, BuildProxyObservation> create)
    {
        var index = state.Builds.FindIndex(item => item.Id == id);
        if (index < 0)
            return;
        var job = state.Builds[index];
        state.Builds[index] = job with { ProxyChecks = [.. job.ProxyChecks.TakeLast(BuildProxyObservation.HistoryLimit - 1), create(job.Stage)] };
    }
}
