using System.Collections.Concurrent;
using System.Net;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;

namespace NetRoute.Service;

public interface IConnectionHistory
{
    IReadOnlyList<ConnectionHistoryDto> Recent(int limit);
}

public sealed class NoConnectionHistory : IConnectionHistory
{
    public IReadOnlyList<ConnectionHistoryDto> Recent(int limit) => [];
}

/// <summary>
/// Where each app has been talking to, with names rather than bare addresses.
///
/// <para>Portmaster's live connection list is the thing its users praise most, and the live view
/// here already shows what is connected right now — this keeps the recent past as well, so
/// "what did that app talk to while I was away" has an answer. Names come from reverse DNS,
/// looked up once per address in the background and cached, because a list of raw IPv6 addresses
/// tells nobody anything.</para>
///
/// <para>It lives in memory and starts empty when the service restarts: it is a window on what is
/// happening, not a log to be kept, and nothing about it leaves the machine.</para>
/// </summary>
public sealed class ConnectionHistory(IConnectionSource connections, ILogger<ConnectionHistory> logger)
    : BackgroundService, IConnectionHistory
{
    private const int Keep = 300;

    private readonly ConcurrentDictionary<(string App, string Address), Entry> _seen = new();
    private readonly ConcurrentDictionary<string, string?> _names = new();

    private sealed record Entry(string App, string Address, string? Adapter, TransportProtocol Protocol, DateTimeOffset First)
    {
        public DateTimeOffset Last { get; set; }
        public int Count { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Record();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Recording connections failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private void Record()
    {
        var now = DateTimeOffset.Now;
        foreach (var connection in connections.GetConnections())
        {
            if (connection.RemoteEndpoint is not { } remote || !IPEndPoint.TryParse(remote, out var endpoint))
            {
                continue;
            }
            var address = endpoint.Address.ToString();
            var entry = _seen.GetOrAdd((connection.ProcessName, address),
                _ => new Entry(connection.ProcessName, address, connection.InterfaceName, connection.Protocol, now));
            entry.Last = now;
            entry.Count++;
            Name(endpoint.Address);
        }

        // Keep the window small; the oldest go first.
        if (_seen.Count > Keep)
        {
            foreach (var old in _seen.OrderBy(e => e.Value.Last).Take(_seen.Count - Keep).ToList())
            {
                _seen.TryRemove(old.Key, out _);
            }
        }
    }

    /// <summary>Looks a name up once per address, in the background, and remembers the answer.</summary>
    private void Name(IPAddress address)
    {
        var key = address.ToString();
        if (_names.ContainsKey(key))
        {
            return;
        }
        if (LocalNetwork.Contains(address))
        {
            _names[key] = "your network";
            return;
        }
        _names[key] = null;   // claim it, so only one lookup happens
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var entry = await Dns.GetHostEntryAsync(key, timeout.Token);
                _names[key] = entry.HostName;
            }
            catch (Exception)
            {
                _names[key] = "";   // no name; don't ask again
            }
        });
    }

    public IReadOnlyList<ConnectionHistoryDto> Recent(int limit)
        => _seen.Values
            .OrderByDescending(e => e.Last)
            .Take(Math.Clamp(limit, 1, Keep))
            .Select(e => new ConnectionHistoryDto(
                e.App,
                _names.GetValueOrDefault(e.Address) is { Length: > 0 } name ? name : e.Address,
                e.Address,
                e.Adapter,
                e.Protocol,
                e.First,
                e.Last,
                e.Count))
            .ToList();
}
