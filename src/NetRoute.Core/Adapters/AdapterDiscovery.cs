using System.Net;
using System.Net.Sockets;
using NetRoute.Core.Interop;

namespace NetRoute.Core.Adapters;

public sealed class AdapterDiscovery
{
    // IANA ifType values reported by GetAdaptersAddresses.
    private const uint IF_TYPE_ETHERNET_CSMACD = 6;
    private const uint IF_TYPE_SOFTWARE_LOOPBACK = 24;
    private const uint IF_TYPE_PPP = 23;
    private const uint IF_TYPE_TUNNEL = 131;
    private const uint IF_TYPE_IEEE80211 = 71;
    private const uint IF_TYPE_WWANPP = 243;
    private const uint IF_TYPE_WWANPP2 = 244;

    private const uint IfOperStatusUp = 1;

    /// <summary>
    /// Returns every interface on the system, including the virtual and tunnel ones.
    /// Filtering for presentation is the caller's job — Advanced Diagnostics wants
    /// the full set, the setup wizard does not.
    /// </summary>
    public IReadOnlyList<NetworkAdapter> DiscoverAll()
    {
        return IpHelper.Enumerate().Select(Map).ToList();
    }

    /// <summary>Adapters worth offering the user as a Gaming or Downloads target.</summary>
    public IReadOnlyList<NetworkAdapter> DiscoverSelectable()
    {
        return DiscoverAll().Where(a => a.IsSelectableAsRole).ToList();
    }

    /// <summary>
    /// Re-resolves a previously persisted role target by LUID, falling back to GUID.
    /// Returns null when the adapter is genuinely gone, which the caller should surface
    /// as <see cref="AdapterState.Missing"/> rather than silently substituting another one.
    /// </summary>
    public NetworkAdapter? Resolve(ulong luid, string? guid = null)
    {
        var all = DiscoverAll();
        return all.FirstOrDefault(a => a.Luid == luid)
               ?? (guid is null ? null : all.FirstOrDefault(a => string.Equals(a.Guid, guid, StringComparison.OrdinalIgnoreCase)));
    }

    private static NetworkAdapter Map(IpHelper.RawAdapter raw)
    {
        var kind = ClassifyKind(raw);

        var ipv4 = raw.UnicastAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var ipv6 = raw.UnicastAddresses.FirstOrDefault(a =>
            a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal);

        return new NetworkAdapter
        {
            Luid = raw.Luid,
            Guid = raw.Guid,
            Name = raw.FriendlyName,
            Description = raw.Description,
            Kind = kind,
            State = ClassifyState(raw, ipv4, ipv6, kind),
            InterfaceIndex = raw.IfIndex,
            InterfaceIndexV6 = raw.Ipv6IfIndex,
            Ipv4Address = ipv4,
            Ipv6Address = ipv6,
            Gateways = raw.Gateways,
            DnsServers = raw.DnsServers,
            LinkSpeedBps = raw.ReceiveLinkSpeed,
            Ipv4Metric = raw.Ipv4Metric,
            Ipv6Metric = raw.Ipv6Metric
        };
    }

    private static AdapterKind ClassifyKind(IpHelper.RawAdapter raw)
    {
        if (raw.IfType == IF_TYPE_SOFTWARE_LOOPBACK)
        {
            return AdapterKind.Loopback;
        }
        if (raw.IfType is IF_TYPE_IEEE80211)
        {
            return AdapterKind.WiFi;
        }
        if (raw.IfType is IF_TYPE_WWANPP or IF_TYPE_WWANPP2)
        {
            return AdapterKind.Cellular;
        }
        if (raw.IfType is IF_TYPE_TUNNEL or IF_TYPE_PPP)
        {
            return AdapterKind.Vpn;
        }

        // ifType alone can't separate a real NIC from a virtual switch or a
        // TAP-style VPN adapter — WireGuard, OpenVPN, Hyper-V and VMware all
        // present as IF_TYPE_ETHERNET_CSMACD. The description is the only
        // signal Windows gives us, so we match on it. Ordering matters:
        // VPN before virtual, because some VPN adapters mention "Virtual".
        var description = raw.Description;

        if (ContainsAny(description, "wireguard", "openvpn", "tap-windows", "tap-nordvpn",
                "wintun", "vpn", "tunnel", "zerotier", "tailscale"))
        {
            return AdapterKind.Vpn;
        }

        if (ContainsAny(description, "hyper-v", "vmware", "virtualbox", "vethernet",
                "loopback adapter", "wsl", "docker", "npcap", "virtual"))
        {
            return AdapterKind.Virtual;
        }

        return raw.IfType == IF_TYPE_ETHERNET_CSMACD ? AdapterKind.Ethernet : AdapterKind.Unknown;
    }

    private static AdapterState ClassifyState(
        IpHelper.RawAdapter raw, IPAddress? ipv4, IPAddress? ipv6, AdapterKind kind)
    {
        if (raw.OperStatus != IfOperStatusUp)
        {
            return AdapterState.Disconnected;
        }

        if (ipv4 is null && ipv6 is null)
        {
            return AdapterState.Disconnected;
        }

        // An APIPA address means DHCP never completed. The adapter is "up" but useless.
        if (ipv4 is not null && ipv4.GetAddressBytes() is [169, 254, ..])
        {
            return AdapterState.Disconnected;
        }

        // Loopback is up and addressed by definition; it just isn't a path anywhere.
        if (kind == AdapterKind.Loopback)
        {
            return AdapterState.NoInternet;
        }

        // No default gateway means no route off-link. This is what separates a real
        // uplink from something like the Hyper-V Default Switch, which is permanently
        // "Up" at 10 Gbps and would otherwise look like the best adapter on the box.
        return raw.Gateways.Count > 0 ? AdapterState.Connected : AdapterState.NoInternet;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
        => needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));
}
