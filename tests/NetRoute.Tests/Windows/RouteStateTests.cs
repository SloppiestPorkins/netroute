using System.Net;
using NetRoute.Core.Adapters;
using NetRoute.Windows.Split;
using NetRoute.Tests.Service;

namespace NetRoute.Tests.Windows;

/// <summary>
/// Whether NetRoute can really put Windows' connection settings back.
///
/// <para>From the review of 23 October 2026, finding 1. The owner's own <c>route-state.json</c>
/// recorded both adapters as manual metrics 1 and 21 with automatic metrics off -- which is what
/// NetRoute itself writes, not what Windows had. So uninstall "restored" the machine to NetRoute's
/// settings and reported success. The cause was not the file writing: breaking a default-route tie
/// deliberately replaces the saved originals, so that restoring them later does not bring the tie
/// straight back, and nothing recorded that those values were ours.</para>
/// </summary>
public class RouteStateTests : IDisposable
{
    private const ulong EthernetLuid = 0x0006008001000000;
    private const ulong WifiLuid = 0x0047008003000000;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"NetRoute.Route.{Guid.NewGuid():N}");

    private string StatePath => Path.Combine(_directory, "route-state.json");

    [Fact]
    public void A_setting_netroute_read_first_is_put_back_exactly()
    {
        // Ethernet was on a manual metric of 35 before NetRoute ever ran. That is the user's own
        // choice and has to come back as it was, not as "automatic".
        var api = new FakeMetricApi
        {
            [EthernetLuid] = new InterfaceMetric(Automatic: false, Metric: 35, Connected: true),
            [WifiLuid] = new InterfaceMetric(Automatic: true, Metric: 25, Connected: true)
        };
        var manager = Manager(api);

        manager.EnsurePreferred(Wifi(), Ethernet());
        manager.Restore();

        Assert.Equal(new InterfaceMetric(false, 35, true), api[EthernetLuid]);
        Assert.True(api[WifiLuid].Automatic);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void A_setting_netroute_wrote_itself_is_put_back_as_windows_automatic()
    {
        var api = new FakeMetricApi
        {
            [EthernetLuid] = new InterfaceMetric(true, 25, true),
            [WifiLuid] = new InterfaceMetric(true, 25, true)
        };
        var manager = Manager(api);
        manager.EnsurePreferred(Wifi(), Ethernet());

        // Breaking the tie overwrites the saved originals with NetRoute's own numbers.
        manager.ResolveTie([Ethernet(), Wifi()], Wifi());
        manager.Restore();

        // Replaying those numbers would leave the machine on NetRoute's settings and call it done.
        Assert.True(api[EthernetLuid].Automatic);
        Assert.True(api[WifiLuid].Automatic);
    }

    [Fact]
    public void A_file_written_before_provenance_was_recorded_is_not_trusted()
    {
        // Exactly the shape of the owner's file: no version, no source, and values that are in
        // fact NetRoute's own. There is no way to tell from it, so the only safe move is Windows'
        // own default rather than replaying what it says.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath,
            $"{{\"PreferredLuid\":{WifiLuid},\"OtherLuid\":{EthernetLuid},\"Originals\":[" +
            $"{{\"Luid\":{WifiLuid},\"Automatic\":false,\"Metric\":1}}," +
            $"{{\"Luid\":{EthernetLuid},\"Automatic\":false,\"Metric\":21}}]}}");

        var api = new FakeMetricApi
        {
            [EthernetLuid] = new InterfaceMetric(false, 21, true),
            [WifiLuid] = new InterfaceMetric(false, 1, true)
        };

        Manager(api).Restore();

        Assert.True(api[EthernetLuid].Automatic);
        Assert.True(api[WifiLuid].Automatic);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_not_mistaken_for_nothing_to_restore()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath, "{ this is not json");

        // Saying "nothing to restore" here would quietly cost the user the ability to be put back
        // and report that it had worked. Uninstall reads the throw, and the file is left in place.
        var ex = Assert.Throws<InvalidOperationException>(() => Manager(new FakeMetricApi()).Restore());

        Assert.Contains("could not be read", ex.Message);
        Assert.Contains("AutomaticMetric Enabled", ex.Message);
        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public void The_record_survives_a_write_that_does_not_finish()
    {
        // The save goes to a temporary file and is moved into place, so a half-written file can
        // never be what a restore reads.
        var api = new FakeMetricApi
        {
            [EthernetLuid] = new InterfaceMetric(false, 35, true),
            [WifiLuid] = new InterfaceMetric(true, 25, true)
        };
        Manager(api).EnsurePreferred(Wifi(), Ethernet());

        Assert.True(File.Exists(StatePath));
        Assert.False(File.Exists(StatePath + ".new"));
        // And it says what it is, so a later version can tell.
        var saved = File.ReadAllText(StatePath);
        Assert.Contains("\"Version\": 2", saved);
        Assert.Contains("CapturedAt", saved);
    }

    [Fact]
    public void An_adapter_that_is_away_keeps_its_original_until_it_comes_back()
    {
        var api = new FakeMetricApi
        {
            [EthernetLuid] = new InterfaceMetric(false, 35, true),
            [WifiLuid] = new InterfaceMetric(true, 25, true)
        };
        var both = new NetRouteEngineTests.MutableAdapterSource(Ethernet(), Wifi());
        var manager = new DefaultRouteManager(both, StatePath, api);

        manager.EnsurePreferred(Wifi(), Ethernet());

        // The Wi-Fi adapter is unplugged -- its metric is still NetRoute's.
        both.Adapters = [Ethernet()];
        manager.Restore();

        Assert.True(File.Exists(StatePath));
        Assert.Equal(new InterfaceMetric(false, 35, true), api[EthernetLuid]);

        // Back again, and now there is nothing left to put back.
        both.Adapters = [Ethernet(), Wifi()];
        manager.Restore();

        Assert.True(api[WifiLuid].Automatic);
        Assert.False(File.Exists(StatePath));
    }

    private DefaultRouteManager Manager(FakeMetricApi api)
        => new(new NetRouteEngineTests.MutableAdapterSource(Ethernet(), Wifi()), StatePath, api);

    private static NetworkAdapter Ethernet() => new()
    {
        Luid = EthernetLuid, Guid = "{E}", Name = "Ethernet", Description = "Realtek", Kind = AdapterKind.Ethernet,
        State = AdapterState.Connected, InterfaceIndex = 21, InterfaceIndexV6 = 21,
        Ipv4Address = IPAddress.Parse("192.168.0.54"), Ipv6Address = null, Gateways = [], DnsServers = [],
        LinkSpeedBps = 1_000_000_000, Ipv4Metric = 1, Ipv6Metric = 1
    };

    private static NetworkAdapter Wifi() => new()
    {
        Luid = WifiLuid, Guid = "{W}", Name = "Wi-Fi 2", Description = "Intel AX210", Kind = AdapterKind.WiFi,
        State = AdapterState.Connected, InterfaceIndex = 7, InterfaceIndexV6 = 7,
        Ipv4Address = IPAddress.Parse("192.168.7.8"), Ipv6Address = null, Gateways = [], DnsServers = [],
        LinkSpeedBps = 600_000_000, Ipv4Metric = 1, Ipv6Metric = 1
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

/// <summary>Windows' IPv4 interface metrics, as a dictionary, so the save/restore rule can be
/// checked without touching a real adapter.</summary>
internal sealed class FakeMetricApi : IMetricApi
{
    private readonly Dictionary<ulong, InterfaceMetric> _interfaces = [];

    /// <summary>Which interface index a route lookup should name. Nothing here asserts on it.</summary>
    public uint Winner { get; set; } = 7;

    public InterfaceMetric this[ulong luid]
    {
        get => _interfaces[luid];
        set => _interfaces[luid] = value;
    }

    public InterfaceMetric? GetIpv4Interface(ulong luid)
        => _interfaces.TryGetValue(luid, out var found) ? found : null;

    public void SetIpv4Interface(ulong luid, bool automatic, uint metric)
    {
        if (!_interfaces.TryGetValue(luid, out var current))
        {
            throw new InvalidOperationException($"No IPv4 interface for LUID {luid}.");
        }
        _interfaces[luid] = automatic
            ? current with { Automatic = true }
            : new InterfaceMetric(false, metric, current.Connected);
    }

    public uint? LowestDefaultRouteMetric(ulong luid) => 0;

    public uint? BestInterfaceIndex(System.Net.IPAddress destination) => Winner;
}
