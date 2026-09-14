using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using NetRoute.Core.Adapters;

namespace NetRoute.Windows.Traffic;

/// <summary>How fast one program is moving data through one adapter right now.</summary>
public sealed record ProcessRate(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? PackageFamilyName,
    ulong? AdapterLuid,
    string? AdapterName,
    double DownBytesPerSecond,
    double UpBytesPerSecond);

/// <summary>
/// Bytes per second for every program, per adapter, from the kernel's own TCP/IP events (ETW).
///
/// <para>The connection tables say which network a program is on; this says how much it is
/// moving. Only sizes and addresses are counted, never contents (§46). Starting a kernel
/// trace session needs administrator rights, so this runs in the service. It uses its own
/// named kernel session (Windows 8 and later allow several), so it never takes over the
/// shared "NT Kernel Logger" another tool might be using.</para>
/// </summary>
public sealed class ProcessNetworkMeter(IAdapterSource adapters) : IDisposable
{
    private const string SessionName = "NetRoute-Network";

    private readonly ConcurrentDictionary<(int Pid, IPAddress? Local), Counter> _counters = new();
    private readonly ProcessIdentityCache _processes = new();
    private readonly object _gate = new();
    private volatile Dictionary<IPAddress, NetworkAdapter> _local = [];
    private TraceEventSession? _session;
    private Dictionary<(int Pid, IPAddress? Local), (long Down, long Up)> _previous = [];
    private DateTimeOffset _previousAt;
    private IReadOnlyList<ProcessRate> _last = [];
    private DateTimeOffset _lastAt;

    private sealed class Counter
    {
        public long Down;
        public long Up;
    }

    public bool Running => _session is not null && Problem is null;

    /// <summary>Why metering isn't running, in words for the user; null while it is.</summary>
    public string? Problem { get; private set; } = "Not started.";

    public void Start()
    {
        if (_session is not null)
        {
            return;
        }
        RefreshAddresses();
        try
        {
            // A session left behind by a crashed service is replaced rather than joined.
            var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var kernel = session.Source.Kernel;
            kernel.TcpIpRecv += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: true);
            kernel.TcpIpSend += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: false);
            kernel.UdpIpRecv += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: true);
            kernel.UdpIpSend += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: false);
            kernel.TcpIpRecvIPV6 += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: true);
            kernel.TcpIpSendIPV6 += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: false);
            kernel.UdpIpRecvIPV6 += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: true);
            kernel.UdpIpSendIPV6 += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: false);

            _session = session;
            Problem = null;
            new Thread(() =>
            {
                try
                {
                    session.Source.Process();
                }
                catch (Exception ex)
                {
                    Problem = $"Network metering stopped: {ex.Message}";
                }
            }) { IsBackground = true, Name = "NetRoute network meter" }.Start();
        }
        catch (Exception ex)
        {
            Problem = $"Windows wouldn't start network metering: {ex.Message}";
            _session?.Dispose();
            _session = null;
        }
    }

    /// <summary>Rates since the previous call. Cached for a second, so several callers can share one reading.</summary>
    public IReadOnlyList<ProcessRate> Rates()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastAt < TimeSpan.FromSeconds(1))
            {
                return _last;
            }

            var byAddress = RefreshAddresses();
            var current = _counters.ToDictionary(kv => kv.Key, kv => (Interlocked.Read(ref kv.Value.Down), Interlocked.Read(ref kv.Value.Up)));
            var seconds = (now - _previousAt).TotalSeconds;
            var result = new List<ProcessRate>();
            foreach (var (key, (down, up)) in current)
            {
                var (previousDown, previousUp) = _previous.GetValueOrDefault(key);
                var deltaDown = down - previousDown;
                var deltaUp = up - previousUp;
                if (deltaDown <= 0 && deltaUp <= 0)
                {
                    // Idle since the last reading: forget it, so exited processes don't pile up.
                    // A packet arriving right now just starts a fresh counter.
                    _counters.TryRemove(key, out _);
                    continue;
                }
                if (_previousAt == default || seconds <= 0)
                {
                    continue;
                }

                var (path, package) = _processes.Get(key.Pid);
                var adapter = key.Local is null ? null : byAddress.GetValueOrDefault(key.Local);
                result.Add(new ProcessRate(
                    key.Pid, path is null ? $"Process {key.Pid}" : Path.GetFileNameWithoutExtension(path), path, package,
                    adapter?.Luid, adapter?.Name, deltaDown / seconds, deltaUp / seconds));
            }

            _previous = current.Where(kv => _counters.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            _previousAt = now;
            _processes.Prune(_counters.Keys.Select(k => k.Pid));
            _last = result;
            _lastAt = now;
            return result;
        }
    }

    private void Add(int pid, IPAddress a, IPAddress b, int size, bool down)
    {
        if (pid <= 4 || size <= 0 || (IPAddress.IsLoopback(a) && IPAddress.IsLoopback(b)))
        {
            return;
        }
        // Which of the two addresses is ours depends on the event, so ask rather than assume.
        var local = _local;
        IPAddress? mine = local.ContainsKey(a) ? a : local.ContainsKey(b) ? b : null;
        if (mine is not null && NetRoute.Core.Traffic.LocalNetwork.Contains(ReferenceEquals(mine, a) ? b : a))
        {
            return;   // internet traffic only: a copy to the NAS isn't using either connection's ISP
        }
        var counter = _counters.GetOrAdd((pid, mine), _ => new Counter());
        if (down)
        {
            Interlocked.Add(ref counter.Down, size);
        }
        else
        {
            Interlocked.Add(ref counter.Up, size);
        }
    }

    private Dictionary<IPAddress, NetworkAdapter> RefreshAddresses()
    {
        var map = new Dictionary<IPAddress, NetworkAdapter>();
        try
        {
            foreach (var adapter in adapters.DiscoverAll())
            {
                foreach (var address in adapter.UnicastAddresses.Append(adapter.Ipv4Address).Append(adapter.Ipv6Address))
                {
                    if (address is not null && !IPAddress.IsLoopback(address))
                    {
                        map.TryAdd(Normalize(address), adapter);
                    }
                }
            }
        }
        catch (Exception)
        {
            return _local;
        }
        _local = map;
        return map;
    }

    private static IPAddress Normalize(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(address.GetAddressBytes()) : address;

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
