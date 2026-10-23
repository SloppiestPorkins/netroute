using NetRoute.Core.Adapters;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;
using NetRoute.Windows.Traffic;

namespace NetRoute.Service;

public interface IAppRateSource
{
    AppRatesDto GetRates();
}

/// <summary>Running byte totals per program and connection, for the usage history.</summary>
public interface IAppTotalsSource
{
    IReadOnlyList<ProcessTotal> GetTotals();
}

public sealed class NoAppRates : IAppRateSource
{
    public AppRatesDto GetRates() => new(false, "Per-app speeds aren't measured here.", []);
}

/// <summary>
/// Runs the ETW network meter for the life of the service and serves its readings, with each
/// program's round-trip time alongside: its own TCP connections' when it has any, otherwise a ping
/// to the far end of its UDP traffic (games), which says so.
/// </summary>
public sealed class AppRateMonitor(IAdapterSource adapters, ILogger<AppRateMonitor> logger) : IHostedService, IAppRateSource, IAppTotalsSource, IDisposable
{
    private readonly ProcessNetworkMeter _meter = new(adapters);
    private readonly ConnectionLatencyProbe _latency = new();
    private readonly PeerLatencyProbe _peers = new();
    private bool _latencyProblemLogged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _meter.Start();
        if (_meter.Problem is { } problem)
        {
            logger.LogWarning("Per-app network metering is off: {Problem}", problem);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _latency.Dispose();
        _meter.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Running totals, after refreshing the deltas that feed them.</summary>
    public IReadOnlyList<ProcessTotal> GetTotals()
    {
        if (!_meter.Running)
        {
            return [];
        }
        _meter.Rates();
        return _meter.Totals();
    }

    public AppRatesDto GetRates()
    {
        if (!_meter.Running)
        {
            return new(false, _meter.Problem, []);
        }
        var rates = _meter.Rates();
        var tcp = LatencyFor(rates);
        var udp = PeerLatencyFor(rates, tcp);
        return new(true, null, rates.Select(r =>
        {
            RttSummary? tcpRtt = null;
            PeerLatency? peerRtt = null;
            if (r.AdapterLuid is { } luid)
            {
                tcpRtt = tcp.TryGetValue((r.ProcessId, luid), out var found) ? found : null;
                peerRtt = udp.TryGetValue((r.ProcessId, luid), out var peer) ? peer : null;
            }
            return ToDto(r, tcpRtt, peerRtt);
        }).ToList());
    }

    /// <summary>
    /// One program's rate row with its latency. The program's own TCP round trip wins when there is
    /// one, because it is the real measurement; a peer ping is only the fallback for programs with
    /// nothing TCP to time. A peer result that is "not measurable" carries no number at all.
    /// </summary>
    public static AppRateDto ToDto(ProcessRate r, RttSummary? tcp, PeerLatency? peer)
    {
        var kind = LatencyKind.None;
        RttSummary? rtt = null;
        if (tcp is { } own)
        {
            (kind, rtt) = (LatencyKind.Tcp, own);
        }
        else if (peer is { } p)
        {
            (kind, rtt) = (p.Kind, p.Kind == LatencyKind.PeerProbe ? p.Rtt : null);
        }
        return new AppRateDto
        {
            ProcessId = r.ProcessId,
            ProcessName = r.ProcessName,
            ExecutablePath = r.ExecutablePath,
            PackageFamilyName = r.PackageFamilyName,
            InterfaceLuid = r.AdapterLuid,
            InterfaceName = r.AdapterName,
            DownBytesPerSecond = r.DownBytesPerSecond,
            UpBytesPerSecond = r.UpBytesPerSecond,
            LatencyKind = kind,
            MedianRttMs = rtt?.MedianMs,
            WorstRttMs = rtt?.WorstMs,
            RttConnections = rtt?.Connections
        };
    }

    /// <summary>
    /// Round trips per program and adapter, for the programs moving data right now. Asking only
    /// about those is what bounds the probe's work (see <see cref="ConnectionLatencyProbe"/>).
    /// </summary>
    private Dictionary<(int Pid, ulong Luid), RttSummary> LatencyFor(IReadOnlyList<ProcessRate> rates)
    {
        var busiest = rates.OrderByDescending(r => r.DownBytesPerSecond + r.UpBytesPerSecond).Select(r => r.ProcessId).Distinct().ToList();
        var samples = _latency.Measure(busiest);
        if (_latency.Problem is { } problem && !_latencyProblemLogged)
        {
            _latencyProblemLogged = true;
            logger.LogInformation("Per-app latency is off: {Problem}", problem);
        }

        var addresses = _meter.LocalAddresses;
        return samples
            .Where(s => addresses.ContainsKey(s.Local))
            .GroupBy(s => (s.ProcessId, addresses[s.Local].Luid))
            .Select(g => (g.Key, Summary: RttSummary.Of(g.Select(s => s.Milliseconds))))
            .Where(e => e.Summary is not null)
            .ToDictionary(e => e.Key, e => e.Summary!.Value);
    }

    /// <summary>
    /// For programs on an adapter that have UDP traffic but no TCP figure: the far ends of that
    /// traffic, pinged from the program's own address. See <see cref="PeerLatencyProbe"/>.
    /// </summary>
    private IReadOnlyDictionary<(int Pid, ulong Luid), PeerLatency> PeerLatencyFor(
        IReadOnlyList<ProcessRate> rates, Dictionary<(int Pid, ulong Luid), RttSummary> tcp)
        => _peers.Measure(PeerTargets(rates, tcp.Keys.ToHashSet()));

    /// <summary>
    /// Who to ping: programs on a known adapter with UDP peers to talk about and no TCP figure
    /// already. Each target carries that program's own address on the adapter, which is what makes
    /// the ping leave by the same line, and the adapter it must route out of. Busiest first, so
    /// the probe's small budget goes to the programs that matter.
    /// </summary>
    public static IReadOnlyList<PeerTarget> PeerTargets(IReadOnlyList<ProcessRate> rates, IReadOnlySet<(int Pid, ulong Luid)> alreadyMeasured)
        => rates
            .Where(r => r.AdapterLuid is not null && r.LocalAddress is not null && r.UdpPeers.Count > 0)
            .GroupBy(r => (r.ProcessId, Luid: r.AdapterLuid!.Value))
            .Where(g => !alreadyMeasured.Contains(g.Key))
            .OrderByDescending(g => g.Sum(r => r.DownBytesPerSecond + r.UpBytesPerSecond))
            .Select(g => new PeerTarget(g.Key.ProcessId, g.Key.Luid, g.First().LocalAddress!,
                g.SelectMany(r => r.UdpPeers).Distinct().Take(PeerLatencyProbe.MaxPeersPerApp).ToList()))
            .ToList();

    public void Dispose()
    {
        _latency.Dispose();
        _meter.Dispose();
    }
}
