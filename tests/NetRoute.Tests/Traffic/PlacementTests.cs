using NetRoute.Core.Traffic;
using Xunit;

namespace NetRoute.Tests.Traffic;

public class PlacementCheckTests
{
    private static AdapterTraffic T(string? adapter, double bytes) => new(adapter, bytes);

    [Fact]
    public void AnAppMovingDataOnItsAssignedNetworkIsOnIt()
    {
        var v = PlacementCheck.Evaluate("Ethernet", [T("Ethernet", 50_000)], null, 4);
        Assert.Equal(PlacementState.OnAssigned, v.State);
        Assert.False(v.IsProblem);
        Assert.True(v.FromTraffic);
    }

    [Fact]
    public void AnAppMovingDataOnTheWrongNetworkIsAMismatchAndSaysWhich()
    {
        var v = PlacementCheck.Evaluate("Ethernet", [T("Wi-Fi 2", 300_000)], "Ethernet", 6);
        Assert.Equal(PlacementState.Mismatch, v.State);
        Assert.Equal("Wi-Fi 2", v.Using);
        Assert.Equal(["Wi-Fi 2"], v.Wrong);
        Assert.True(v.IsProblem);
    }

    [Fact]
    public void BytesOutrankTheConnectionTableWhichCanBeStale()
    {
        // The table says Ethernet; the packets say Wi-Fi 2. The packets are what the ISP bills.
        var v = PlacementCheck.Evaluate("Ethernet", [T("Wi-Fi 2", 80_000)], "Ethernet", 3);
        Assert.Equal(PlacementState.Mismatch, v.State);
    }

    [Fact]
    public void BothNetworksAtOnceIsASplit()
    {
        var v = PlacementCheck.Evaluate("Ethernet", [T("Ethernet", 100_000), T("Wi-Fi 2", 60_000)], null, 5);
        Assert.Equal(PlacementState.Split, v.State);
        Assert.Equal(["Wi-Fi 2"], v.Wrong);
        Assert.True(v.IsProblem);
    }

    [Fact]
    public void AStrayTrickleOnTheOtherNetworkIsNotASplit()
    {
        var v = PlacementCheck.Evaluate("Ethernet", [T("Ethernet", 5_000_000), T("Wi-Fi 2", 900)], null, 5);
        Assert.Equal(PlacementState.OnAssigned, v.State);
    }

    [Fact]
    public void RowsOnTheSameNetworkAreSummedAndNamesCompareCaseInsensitively()
    {
        var v = PlacementCheck.Evaluate("ethernet", [T("Ethernet", 600), T("ETHERNET", 900)], null, 2);
        Assert.Equal(PlacementState.OnAssigned, v.State);
    }

    [Fact]
    public void WithNoTrafficTheConnectionTableSpeaks()
    {
        var wrong = PlacementCheck.Evaluate("Ethernet", [], "Wi-Fi 2", 2);
        Assert.Equal(PlacementState.Mismatch, wrong.State);
        Assert.False(wrong.FromTraffic);

        var right = PlacementCheck.Evaluate("Ethernet", [], "Ethernet", 2);
        Assert.Equal(PlacementState.OnAssigned, right.State);
    }

    [Fact]
    public void QuietTrafficBelowTheThresholdIsTreatedAsNoTraffic()
    {
        var v = PlacementCheck.Evaluate("Ethernet", [T("Wi-Fi 2", 200)], "Ethernet", 2);
        Assert.Equal(PlacementState.OnAssigned, v.State);
    }

    [Fact]
    public void NothingAtAllIsIdleNotAProblem()
    {
        var v = PlacementCheck.Evaluate("Ethernet", [], null, 0);
        Assert.Equal(PlacementState.Idle, v.State);
        Assert.False(v.IsProblem);
    }

    [Fact]
    public void AnAppWithNoAssignedNetworkCannotBeOnTheWrongOne()
    {
        var v = PlacementCheck.Evaluate(null, [T("Wi-Fi 2", 900_000)], null, 3);
        Assert.Equal(PlacementState.Unmanaged, v.State);
        Assert.Equal("Wi-Fi 2", v.Using);
        Assert.False(v.IsProblem);
    }

    [Fact]
    public void TrafficThatCouldNotBeTiedToAnAdapterAccuses_Nobody()
    {
        var v = PlacementCheck.Evaluate("Ethernet", [T(null, 900_000)], "Ethernet", 2);
        Assert.Equal(PlacementState.OnAssigned, v.State);
    }
}
