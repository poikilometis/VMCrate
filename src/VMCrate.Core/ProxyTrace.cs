using System.Globalization;
using System.Text.Json.Serialization;

namespace VMCrate.Core;

public enum ProxyTarget { WindowsUpdate, Kali, Debian, Ubuntu, Fedora, Alpine }
public enum ProbeCheck { Unknown, Passed, Failed }
public enum ProxyResult { Reachable, NoRoute, GatewayUnresolved, ProxyUnavailable, RelayUnavailable,
    ProxyAuthentication, ProxyRejected, TlsOrTunnelFailure, RepositoryStatus, NoResponse }

public sealed record ProxyGuestTrace(ProbeCheck Route, ProbeCheck Gateway, ProbeCheck ProxyTcp, ProbeCheck RelayTcp,
    int Connect, int Http, int CurlExit, long DownloadBytes = 0, long ElapsedMs = 0)
{
    public const int SampleLimit = 256 * 1024;
    [JsonIgnore]
    public double? KiBPerSecond => CurlExit == 0 && Http is 200 or 206 && DownloadBytes > 0 && ElapsedMs > 0
        ? DownloadBytes * 1000d / (1024 * ElapsedMs) : null;

    [JsonIgnore]
    public ProxyResult Result => Connect == 407 ? ProxyResult.ProxyAuthentication
        : Http is >= 200 and < 400 && CurlExit is 0 or 63 ? ProxyResult.Reachable
        : Route == ProbeCheck.Failed ? ProxyResult.NoRoute
        : Gateway == ProbeCheck.Failed ? ProxyResult.GatewayUnresolved
        : RelayTcp == ProbeCheck.Failed ? ProxyResult.RelayUnavailable
        : ProxyTcp == ProbeCheck.Failed ? ProxyResult.ProxyUnavailable
        : Connect >= 400 ? ProxyResult.ProxyRejected
        : Connect == 200 && Http == 0 ? ProxyResult.TlsOrTunnelFailure
        : Http >= 100 ? ProxyResult.RepositoryStatus
        : ProxyResult.NoResponse;

    public static ProxyGuestTrace Parse(string line)
    {
        var fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var measured = fields is ["VMCRATE_PROXY2", _, _, _, _, _, _, _, _, _];
        if (!measured && fields is not ["VMCRATE_PROXY1", _, _, _, _, _, _, _])
            throw new LabException("error.proxyResult");
        var values = new int[7];
        for (var i = 0; i < values.Length; i++)
        {
            if (!int.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
                throw new LabException("error.proxyResult");
        }
        if (values.Take(4).Any(value => value is < 0 or > 2) ||
            values.Skip(4).Take(2).Any(value => value != 0 && value is < 100 or > 599) ||
            values[6] is < 0 or > 255)
            throw new LabException("error.proxyResult");

        long bytes = 0, elapsed = 0;
        if (measured && (!long.TryParse(fields[8], NumberStyles.None, CultureInfo.InvariantCulture, out bytes) ||
            !long.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out elapsed) ||
            bytes > SampleLimit || elapsed > 18000))
            throw new LabException("error.proxyResult");
        return new((ProbeCheck)values[0], (ProbeCheck)values[1], (ProbeCheck)values[2], (ProbeCheck)values[3],
            values[4], values[5], values[6], bytes, elapsed);
    }
}

public sealed record ProxyHostTrace(PowerState State, bool PolicyMatches, HostNetworkObservation? Network,
    HostSwitch? InternalSwitch, ProbeCheck ProxyTcp)
{
    public bool RouteDiffersFrom(ProxyHostTrace? previous) =>
        Network is { RouteKnown: true, Interface: not null } current &&
        previous?.Network is { RouteKnown: true, Interface: not null } before &&
        (!current.Interface.Equals(before.Interface, StringComparison.OrdinalIgnoreCase) ||
         Changed(current.SourceAddress, before.SourceAddress) || Changed(current.IsTunnel, before.IsTunnel) ||
         Changed(InternalSwitch?.Id, previous.InternalSwitch?.Id));

    private static bool Changed(string? current, string? before) =>
        current is not null && before is not null && !current.Equals(before, StringComparison.OrdinalIgnoreCase);

    private static bool Changed<T>(T? current, T? before) where T : struct =>
        current is T value && before is T previous && !EqualityComparer<T>.Default.Equals(value, previous);
}

public sealed record ProxyTraceOptions(ProxyTarget Target, int? RelayPort = null, bool MeasureDownload = false)
{
    public bool CanMeasureDownload => Target is ProxyTarget.Kali or ProxyTarget.Debian or ProxyTarget.Ubuntu;
    public Uri Repository => new(Target switch
    {
        ProxyTarget.WindowsUpdate => "https://catalog.update.microsoft.com/",
        ProxyTarget.Kali => "https://http.kali.org/kali/dists/kali-rolling/InRelease",
        ProxyTarget.Debian => "https://deb.debian.org/debian/dists/stable/InRelease",
        ProxyTarget.Ubuntu => "https://archive.ubuntu.com/ubuntu/dists/noble/InRelease",
        ProxyTarget.Fedora => "https://mirrors.fedoraproject.org/",
        ProxyTarget.Alpine => "https://dl-cdn.alpinelinux.org/alpine/",
        _ => throw new LabException("error.proxyOptions")
    });

    internal void Validate()
    {
        if (!Enum.IsDefined(Target) || RelayPort is < 1 or > 65535 || (MeasureDownload && !CanMeasureDownload))
            throw new LabException("error.proxyOptions");
    }
}
