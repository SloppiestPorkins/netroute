using System.Net;
using NetRoute.Core.Adapters;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Windows.Traffic;

namespace NetRoute.Tests.Traffic;

/// <summary>Edges of the traffic verdict that the main traffic tests didn't pin (review section F).</summary>
public class VerificationBoundaryTests
{
    private const ulong EthernetLuid = 1, WifiLuid = 2;
    private static readonly DateTimeOffset Applied = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private const string Halo = @"C:\Games\Halo\HaloInfinite.exe";

    /// <summary>A socket first seen at exactly the moment the policy was applied was not opened under it: no leak.</summary>
    [Fact]
    public void A_connection_first_seen_exactly_when_the_policy_took_effect_is_preexisting_not_a_leak()
    {
        var v = TrafficVerdicts.For(Pinned(), [Conn(WifiLuid, "Wi-Fi 2", Applied)], Applied);

        Assert.NotEqual(VerificationState.Leak, v.State);
        Assert.Equal(1, v.PreexistingConnections);
    }

    [Fact]
    public void A_connection_one_tick_after_the_policy_on_the_wrong_network_is_a_leak()
    {
        var v = TrafficVerdicts.For(Pinned(), [Conn(WifiLuid, "Wi-Fi 2", Applied.AddTicks(1))], Applied);

        Assert.Equal(VerificationState.Leak, v.State);
    }

    /// <summary>
    /// What "Verified" should require is data moving or an established connection. The Traffic layer
    /// still counts a bound, idle UDP socket (an mDNS or SSDP socket on the adapter's address). Until
    /// TrafficVerdicts changes, the app gates the word on observed bytes instead (see StatusWording).
    /// </summary>
    [Fact]
    public void An_idle_bound_udp_socket_does_not_make_an_app_verified()
    {
        var idleUdp = Conn(EthernetLuid, "Ethernet", Applied.AddMinutes(1)) with
        {
            Protocol = TransportProtocol.Udp, Remote = null, State = null
        };

        var v = TrafficVerdicts.For(Pinned(), [idleUdp], Applied);

        Assert.NotEqual(VerificationState.Verified, v.State);
    }

    private static AppEnforcement Pinned() => new()
    {
        Rule = AppRule.Create(AppIdentity.ForExecutable(Halo, "Halo Infinite"), RoleId.Gaming),
        Action = EnforcementAction.PinToAdapter,
        ResolvedAdapter = new NetworkAdapter
        {
            Luid = EthernetLuid, Guid = "{E}", Name = "Ethernet", Description = "Realtek", Kind = AdapterKind.Ethernet,
            State = AdapterState.Connected, InterfaceIndex = 18, InterfaceIndexV6 = 18,
            Ipv4Address = IPAddress.Parse("192.168.0.51"), Ipv6Address = null, Gateways = [], DnsServers = [],
            LinkSpeedBps = 1_000_000_000, Ipv4Metric = 1, Ipv6Metric = 1
        },
        BlockIpv6 = false,
        Reasons = []
    };

    private static ObservedConnection Conn(ulong luid, string adapter, DateTimeOffset first) => new(
        TransportProtocol.Tcp,
        new IPEndPoint(IPAddress.Parse("192.168.0.51"), 50000),
        new IPEndPoint(IPAddress.Parse("203.0.113.5"), 443),
        4242, "HaloInfinite", Halo, null, luid, adapter, null, false, "Established", first);
}
