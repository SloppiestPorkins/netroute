using System.Net.NetworkInformation;

namespace NetRoute.Service;

public sealed class ServiceWorker(NetRouteEngine engine, NamedPipeServer server, ILogger<ServiceWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private DateTimeOffset _lastNetworkSignal;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        try
        {
            await ReconcileSafely(stoppingToken);
            var pipe = server.RunAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                var signaled = _signal.WaitAsync(stoppingToken);
                await Task.WhenAny(delay, signaled);
                if (signaled.IsCompletedSuccessfully)
                {
                    var remaining = TimeSpan.FromSeconds(1.5) - (DateTimeOffset.UtcNow - _lastNetworkSignal);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, stoppingToken);
                }
                await ReconcileSafely(stoppingToken);
            }
            await pipe;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Signal();
    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Signal();
    private void Signal() { _lastNetworkSignal = DateTimeOffset.UtcNow; if (_signal.CurrentCount == 0) _signal.Release(); }
    private async Task ReconcileSafely(CancellationToken ct)
    {
        try { await engine.ReconcileAsync(ct); }
        catch (Exception ex) { logger.LogError(ex, "Policy reconciliation failed"); }
    }
}
