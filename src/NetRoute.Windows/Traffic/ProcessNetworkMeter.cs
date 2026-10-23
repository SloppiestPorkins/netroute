using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using NetRoute.Core.Adapters;

namespace NetRoute.Windows.Traffic;

/// <summary>Everything one program has moved through one adapter since the service started.</summary>
public sealed record ProcessTotal(string App, string? Adapter, long DownBytes, long UpBytes);

/// <summary>How fast one program is moving data through one adapter right now.</summary>
public sealed record ProcessRate(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? PackageFamilyName,
    ulong? AdapterLuid,
    string? AdapterName,
    double DownBytesPerSecond,
    double UpBytesPerSecond)
{
    /// <summary>The program's address on this adapter. A latency probe has to leave from it to travel the same line.</summary>
    public IPAddress? LocalAddress { get; init; }

    /// <summary>
    /// The remote addresses it exchanged UDP with during the last reading, busiest first (at most
    /// <see cref="ProcessNetworkMeter.PeersPerReading"/>). The UDP connection table has no remote
    /// address, so this is the only place they can be learned. Empty for TCP, which has the table.
    /// </summary>
    public IReadOnlyList<IPAddress> UdpPeers { get; init; } = [];
}

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

    /// <summary>How many UDP peers are kept per reading for each program.</summary>
    public const int PeersPerReading = 4;

    // Distinct remote addresses remembered per program within one reading. A game talks to a
    // handful; a torrent client to thousands, and nothing is gained by tracking them all.
    internal const int MaxPeersTracked = 32;

    private readonly ConcurrentDictionary<(int Pid, IPAddress? Local), Counter> _counters = new();
    private readonly ProcessIdentityCache _processes = new();
    private readonly object _gate = new();
    private readonly Dictionary<(string App, string Adapter), (long Down, long Up)> _totals = [];
    private volatile Dictionary<IPAddress, NetworkAdapter> _local = [];
    private TraceEventSession? _session;
    private DateTimeOffset _previousAt;
    private IReadOnlyList<ProcessRate> _last = [];
    private DateTimeOffset _lastAt;
    private DateTimeOffset _addressesAt;
    private volatile bool _addressesChanged;

    internal sealed class Counter
    {
        public long Down;
        public long Up;

        // What the previous reading saw, so a reading is "now minus then" with no second
        // dictionary built per second to hold the last totals. Touched only under the meter's gate.
        public long SeenDown;
        public long SeenUp;

        private Dictionary<IPAddress, long>? _peers;

        /// <summary>Called on the event thread for every UDP packet; the lock is uncontended except for the once-a-second read.</summary>
        public void NotePeer(IPAddress peer, int size)
        {
            lock (this)
            {
                _peers ??= new Dictionary<IPAddress, long>();
                if (_peers.TryGetValue(peer, out var bytes))
                {
                    _peers[peer] = bytes + size;
                }
                else if (_peers.Count < MaxPeersTracked)
                {
                    _peers[peer] = size;
                }
            }
        }

        /// <summary>The busiest peers since the last call, which then starts again.</summary>
        public IReadOnlyList<IPAddress> TakePeers()
        {
            lock (this)
            {
                if (_peers is not { Count: > 0 })
                {
                    return [];
                }
                var top = _peers.OrderByDescending(p => p.Value).Take(PeersPerReading).Select(p => p.Key).ToList();
                _peers.Clear();
                return top;
            }
        }
    }

    public bool Running => _session is not null && Problem is null;

    /// <summary>
    /// Which adapter each local address belongs to, as of the last reading. Lets other
    /// per-connection measurements be tied to the same adapters the rates are grouped by,
    /// using the same address normalisation, rather than rediscovering adapters a second time.
    /// </summary>
    public IReadOnlyDictionary<IPAddress, NetworkAdapter> LocalAddresses => _local;

    /// <summary>Why metering isn't running, in words for the user; null while it is.</summary>
    public string? Problem { get; private set; } = "Not started.";

    public void Start()
    {
        if (_session is not null)
        {
            return;
        }
        RefreshAddresses(force: true);
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        try
        {
            // A session left behind by a crashed service is replaced rather than joined.
            var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var kernel = session.Source.Kernel;
            kernel.TcpIpRecv += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: true);
            kernel.TcpIpSend += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: false);
            kernel.UdpIpRecv += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: true, udp: true);
            kernel.UdpIpSend += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: false, udp: true);
            kernel.TcpIpRecvIPV6 += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: true);
            kernel.TcpIpSendIPV6 += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: false);
            kernel.UdpIpRecvIPV6 += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: true, udp: true);
            kernel.UdpIpSendIPV6 += d => Add(d.ProcessID, d.saddr, d.daddr, d.size, down: false, udp: true);

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

            var firstReading = _previousAt == default;
            var seconds = (now - _previousAt).TotalSeconds;
            var unattributed = false;
            var result = new List<ProcessRate>();
            var idle = (List<(int, IPAddress?)>?)null;
            foreach (var (key, counter) in _counters)
            {
                var down = Interlocked.Read(ref counter.Down);
                var up = Interlocked.Read(ref counter.Up);
                var deltaDown = down - counter.SeenDown;
                var deltaUp = up - counter.SeenUp;
                counter.SeenDown = down;
                counter.SeenUp = up;
                if (firstReading || seconds <= 0)
                {
                    continue;
                }
                if (deltaDown <= 0 && deltaUp <= 0)
                {
                    // Idle since the last reading: forget it, so exited processes don't pile up.
                    // A packet arriving right now just starts a fresh counter.
                    (idle ??= []).Add(key);
                    continue;
                }
                unattributed |= key.Local is null;
                result.Add(new ProcessRate(key.Pid, "", null, null, null, null, deltaDown / seconds, deltaUp / seconds)
                {
                    LocalAddress = key.Local,
                    UdpPeers = counter.TakePeers()
                });
            }
            if (idle is not null)
            {
                foreach (var key in idle)
                {
                    _counters.TryRemove(key, out _);
                }
            }

            // Identity and adapter are looked up only for the rows that are going to be reported,
            // and the adapter map is rebuilt only when it may have changed (see RefreshAddresses).
            var byAddress = RefreshAddresses(unattributed);
            for (var i = 0; i < result.Count; i++)
            {
                var r = result[i];
                var (path, package) = _processes.Get(r.ProcessId);
                var adapter = r.LocalAddress is null ? null : byAddress.GetValueOrDefault(r.LocalAddress);
                var name = path is null ? $"Process {r.ProcessId}" : Path.GetFileNameWithoutExtension(path);
                result[i] = r with { ProcessName = name, ExecutablePath = path, PackageFamilyName = package, AdapterLuid = adapter?.Luid, AdapterName = adapter?.Name };

                // Accumulated here, where each delta is counted exactly once, whoever asked.
                var totalKey = (name, adapter?.Name ?? "");
                var running = _totals.GetValueOrDefault(totalKey);
                _totals[totalKey] = (running.Down + (long)Math.Round(r.DownBytesPerSecond * seconds), running.Up + (long)Math.Round(r.UpBytesPerSecond * seconds));
            }

            _previousAt = now;
            _processes.Prune(_counters.Keys.Select(k => k.Pid));
            _last = result;
            _lastAt = now;
            return result;
        }
    }

    /// <summary>Running totals per program and adapter, for the usage history.</summary>
    public IReadOnlyList<ProcessTotal> Totals()
    {
        lock (_gate)
        {
            return _totals.Select(e => new ProcessTotal(e.Key.App, e.Key.Adapter.Length == 0 ? null : e.Key.Adapter, e.Value.Down, e.Value.Up)).ToList();
        }
    }

    private void Add(int pid, IPAddress a, IPAddress b, int size, bool down, bool udp = false)
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
        if (udp && mine is not null)
        {
            counter.NotePeer(ReferenceEquals(mine, a) ? b : a, size);
        }
        if (down)
        {
            Interlocked.Add(ref counter.Down, size);
        }
        else
        {
            Interlocked.Add(ref counter.Up, size);
        }
    }

    /// <summary>
    /// Adapter enumeration is a full GetAdaptersAddresses walk (addresses, gateways, DNS), so it is
    /// not done every second. It runs when Windows says an address changed, when it has been a
    /// while, or, at most every few seconds, when traffic turns up on an address no adapter owns
    /// (a DHCP renewal that gave a new one).
    /// </summary>
    private Dictionary<IPAddress, NetworkAdapter> RefreshAddresses(bool unattributedTraffic = false, bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && !NetRoute.Core.Traffic.AddressRefresh.ShouldRefresh(now - _addressesAt, _addressesChanged, unattributedTraffic))
        {
            return _local;
        }
        _addressesChanged = false;
        _addressesAt = now;
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

    private void OnAddressChanged(object? sender, EventArgs e) => _addressesChanged = true;

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _session?.Dispose();
        _session = null;
    }
}
