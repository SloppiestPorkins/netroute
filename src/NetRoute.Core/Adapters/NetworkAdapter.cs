using System.Net;

namespace NetRoute.Core.Adapters;

/// <summary>How the adapter physically (or virtually) connects. Drives the UI icon and grouping.</summary>
public enum AdapterKind
{
    Unknown,
    Ethernet,
    WiFi,
    Cellular,
    Vpn,
    Virtual,
    Loopback
}

public enum AdapterState
{
    /// <summary>Up, has an address, and has a default gateway — usable as a role target.</summary>
    Connected,

    /// <summary>Up but with no default gateway. Usually a host-only/virtual switch. Not internet-capable.</summary>
    NoInternet,

    /// <summary>Present but not operational.</summary>
    Disconnected,

    /// <summary>Configured as a role target but no longer present on the system.</summary>
    Missing
}

/// <summary>
/// A snapshot of one network interface.
///
/// <para><see cref="Luid"/> is the identity. Everything else — friendly name, IP,
/// gateway, speed, state — is resolved fresh on each discovery pass and must never
/// be persisted as the way to find this adapter again. See §7 of the product brief:
/// the user has to be able to rename an adapter or move to a new subnet without
/// their app rules falling apart.</para>
/// </summary>
public sealed record NetworkAdapter
{
    /// <summary>Stable Windows interface LUID. This is what NetRoute persists and what WFP filters key on.</summary>
    public required ulong Luid { get; init; }

    /// <summary>Stable interface GUID, e.g. "{BCCF9527-...}". Persisted alongside the LUID as a fallback.</summary>
    public required string Guid { get; init; }

    /// <summary>User-visible name, e.g. "Ethernet". Display only — may change.</summary>
    public required string Name { get; init; }

    /// <summary>Hardware description, e.g. "Realtek PCIe GbE Family Controller". Display only.</summary>
    public required string Description { get; init; }

    public required AdapterKind Kind { get; init; }
    public required AdapterState State { get; init; }

    /// <summary>IPv4 interface index. Changes across reboots on some systems — diagnostics only, never persisted.</summary>
    public required uint InterfaceIndex { get; init; }
    public required uint InterfaceIndexV6 { get; init; }

    public required IPAddress? Ipv4Address { get; init; }
    public required IPAddress? Ipv6Address { get; init; }
    public required IReadOnlyList<IPAddress> Gateways { get; init; }
    public required IReadOnlyList<IPAddress> DnsServers { get; init; }

    /// <summary>Negotiated downlink speed in bits per second.</summary>
    public required ulong LinkSpeedBps { get; init; }

    public required uint Ipv4Metric { get; init; }
    public required uint Ipv6Metric { get; init; }

    public bool HasIpv4Gateway => Gateways.Any(g => g.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    public bool HasIpv6Gateway => Gateways.Any(g => g.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);

    /// <summary>
    /// True when this adapter can carry IPv4 but not IPv6.
    ///
    /// <para>This matters more than it looks. If a role resolves to an IPv4-only adapter,
    /// any app assigned to that role will happily send IPv6 out of whichever adapter *does*
    /// have a v6 default route — silently bypassing the policy. §23 forbids that, so the
    /// enforcement layer has to explicitly block IPv6 for these roles rather than
    /// leave it to chance.</para>
    /// </summary>
    public bool IsIpv4Only => Ipv4Address is not null && !HasIpv6Gateway;

    /// <summary>Whether this adapter is a sensible thing to offer as a Gaming/Downloads target.</summary>
    public bool IsSelectableAsRole =>
        State is AdapterState.Connected &&
        Kind is not (AdapterKind.Loopback or AdapterKind.Virtual);

    /// <summary>e.g. "1 Gbps", "866 Mbps". Empty when the speed is not meaningful.</summary>
    public string LinkSpeedDisplay => LinkSpeedBps switch
    {
        0 or ulong.MaxValue => string.Empty,
        >= 1_000_000_000 => $"{LinkSpeedBps / 1_000_000_000d:0.##} Gbps",
        >= 1_000_000 => $"{LinkSpeedBps / 1_000_000d:0.##} Mbps",
        _ => $"{LinkSpeedBps / 1_000d:0.##} Kbps"
    };
}
