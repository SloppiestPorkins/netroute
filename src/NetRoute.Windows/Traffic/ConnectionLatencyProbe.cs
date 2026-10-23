using System.Net;
using System.Net.Sockets;
using NetRoute.Core.Traffic;

namespace NetRoute.Windows.Traffic;

/// <summary>One connection's latest round-trip time, with the local address it leaves from so it can be tied to an adapter.</summary>
public sealed record RttSample(int ProcessId, IPAddress Local, uint Milliseconds);

/// <summary>
/// Per-connection TCP round-trip times from Windows' own TCP stack (ESTATS), for the programs
/// that are moving data right now.
///
/// <para>Switching ESTATS on makes the kernel keep extra state for that connection, and the
/// live machine routinely has 300 or more connections. Doing it for all of them on every poll
/// would be a standing cost in a service that runs for weeks, to produce numbers nobody is
/// looking at. So the work is bounded three ways, and each bound exists for a reason:</para>
/// <list type="bullet">
/// <item>Only established connections of the programs the caller names (the ones with live
/// traffic, busiest first), and only to the internet. A LAN or loopback round trip says
/// nothing about a line, and the speeds panel leaves that traffic out too.</item>
/// <item><see cref="MaxPerProcess"/> connections per program: a torrent client with 400 sockets
/// would otherwise take every slot, and a dozen samples are plenty to tell a healthy app from a
/// stalled one.</item>
/// <item><see cref="MaxTracked"/> connections in total and <see cref="MaxEnablesPerPass"/>
/// newly enabled per pass, so a burst of new connections is spread over several passes rather
/// than landing as one spike.</item>
/// </list>
///
/// <para>Connections already enabled are remembered and never re-enabled. One that falls out of
/// the wanted set while still open is switched back off, because forgetting it without doing so
/// would leave the kernel collecting for a connection nobody reads. A pass runs at most every
/// <see cref="MinInterval"/> and only when something asks (the GUI polling for speeds), so a
/// service with no window open does none of this. Connections left enabled when the last
/// reader goes away stay enabled until they close, bounded by <see cref="MaxTracked"/>.</para>
///
/// <para>Failure is quiet. If Windows refuses to enable collection (no rights, or a build
/// without ESTATS), <see cref="Problem"/> is set once and the probe stops trying; speeds carry
/// on without latency. A connection that closed between the table read and the call fails with
/// not-found, which is normal and ignored.</para>
/// </summary>
public sealed class ConnectionLatencyProbe : IDisposable
{
    public const int MaxPerProcess = 16;
    public const int MaxTracked = 128;
    public const int MaxEnablesPerPass = 32;
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);

    private const uint ERROR_INVALID_FUNCTION = 1;
    private const uint ERROR_ACCESS_DENIED = 5;
    private const uint ERROR_NOT_SUPPORTED = 50;

    // Per-connection failures are routine, so giving up needs either a refusal that can only
    // mean "never" or a run of failures with not one success to set against them.
    private const int GiveUpAfterRefusals = 8;
    private const int GiveUpAfterFailedPasses = 3;

    private readonly ITcpEstats _api;
    private readonly Func<IReadOnlyList<SocketEntry>> _table;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<(IPEndPoint Local, IPEndPoint Remote), SocketEntry> _tracked = [];
    private IReadOnlyList<RttSample> _last = [];
    private DateTimeOffset _lastPassAt;
    private bool _everEnabled;
    private int _refusals;
    private int _failedPasses;

    public ConnectionLatencyProbe() : this(new NativeTcpEstats(), ConnectionTable.Snapshot, () => DateTimeOffset.UtcNow)
    {
    }

    internal ConnectionLatencyProbe(ITcpEstats api, Func<IReadOnlyList<SocketEntry>> table, Func<DateTimeOffset> clock)
    {
        _api = api;
        _table = table;
        _clock = clock;
    }

    /// <summary>Why latency isn't being measured, in words for a log; null while it is (or while nothing has failed).</summary>
    public string? Problem { get; private set; }

    /// <summary>How many connections currently have collection switched on. For tests and diagnostics.</summary>
    public int TrackedCount
    {
        get
        {
            lock (_gate)
            {
                return _tracked.Count;
            }
        }
    }

    /// <summary>
    /// Round-trip samples for the given programs, busiest first. Cached for <see cref="MinInterval"/>,
    /// so callers can ask as often as they like. A connection enabled on this pass has no sample
    /// yet and shows up on the next.
    /// </summary>
    public IReadOnlyList<RttSample> Measure(IReadOnlyList<int> busiestFirst)
    {
        lock (_gate)
        {
            if (Problem is not null)
            {
                return [];
            }
            var now = _clock();
            if (now - _lastPassAt < MinInterval)
            {
                return _last;
            }
            _lastPassAt = now;
            try
            {
                _last = Pass(busiestFirst);
                _failedPasses = 0;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _last = [];
                if (++_failedPasses >= GiveUpAfterFailedPasses)
                {
                    Problem = $"Reading TCP statistics failed repeatedly: {ex.Message}";
                }
            }
            return _last;
        }
    }

    private List<RttSample> Pass(IReadOnlyList<int> busiestFirst)
    {
        var rank = new Dictionary<int, int>();
        foreach (var pid in busiestFirst)
        {
            rank.TryAdd(pid, rank.Count);
        }

        var table = _table();
        var open = table.Where(s => s.Remote is not null).Select(Key).ToHashSet();

        // Already-enabled connections sort ahead of new ones within a program, so a program
        // with more than MaxPerProcess sockets keeps the ones it has rather than rotating.
        var wanted = new List<SocketEntry>();
        var perProcess = new Dictionary<int, int>();
        foreach (var socket in table.Where(s => IsCandidate(s) && rank.ContainsKey(s.ProcessId))
                     .OrderBy(s => rank[s.ProcessId])
                     .ThenBy(s => _tracked.ContainsKey(Key(s)) ? 0 : 1))
        {
            if (wanted.Count >= MaxTracked)
            {
                break;
            }
            var used = perProcess.GetValueOrDefault(socket.ProcessId);
            if (used >= MaxPerProcess)
            {
                continue;
            }
            perProcess[socket.ProcessId] = used + 1;
            wanted.Add(socket);
        }

        var wantedKeys = wanted.Select(Key).ToHashSet();
        foreach (var (key, socket) in _tracked.ToList())
        {
            if (wantedKeys.Contains(key))
            {
                continue;
            }
            if (open.Contains(key))
            {
                _api.SetCollection(socket, false);
            }
            _tracked.Remove(key);
        }

        var enabledNow = new HashSet<(IPEndPoint, IPEndPoint)>();
        foreach (var socket in wanted.Where(s => !_tracked.ContainsKey(Key(s))).Take(MaxEnablesPerPass))
        {
            var error = _api.SetCollection(socket, true);
            if (error == 0)
            {
                _tracked[Key(socket)] = socket;
                enabledNow.Add(Key(socket));
                _everEnabled = true;
            }
            else if (error is ERROR_ACCESS_DENIED or ERROR_NOT_SUPPORTED or ERROR_INVALID_FUNCTION)
            {
                Problem = error == ERROR_ACCESS_DENIED
                    ? "Windows wouldn't let NetRoute switch on TCP statistics (access denied)."
                    : $"This version of Windows doesn't offer TCP statistics (error {error}).";
                return [];
            }
            else if (!_everEnabled && ++_refusals >= GiveUpAfterRefusals)
            {
                Problem = $"Windows refused to switch on TCP statistics for {_refusals} connections in a row (last error {error}).";
                return [];
            }
        }

        var samples = new List<RttSample>();
        foreach (var socket in wanted)
        {
            var key = Key(socket);
            if (!_tracked.ContainsKey(key) || enabledNow.Contains(key))
            {
                continue;
            }
            var reading = _api.Read(socket);

            // Zero means no round trip has been timed yet (or one under a millisecond, which on an
            // internet connection is not a real reading); all-ones is the unset value.
            if (reading.Error == 0 && reading.RttCount > 0 && reading.SampleRttMs is > 0 and < uint.MaxValue)
            {
                samples.Add(new RttSample(socket.ProcessId, Normalize(socket.Local.Address), reading.SampleRttMs));
            }
        }
        return samples;
    }

    private static bool IsCandidate(SocketEntry s)
        => s.Protocol == TransportProtocol.Tcp
           && s.TcpState == "Established"
           && s.Remote is { } remote
           && !IPAddress.IsLoopback(remote.Address)
           && !LocalNetwork.Contains(remote.Address);

    private static (IPEndPoint, IPEndPoint) Key(SocketEntry s) => (s.Local, s.Remote!);

    /// <summary>
    /// Link-local addresses carry a scope ID in the table that the meter's adapter map doesn't,
    /// so it is dropped here to make the two agree.
    /// </summary>
    private static IPAddress Normalize(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(address.GetAddressBytes()) : address;

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var socket in _tracked.Values)
            {
                try
                {
                    _api.SetCollection(socket, false);
                }
                catch (Exception)
                {
                    // Shutting down: the kernel drops the state when the connection closes anyway.
                }
            }
            _tracked.Clear();
        }
    }
}
