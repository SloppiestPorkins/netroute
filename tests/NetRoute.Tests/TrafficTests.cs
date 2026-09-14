using System.Net;
using System.Net.Sockets;
using NetRoute.Core.Adapters;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Windows.Traffic;
using Xunit;

namespace NetRoute.Tests;

public class TrafficTests
{
    private const ulong EthernetLuid = 1, WifiLuid = 2;
    private static readonly DateTimeOffset Applied = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private const string Halo = @"G:\SteamLibrary\steamapps\common\Halo Infinite\HaloInfinite.exe";

    // ---- connection table (live, needs no privileges) ----

    [Fact]
    public void SeesALiveTcpConnectionOwnedByThisProcess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        using var server = listener.AcceptTcpClient();

        var rows = ConnectionTable.Snapshot();

        Assert.Contains(rows, r => r.Protocol == TransportProtocol.Tcp && r.ProcessId == Environment.ProcessId
                                   && r.Remote?.Port == port && r.TcpState == "Established");
    }

    [Fact]
    public void SeesABoundUdpSocketOwnedByThisProcess()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        Assert.Contains(ConnectionTable.Snapshot(),
            r => r.Protocol == TransportProtocol.Udp && r.Local.Port == port && r.ProcessId == Environment.ProcessId);
    }

    /// <summary>Pins the MIB_TCPROW_OWNER_PID offsets: a shifted field would misattribute traffic.</summary>
    [Fact]
    public void ParsesTcpRowsAtTheHeaderOffsets()
    {
        var table = new byte[4 + 24];
        BitConverter.GetBytes(1).CopyTo(table, 0);
        BitConverter.GetBytes(5u).CopyTo(table, 4);                // ESTABLISHED
        new byte[] { 192, 168, 0, 51 }.CopyTo(table, 8);           // local address
        new byte[] { 0x01, 0xBB }.CopyTo(table, 12);               // port 443, network order
        new byte[] { 1, 1, 1, 1 }.CopyTo(table, 16);               // remote address
        new byte[] { 0xC7, 0x38 }.CopyTo(table, 20);               // port 51000
        BitConverter.GetBytes(1234).CopyTo(table, 24);             // pid

        var rows = new List<SocketEntry>();
        ConnectionTable.ParseTcp4(table, rows);

        var row = Assert.Single(rows);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.0.51"), 443), row.Local);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 51000), row.Remote);
        Assert.Equal(1234, row.ProcessId);
    }

    // ---- verdicts ----

    [Fact]
    public void TrafficOnTheExpectedNetworkIsVerified()
    {
        var v = TrafficVerdicts.For(Pinned(), [Conn(Halo, EthernetLuid, "Ethernet", Applied.AddMinutes(1))], Applied);
        Assert.Equal(VerificationState.Verified, v.State);
        Assert.Contains("Ethernet", v.Summary);
    }

    [Fact]
    public void NewTrafficOnAnotherNetworkIsALeak()
    {
        var v = TrafficVerdicts.For(Pinned(),
            [Conn(Halo, EthernetLuid, "Ethernet", Applied.AddMinutes(1)), Conn(Halo, WifiLuid, "Wi-Fi 2", Applied.AddMinutes(2))], Applied);
        Assert.Equal(VerificationState.Leak, v.State);
        Assert.Equal("Wi-Fi 2", Assert.Single(v.Leaks).ObservedAdapter);
    }

    /// <summary>§19: a connection opened before the rule is expected to stay put. It must not be called a leak.</summary>
    [Fact]
    public void AConnectionFromBeforeTheRuleIsNotALeak()
    {
        var v = TrafficVerdicts.For(Pinned(), [Conn(Halo, WifiLuid, "Wi-Fi 2", Applied.AddMinutes(-5))], Applied);
        Assert.NotEqual(VerificationState.Leak, v.State);
        Assert.Equal(1, v.PreexistingConnections);
        Assert.Contains("restart", v.Summary);
    }

    /// <summary>Sockets open when the observer started have unknown age, so they can't prove a leak either.</summary>
    [Fact]
    public void UnknownAgeIsTreatedAsPreexisting()
    {
        var v = TrafficVerdicts.For(Pinned(), [Conn(Halo, WifiLuid, "Wi-Fi 2", DateTimeOffset.MinValue)], Applied);
        Assert.Equal(VerificationState.Configured, v.State);
        Assert.Empty(v.Leaks);
    }

    [Fact]
    public void LoopbackAndOtherAppsAreIgnored()
    {
        var v = TrafficVerdicts.For(Pinned(),
        [
            Conn(Halo, null, null, Applied.AddMinutes(1), loopback: true),
            Conn(@"C:\Program Files\Other\other.exe", WifiLuid, "Wi-Fi 2", Applied.AddMinutes(1))
        ], Applied);
        Assert.Equal(VerificationState.Configured, v.State);
        Assert.Contains("No network activity", v.Summary);
    }

    /// <summary>Other program files in the game's own folder count as the game (DayZ_BE starts DayZ_x64).</summary>
    [Fact]
    public void AnotherProgramInTheGameFolderCountsAsTheGame()
    {
        var helper = @"G:\SteamLibrary\steamapps\common\Halo Infinite\bin\helper.exe";
        Assert.True(TrafficVerdicts.Covers(AppIdentity.ForExecutable(Halo), Conn(helper, EthernetLuid, "Ethernet", Applied)));
    }

    // ---- helpers ----

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

    private static ObservedConnection Conn(string exe, ulong? luid, string? adapter, DateTimeOffset first, bool loopback = false) => new(
        TransportProtocol.Tcp,
        new IPEndPoint(loopback ? IPAddress.Loopback : IPAddress.Parse("192.168.0.51"), 50000),
        new IPEndPoint(IPAddress.Parse("203.0.113.5"), 443),
        4242, Path.GetFileNameWithoutExtension(exe), exe, null, luid, adapter, null, loopback, "Established", first);
}
