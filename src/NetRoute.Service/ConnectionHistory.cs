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
/// here already shows what is connected right now. This keeps the recent past as well, so
/// "what did that app talk to while I was away" has an answer.</para>
///
/// <para><b>What leaves the machine, exactly.</b> Recording is local: it reads this PC's connection
/// table and keeps the result in memory. Names are the only thing that touches the network, and
/// they do: a reverse-DNS (PTR) query for an address goes to the DNS server Windows uses, which
/// means that server (and so your ISP, if it is the resolver) can see which addresses were asked
/// about. To keep that to the minimum, an address is looked up only when someone asks for the
/// history (the History panel being open), only for the addresses in the list being returned, once
/// each (failures are not retried for a while), and never for addresses on your own network. With
/// the panel closed there are no lookups at all. The cache is capped and only a few lookups run at
/// once, so a dead resolver costs a trickle of timeouts rather than a flood.</para>
///
/// <para>Recording itself is cheap-ish but not free (it snapshots every socket), so it samples every
/// <see cref="BusyInterval"/> while the history is being looked at and every <see cref="IdleInterval"/>
/// otherwise: the past stays available, but a PC nobody is looking at is not polled every five seconds.</para>
///
/// <para>It lives in memory and starts empty when the service restarts: it is a window on what is
/// happening, not a log to be kept.</para>
/// </summary>
public sealed class ConnectionHistory(
    IConnectionSource connections,
    ILogger<ConnectionHistory> logger,
    Func<IPAddress, CancellationToken, Task<string>>? resolve = null,
    TimeProvider? time = null)
    : BackgroundService, IConnectionHistory
{
    private const int Keep = 300;
    private const int KeepNames = 1000;
    private const int MaxLookups = 4;

    /// <summary>How long a failed lookup is believed before it is tried again.</summary>
    public static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(15);

    /// <summary>Sampling period while someone has looked at the history recently.</summary>
    public static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(5);

    /// <summary>Sampling period when nobody has: enough to have a past to show, little enough to be unnoticeable.</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long after the last request the history counts as being looked at.</summary>
    public static readonly TimeSpan BusyFor = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<IPAddress, CancellationToken, Task<string>> _resolve =
        resolve ?? (async (address, ct) => (await Dns.GetHostEntryAsync(address.ToString(), ct)).HostName);
    private readonly ConcurrentDictionary<(string App, string Address), Entry> _seen = new();
    private readonly ConcurrentDictionary<string, NameEntry> _names = new();
    private int _lookupsInFlight;
    private long _askedAtTicks = long.MinValue;   // UTC ticks of the last Recent() call

    /// <summary>An empty name means no answer; Pending marks a lookup under way.</summary>
    private sealed record NameEntry(string Name, DateTimeOffset At, bool Pending = false);

    private sealed record Entry(string App, string Address, string? Adapter, TransportProtocol Protocol, DateTimeOffset First)
    {
        public DateTimeOffset Last { get; set; }
        public int Count { get; set; }
    }

    /// <summary>How many addresses have a remembered answer, for tests and diagnostics.</summary>
    public int CachedNames => _names.Count;

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
            await Task.Delay(NextInterval(), stoppingToken);
        }
    }

    /// <summary>True while the history has been asked for recently, so there is a reader.</summary>
    public bool BeingRead => Volatile.Read(ref _askedAtTicks) is var at && at != long.MinValue
                             && _time.GetUtcNow().UtcTicks - at < BusyFor.Ticks;

    /// <summary>How long to wait before the next sample.</summary>
    public TimeSpan NextInterval() => BeingRead ? BusyInterval : IdleInterval;

    public void Record()
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
        }

        // Keep the window small; the oldest go first.
        if (_seen.Count > Keep)
        {
            foreach (var old in _seen.OrderBy(e => e.Value.Last).Take(_seen.Count - Keep).ToList())
            {
                _seen.TryRemove(old.Key, out _);
            }
        }
        TrimNames();
    }

    /// <summary>
    /// Names are kept per address and addresses come and go, so without this the cache only grows.
    /// Addresses no longer in the window go first, then the oldest answers.
    /// </summary>
    private void TrimNames()
    {
        if (_names.Count <= KeepNames)
        {
            return;
        }
        var shown = _seen.Keys.Select(k => k.Address).ToHashSet();
        var doomed = _names
            .Where(n => !n.Value.Pending)
            .OrderBy(n => shown.Contains(n.Key))
            .ThenBy(n => n.Value.At)
            .Take(_names.Count - KeepNames);
        foreach (var name in doomed.ToList())
        {
            _names.TryRemove(name.Key, out _);
        }
    }

    /// <summary>
    /// Looks a name up once per address, in the background, and remembers the answer. Past the
    /// lookup limit an address is simply left alone until a later request, rather than queued.
    /// Only ever called for addresses a reader is about to see.
    /// </summary>
    private void Name(IPAddress address)
    {
        var key = address.ToString();
        var now = _time.GetUtcNow();
        if (_names.TryGetValue(key, out var known) && (known.Name.Length > 0 || known.Pending || now - known.At < FailureTtl))
        {
            return;
        }
        if (LocalNetwork.Contains(address))
        {
            _names[key] = new("your network", now);
            return;
        }
        if (Interlocked.Increment(ref _lookupsInFlight) > MaxLookups)
        {
            Interlocked.Decrement(ref _lookupsInFlight);
            return;
        }
        _names[key] = new(string.Empty, now, Pending: true);   // claim it, so only one lookup happens
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                _names[key] = new(await _resolve(address, timeout.Token), _time.GetUtcNow());
            }
            catch (Exception)
            {
                // No name. Kept with its time, so a dead resolver is asked again only after the TTL.
                _names[key] = new(string.Empty, _time.GetUtcNow());
            }
            finally
            {
                Interlocked.Decrement(ref _lookupsInFlight);
            }
        });
    }

    public IReadOnlyList<ConnectionHistoryDto> Recent(int limit)
    {
        Interlocked.Exchange(ref _askedAtTicks, _time.GetUtcNow().UtcTicks);
        var shown = _seen.Values
            .OrderByDescending(e => e.Last)
            .Take(Math.Clamp(limit, 1, Keep))
            .ToList();

        // Names are fetched for what is being shown and nothing else; the answers arrive for the
        // reader's next request. Until then the address stands in for the name.
        foreach (var address in shown.Select(e => e.Address).Distinct())
        {
            if (IPAddress.TryParse(address, out var parsed))
            {
                Name(parsed);
            }
        }

        return shown
            .Select(e => new ConnectionHistoryDto(
                e.App,
                _names.TryGetValue(e.Address, out var known) && known.Name.Length > 0 ? known.Name : e.Address,
                e.Address,
                e.Adapter,
                e.Protocol,
                e.First,
                e.Last,
                e.Count))
            .ToList();
    }
}
