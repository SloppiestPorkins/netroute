using System.Collections.Concurrent;
using System.Net;
using NetRoute.Core.Adapters;
using NetRoute.Windows.Traffic;

namespace NetRoute.Service;

public sealed record LinkQuality(int? LatencyMs, double? LossPercent, bool? Reachable);

public interface ILinkQualitySource
{
    LinkQuality? For(ulong adapterLuid);
}

public sealed class NoLinkQuality : ILinkQualitySource
{
    public LinkQuality? For(ulong adapterLuid) => null;
}

/// <summary>
/// Latency, packet loss and "internet available" for each network card (§8), measured rather
/// than assumed: every 3 seconds each connected adapter pings 1.1.1.1 from its own address.
/// Loss is over the last minute; latency is the median of the last few replies, so one slow
/// reply doesn't make the number jump.
/// </summary>
public sealed class LinkQualityMonitor(IAdapterSource adapters, ILogger<LinkQualityMonitor> logger) : BackgroundService, ILinkQualitySource
{
    private static readonly IPAddress Target = IPAddress.Parse("1.1.1.1");
    private const int Window = 20;
    private readonly ConcurrentDictionary<ulong, Queue<int?>> _samples = new();

    public LinkQuality? For(ulong adapterLuid)
    {
        if (!_samples.TryGetValue(adapterLuid, out var queue))
        {
            return null;
        }
        List<int?> results;
        lock (queue)
        {
            results = queue.ToList();
        }
        if (results.Count == 0)
        {
            return null;
        }

        var recent = results.TakeLast(5).OfType<int>().Order().ToList();
        var lost = results.Count(r => r is null);
        return new LinkQuality(
            recent.Count > 0 ? recent[recent.Count / 2] : null,
            results.Count >= 5 ? Math.Round(100.0 * lost / results.Count, 1) : null,
            results.TakeLast(3).Any(r => r is not null));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var targets = adapters.DiscoverAll()
                    .Where(a => a.State == AdapterState.Connected && a.HasIpv4Gateway && a.Ipv4Address is not null)
                    .ToList();
                var results = await Task.WhenAll(targets.Select(a => Task.Run(() => (a.Luid, Rtt: SafePing(a.Ipv4Address!)), stoppingToken)));
                foreach (var (luid, rtt) in results)
                {
                    var queue = _samples.GetOrAdd(luid, _ => new Queue<int?>());
                    lock (queue)
                    {
                        queue.Enqueue(rtt);
                        while (queue.Count > Window)
                        {
                            queue.Dequeue();
                        }
                    }
                }
                foreach (var gone in _samples.Keys.Except(targets.Select(t => t.Luid)).ToList())
                {
                    _samples.TryRemove(gone, out _);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Link quality probe failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private static int? SafePing(IPAddress source)
    {
        try
        {
            return LinkProbe.Ping(source, Target);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
