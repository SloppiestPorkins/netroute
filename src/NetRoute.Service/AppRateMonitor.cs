using NetRoute.Core.Adapters;
using NetRoute.Ipc;
using NetRoute.Windows.Traffic;

namespace NetRoute.Service;

public interface IAppRateSource
{
    AppRatesDto GetRates();
}

public sealed class NoAppRates : IAppRateSource
{
    public AppRatesDto GetRates() => new(false, "Per-app speeds aren't measured here.", []);
}

/// <summary>Runs the ETW network meter for the life of the service and serves its readings.</summary>
public sealed class AppRateMonitor(IAdapterSource adapters, ILogger<AppRateMonitor> logger) : IHostedService, IAppRateSource, IDisposable
{
    private readonly ProcessNetworkMeter _meter = new(adapters);

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
        _meter.Dispose();
        return Task.CompletedTask;
    }

    public AppRatesDto GetRates()
    {
        if (!_meter.Running)
        {
            return new(false, _meter.Problem, []);
        }
        return new(true, null, _meter.Rates().Select(r => new AppRateDto
        {
            ProcessId = r.ProcessId,
            ProcessName = r.ProcessName,
            ExecutablePath = r.ExecutablePath,
            PackageFamilyName = r.PackageFamilyName,
            InterfaceLuid = r.AdapterLuid,
            InterfaceName = r.AdapterName,
            DownBytesPerSecond = r.DownBytesPerSecond,
            UpBytesPerSecond = r.UpBytesPerSecond
        }).ToList());
    }

    public void Dispose() => _meter.Dispose();
}
