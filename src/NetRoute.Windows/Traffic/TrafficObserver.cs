using System.Net;
using System.Net.Sockets;
using NetRoute.Core.Adapters;
using NetRoute.Core.Traffic;

namespace NetRoute.Windows.Traffic;

/// <summary>A live socket, tied to its program and, where possible, to the adapter it uses.</summary>
public sealed record ObservedConnection(
    TransportProtocol Protocol,
    IPEndPoint Local,
    IPEndPoint? Remote,
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? PackageFamilyName,
    ulong? AdapterLuid,
    string? AdapterName,
    uint? InterfaceIndex,
    bool IsLoopback,
    string? State,
    DateTimeOffset FirstSeen);

/// <summary>
/// What every app is doing on the network right now: its sockets, each attributed to the
/// adapter that owns its local address.
///
/// <para>This is the OBSERVED half of §24. Where an app's traffic leaves is decided by its
/// socket's local address, so the adapter owning that address is where the traffic went.
/// Nothing is inspected per packet (§46): each call is one read of the connection tables.</para>
///
/// <para>It also records when each socket was first seen. That separates a connection opened
/// after a rule changed (which should be on the new network, or it's a leak) from one that
/// was already open (§19: expected to stay where it was until the app reconnects). Sockets
/// already open when the observer starts have unknown age and are treated as pre-existing,
/// because a leak claim has to be certain.</para>
/// </summary>
public sealed class TrafficObserver(IAdapterSource adapters)
{
    private readonly ProcessIdentityCache _processes = new();
    private readonly object _gate = new();
    private Dictionary<string, DateTimeOffset> _firstSeen = [];
    private bool _primed;
    private IReadOnlyList<ObservedConnection>? _last;
    private DateTimeOffset _lastAt;

    public IReadOnlyList<ObservedConnection> Snapshot()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_last is not null && now - _lastAt < TimeSpan.FromSeconds(1))
            {
                return _last;   // status and the live view can both ask within the same second
            }

            var byAddress = new Dictionary<IPAddress, NetworkAdapter>();
            foreach (var adapter in adapters.DiscoverAll())
            {
                foreach (var address in adapter.UnicastAddresses.Append(adapter.Ipv4Address).Append(adapter.Ipv6Address))
                {
                    if (address is not null)
                    {
                        byAddress.TryAdd(Normalize(address), adapter);
                    }
                }
            }

            var seen = new Dictionary<string, DateTimeOffset>();
            var result = new List<ObservedConnection>();
            foreach (var socket in ConnectionTable.Snapshot())
            {
                if (socket.ProcessId <= 4)
                {
                    continue;   // Idle and System
                }

                var local = Normalize(socket.Local.Address);
                var loopback = IPAddress.IsLoopback(local);
                NetworkAdapter? adapter = null;
                if (!loopback && !local.Equals(IPAddress.Any) && !local.Equals(IPAddress.IPv6Any))
                {
                    byAddress.TryGetValue(local, out adapter);
                }

                var key = $"{socket.Protocol}|{socket.Local}|{socket.Remote}|{socket.ProcessId}";
                var first = _firstSeen.TryGetValue(key, out var known) ? known : _primed ? now : DateTimeOffset.MinValue;
                seen[key] = first;

                var (path, package) = _processes.Get(socket.ProcessId);
                result.Add(new ObservedConnection(
                    socket.Protocol, socket.Local, socket.Remote, socket.ProcessId,
                    path is null ? $"Process {socket.ProcessId}" : Path.GetFileNameWithoutExtension(path),
                    path, package, adapter?.Luid, adapter?.Name, adapter?.InterfaceIndex, loopback, socket.TcpState, first));
            }

            _firstSeen = seen;
            _primed = true;
            _processes.Prune(result.Select(c => c.ProcessId));
            _last = result;
            _lastAt = now;
            return result;
        }
    }

    /// <summary>Drops IPv6 scope IDs and unwraps IPv4-mapped addresses from dual-stack sockets.</summary>
    private static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(address.GetAddressBytes()) : address;
    }
}
