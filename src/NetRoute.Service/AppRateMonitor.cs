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
/// program's TCP round-trip time alongside when Windows can provide it.
/// </summary>
public sealed class AppRateMonitor(IAdapterSource adapters, ILogger<AppRateMonitor> logger) : IHostedService, IAppRateSource, IAppTotalsSource, IDisposable
{
    private readonly ProcessNetworkMeter _meter = new(adapters);
    private readonly ConnectionLatencyProbe _latency = new();
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
        var latency = LatencyFor(rates);
        return new(true, null, rates.Select(r =>
        {
            RttSummary? rtt = r.AdapterLuid is { } luid && latency.TryGetValue((r.ProcessId, luid), out var found) ? found : null;
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
                MedianRttMs = rtt?.MedianMs,
                WorstRttMs = rtt?.WorstMs,
                RttConnections = rtt?.Connections
            };
        }).ToList());
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

    public void Dispose()
    {
        _latency.Dispose();
        _meter.Dispose();
    }
}
