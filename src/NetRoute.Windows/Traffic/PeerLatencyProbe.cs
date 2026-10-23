using System.Net;
using NetRoute.Core.Traffic;

namespace NetRoute.Windows.Traffic;

/// <summary>A program on one network, with the remote addresses it is exchanging UDP with.</summary>
/// <param name="Local">The program's address on that network. The probe leaves from it.</param>
/// <param name="Peers">Busiest first.</param>
public sealed record PeerTarget(int ProcessId, ulong AdapterLuid, IPAddress Local, IReadOnlyList<IPAddress> Peers);

/// <summary>What the probe can say about one program on one network. <see cref="Rtt"/> is set only for <see cref="LatencyKind.PeerProbe"/>.</summary>
public readonly record struct PeerLatency(LatencyKind Kind, RttSummary? Rtt);

/// <summary>
/// Round-trip time for programs that have nothing for ESTATS to measure, which in practice means
/// games: they talk UDP, and ESTATS is TCP-only. The only way left to put a number on the line is
/// to ping the other end of the traffic.
///
/// <para>That is a different measurement from a TCP round trip and the rest of the product says so
/// (see <see cref="LatencyKind.PeerProbe"/>). It is also often impossible: many game servers
/// drop ICMP. When no peer answers, the result is <see cref="LatencyKind.NotMeasurable"/>, never
/// zero and never the last good value.</para>
///
/// <para>Cost is held low on purpose, as it is a diagnostic that runs inside a service on a
/// gaming PC:</para>
/// <list type="bullet">
/// <item>Only the programs the caller names (those moving data, and only when something is
/// reading), at most <see cref="MaxPeersPerApp"/> peers each and <see cref="MaxPingsPerPass"/>
/// pings per pass, passes at least <see cref="MinInterval"/> apart.</item>
/// <item>Answers are cached per network and peer for <see cref="RefreshAfter"/>. Peers that don't
/// answer are retried after <see cref="RetryAfter"/>, backing off to <see cref="GiveUpRetry"/>
/// after <see cref="GiveUpAfter"/> failures.</item>
/// <item>The pings run on a background task, never on the caller's thread, because a silent peer
/// costs a full timeout.</item>
/// <item>An answer older than <see cref="FreshFor"/> is no longer shown.</item>
/// </list>
/// Each ping leaves from the program's own address and is refused if Windows would route it out of
/// another adapter (see <see cref="NativeIcmpPinger"/>).
/// </summary>
public sealed class PeerLatencyProbe
{
    public const int MaxPeersPerApp = 3;
    public const int MaxPingsPerPass = 4;
    public const int MaxCached = 256;
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1000);
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan GiveUpRetry = TimeSpan.FromMinutes(5);
    public const int GiveUpAfter = 3;

    private readonly IPinger _pinger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<Action, Task> _schedule;
    private readonly object _gate = new();
    private readonly Dictionary<(ulong Luid, IPAddress Peer), PeerState> _peers = [];
    private DateTimeOffset _lastPass;
    private bool _running;

    private sealed class PeerState
    {
        public DateTimeOffset LastAttempt;
        public DateTimeOffset LastReply;
        public int Rtt;
        public int Failures;
        public bool WrongInterface;
        public DateTimeOffset LastWanted;
    }

    public PeerLatencyProbe() : this(new NativeIcmpPinger(), () => DateTimeOffset.UtcNow, work => Task.Run(work))
    {
    }

    internal PeerLatencyProbe(IPinger pinger, Func<DateTimeOffset> clock, Func<Action, Task> schedule)
    {
        _pinger = pinger;
        _clock = clock;
        _schedule = schedule;
    }

    /// <summary>How many (network, peer) pairs are remembered. For tests.</summary>
    public int CachedCount
    {
        get
        {
            lock (_gate)
            {
                return _peers.Count;
            }
        }
    }

    /// <summary>
    /// What is known right now for each of the given programs, from the cache; this never waits
    /// for a ping. If a pass is due it is started in the background and its answers appear on a
    /// later call. Programs the probe has no verdict on yet are absent from the result.
    /// </summary>
    public IReadOnlyDictionary<(int Pid, ulong Luid), PeerLatency> Measure(IReadOnlyList<PeerTarget> targets)
    {
        lock (_gate)
        {
            var now = _clock();
            var trimmed = targets.Select(t => t with { Peers = t.Peers.Take(MaxPeersPerApp).ToList() }).ToList();
            foreach (var peer in trimmed.SelectMany(t => t.Peers.Select(p => (t.AdapterLuid, Peer: p))))
            {
                if (_peers.TryGetValue(peer, out var known))
                {
                    known.LastWanted = now;
                }
            }

            if (!_running && now - _lastPass >= MinInterval)
            {
                var work = Plan(trimmed, now);
                if (work.Count > 0)
                {
                    _lastPass = now;
                    _running = true;
                    _ = _schedule(() => Run(work));
                }
            }

            return Verdicts(trimmed, now);
        }
    }

    /// <summary>
    /// Who to ping this pass: everything due, never-tried peers first and then the longest-waiting,
    /// busiest program first within each. Without that ordering the busiest program's peers would
    /// come due again every refresh and shut everyone else out of the small per-pass budget.
    /// </summary>
    private List<(IPAddress Source, ulong Luid, IPAddress Peer)> Plan(List<PeerTarget> targets, DateTimeOffset now)
    {
        var seen = new HashSet<(ulong, IPAddress)>();
        var due = new List<(IPAddress Source, ulong Luid, IPAddress Peer)>();
        foreach (var target in targets)
        {
            foreach (var peer in target.Peers)
            {
                if (seen.Add((target.AdapterLuid, peer)) && Due((target.AdapterLuid, peer), now))
                {
                    due.Add((target.Local, target.AdapterLuid, peer));
                }
            }
        }
        return due
            .OrderBy(d => _peers.TryGetValue((d.Luid, d.Peer), out var state) ? state.LastAttempt : default)
            .Take(MaxPingsPerPass)
            .ToList();
    }

    private bool Due((ulong, IPAddress) key, DateTimeOffset now)
    {
        if (!_peers.TryGetValue(key, out var state))
        {
            return true;
        }
        var wait = state.Failures == 0 ? RefreshAfter : state.Failures >= GiveUpAfter ? GiveUpRetry : RetryAfter;
        return now - state.LastAttempt >= wait;
    }

    private void Run(List<(IPAddress Source, ulong Luid, IPAddress Peer)> work)
    {
        try
        {
            foreach (var (source, luid, peer) in work)
            {
                PingOutcome outcome;
                try
                {
                    outcome = _pinger.Ping(source, peer, luid, Timeout);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    outcome = new(PingStatus.Failed);
                }

                lock (_gate)
                {
                    var now = _clock();
                    if (!_peers.TryGetValue((luid, peer), out var state))
                    {
                        Evict();
                        _peers[(luid, peer)] = state = new PeerState { LastWanted = now };
                    }
                    state.LastAttempt = now;
                    state.WrongInterface = outcome.Status == PingStatus.WrongInterface;
                    if (outcome.Status == PingStatus.Reply)
                    {
                        state.Failures = 0;
                        state.Rtt = outcome.Milliseconds;
                        state.LastReply = now;
                    }
                    else
                    {
                        // A wrong-interface refusal won't fix itself in a minute, so it goes straight to the long wait.
                        state.Failures = outcome.Status == PingStatus.WrongInterface ? GiveUpAfter : state.Failures + 1;
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    /// <summary>Drops the entry nobody has asked about for longest, so the cache can't grow without bound.</summary>
    private void Evict()
    {
        if (_peers.Count < MaxCached)
        {
            return;
        }
        var oldest = _peers.OrderBy(p => p.Value.LastWanted).First().Key;
        _peers.Remove(oldest);
    }

    private Dictionary<(int, ulong), PeerLatency> Verdicts(List<PeerTarget> targets, DateTimeOffset now)
    {
        var result = new Dictionary<(int, ulong), PeerLatency>();
        foreach (var target in targets)
        {
            var fresh = new List<uint>();
            var failed = 0;
            foreach (var peer in target.Peers)
            {
                if (!_peers.TryGetValue((target.AdapterLuid, peer), out var state) || state.LastAttempt == default)
                {
                    continue;   // not tried yet: no verdict, which is different from "can't be measured"
                }
                if (state.Failures > 0)
                {
                    failed++;
                }
                else if (state.LastReply != default && now - state.LastReply <= FreshFor)
                {
                    fresh.Add((uint)state.Rtt);
                }
                // An answer older than FreshFor with nothing newer is simply unknown until the next ping lands.
            }

            var key = (target.ProcessId, target.AdapterLuid);
            if (fresh.Count > 0)
            {
                result[key] = new(LatencyKind.PeerProbe, RttSummary.Of(fresh));
            }
            else if (failed > 0)
            {
                result[key] = new(LatencyKind.NotMeasurable, null);
            }
        }
        return result;
    }
}
