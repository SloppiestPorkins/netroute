using NetRoute.Core.Adapters;
using NetRoute.Tests.Service;

namespace NetRoute.Tests.Adapters;

public sealed class RouteTiesTests
{
    private static readonly NetworkAdapter Eth = NetRouteEngineTests.Ethernet();
    private static readonly NetworkAdapter Wifi = NetRouteEngineTests.Wifi();

    private static DefaultRoute Route(NetworkAdapter a, uint metric) => new(a.Luid, a.InterfaceIndex, metric);

    [Fact]
    public void EqualTotalCostIsATie()
    {
        var tie = RouteTies.Find([Eth, Wifi], [Route(Eth, 0), Route(Wifi, 0)]);
        Assert.NotNull(tie);
        Assert.Equal("Ethernet and Wi-Fi", tie!.Names);
        Assert.Equal(10, tie.Cost);
    }

    [Fact]
    public void RouteMetricCountsTowardTheCost()
        => Assert.Null(RouteTies.Find([Eth, Wifi], [Route(Eth, 0), Route(Wifi, 5)]));

    [Fact]
    public void AnAdaptersBestRouteIsWhatCounts()
        => Assert.NotNull(RouteTies.Find([Eth, Wifi], [Route(Eth, 50), Route(Eth, 0), Route(Wifi, 0)]));

    [Fact]
    public void OfflineAdaptersAndAdaptersWithoutADefaultRouteDontTie()
    {
        Assert.Null(RouteTies.Find([Eth, NetRouteEngineTests.Wifi(AdapterState.Disconnected)], [Route(Eth, 0), Route(Wifi, 0)]));
        Assert.Null(RouteTies.Find([Eth, Wifi], [Route(Eth, 0)]));
    }
}
