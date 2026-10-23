using System.Net;
using NetRoute.Core.Traffic;
using Xunit;

namespace NetRoute.Tests.Traffic;

public class RateSmootherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AStepIsApproachedGraduallyNotInOneJump()
    {
        var s = new RateSmoother();
        s.Update("a", 0, 0, T0);
        var (first, _) = s.Update("a", 1_000_000, 0, T0.AddSeconds(2));
        var (second, _) = s.Update("a", 1_000_000, 0, T0.AddSeconds(4));
        var (third, _) = s.Update("a", 1_000_000, 0, T0.AddSeconds(6));

        Assert.InRange(first, 300_000, 700_000);
        Assert.True(second > first && third > second);
        Assert.True(third < 1_000_000);
    }

    [Fact]
    public void ABurstyReadingDoesNotSwingTheDisplayedFigureAsFarAsTheRawOne()
    {
        var s = new RateSmoother();
        var shown = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            // Raw samples alternate between 2 MB/s and nothing, like a game sending in bursts.
            shown.Add(s.Update("a", i % 2 == 0 ? 2_000_000 : 0, 0, T0.AddSeconds(i * 2)).Down);
        }
        var tail = shown.Skip(10).ToList();
        Assert.True(tail.Max() - tail.Min() < 1_200_000, $"swing {tail.Max() - tail.Min():N0}");
    }

    [Fact]
    public void ResultDependsOnElapsedTimeNotOnHowManyTimesItWasAsked()
    {
        var once = new RateSmoother();
        once.Update("a", 0, 0, T0);
        var a = once.Update("a", 1_000_000, 0, T0.AddSeconds(2)).Down;

        var twice = new RateSmoother();
        twice.Update("a", 0, 0, T0);
        twice.Update("a", 1_000_000, 0, T0.AddSeconds(1));
        var b = twice.Update("a", 1_000_000, 0, T0.AddSeconds(2)).Down;

        Assert.InRange(Math.Abs(a - b), 0, 1);
    }

    [Fact]
    public void ADroppedRateFadesAndThenReadsZeroRatherThanLingeringAtADecimal()
    {
        var s = new RateSmoother();
        s.Update("a", 1_000_000, 500_000, T0);
        var mid = s.Update("a", 0, 0, T0.AddSeconds(2));
        Assert.True(mid.Down > 0);

        var later = s.Update("a", 0, 0, T0.AddSeconds(60));
        Assert.Equal((0d, 0d), later);
    }

    [Fact]
    public void KeepForgetsAppsThatAreGone()
    {
        var s = new RateSmoother();
        s.Update("a", 1_000_000, 0, T0);
        s.Keep([]);
        // A forgotten key starts from the raw value again instead of blending with a dead one.
        Assert.Equal(5_000_000, s.Update("a", 5_000_000, 0, T0.AddSeconds(2)).Down);
    }
}

public class ValueHoldTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AMissedSampleIsBridgedButOnlyBriefly()
    {
        var hold = new ValueHold<int>(TimeSpan.FromSeconds(10));
        Assert.Equal(25, hold.Get("a", 25, T0));
        Assert.Equal(25, hold.Get("a", null, T0.AddSeconds(8)));
        Assert.Null(hold.Get("a", null, T0.AddSeconds(11)));
    }

    [Fact]
    public void AStaleFigureIsNeverRevivedAfterItHasExpired()
    {
        var hold = new ValueHold<int>(TimeSpan.FromSeconds(10));
        hold.Get("a", 25, T0);
        hold.Get("a", null, T0.AddSeconds(11));
        Assert.Null(hold.Get("a", null, T0.AddSeconds(12)));
    }

    [Fact]
    public void AFreshValueReplacesTheHeldOne()
    {
        var hold = new ValueHold<int>(TimeSpan.FromSeconds(10));
        hold.Get("a", 25, T0);
        Assert.Equal(40, hold.Get("a", 40, T0.AddSeconds(3)));
        Assert.Equal(40, hold.Get("a", null, T0.AddSeconds(5)));
    }
}

public class StickyOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RowsDoNotSwapWhenTheirSpeedsCrossBetweenResorts()
    {
        var order = new StickyOrder();
        Assert.Equal(["a", "b"], order.Order([("a", 100), ("b", 50)], T0));

        // b overtakes a two seconds later: the order holds.
        Assert.Equal(["a", "b"], order.Order([("a", 50), ("b", 100)], T0.AddSeconds(2)));
        Assert.Equal(["a", "b"], order.Order([("a", 10), ("b", 900)], T0.AddSeconds(8)));
    }

    [Fact]
    public void TheOrderCatchesUpAfterTheResortInterval()
    {
        var order = new StickyOrder();
        order.Order([("a", 100), ("b", 50)], T0);
        Assert.Equal(["b", "a"], order.Order([("a", 50), ("b", 100)], T0 + StickyOrder.Resort));
    }

    [Fact]
    public void ANewRowWaitsAtTheEndAndAGoneRowLeavesWithoutDisturbingTheRest()
    {
        var order = new StickyOrder();
        order.Order([("a", 100), ("b", 50), ("c", 10)], T0);

        Assert.Equal(["a", "b", "c", "d"], order.Order([("a", 100), ("b", 50), ("c", 10), ("d", 9999)], T0.AddSeconds(2)));
        Assert.Equal(["a", "c", "d"], order.Order([("a", 100), ("c", 10), ("d", 9999)], T0.AddSeconds(4)));
    }
}

public class DebounceTests
{
    [Fact]
    public void ASingleOddReadingChangesNothing()
    {
        var d = new Debounce(2);
        Assert.False(d.Update("a", false));
        Assert.False(d.Update("a", true));
        Assert.False(d.Update("a", false));
    }

    [Fact]
    public void AStateThatHoldsIsBelievedInBothDirections()
    {
        var d = new Debounce(2);
        d.Update("a", true);
        Assert.True(d.Update("a", true));
        Assert.True(d.Update("a", false));   // one good reading doesn't clear a real problem
        Assert.False(d.Update("a", false));
    }
}

public class AddressRefreshTests
{
    [Fact]
    public void QuietNetworksAreNotReEnumeratedEverySecond()
    {
        Assert.False(AddressRefresh.ShouldRefresh(TimeSpan.FromSeconds(1), false, false));
        Assert.False(AddressRefresh.ShouldRefresh(TimeSpan.FromSeconds(10), false, false));
    }

    [Fact]
    public void ItRefreshesWhenTheMapIsOldWhenWindowsSaysSoOrWhenTrafficIsUnexplained()
    {
        Assert.True(AddressRefresh.ShouldRefresh(AddressRefresh.MaxAge, false, false));
        Assert.True(AddressRefresh.ShouldRefresh(TimeSpan.FromSeconds(2), true, false));
        Assert.True(AddressRefresh.ShouldRefresh(TimeSpan.FromSeconds(4), false, true));
    }

    [Fact]
    public void BurstsOfNoticesAndPersistentUnexplainedTrafficAreRateLimited()
    {
        Assert.False(AddressRefresh.ShouldRefresh(TimeSpan.FromMilliseconds(300), true, false));
        Assert.False(AddressRefresh.ShouldRefresh(TimeSpan.FromSeconds(1.5), false, true));
    }
}

public class LocalNetworkFastPathTests
{
    [Theory]
    [InlineData("::ffff:192.168.0.1", true)]
    [InlineData("::ffff:162.254.196.26", false)]
    [InlineData("255.255.255.255", true)]
    [InlineData("224.0.0.251", true)]
    [InlineData("2001:db8::1", false)]
    [InlineData("fdff::1", true)]
    public void MatchesLikeBeforeWithoutAllocatingPerCall(string address, bool local)
        => Assert.Equal(local, LocalNetwork.Contains(IPAddress.Parse(address)));

    [Fact]
    public void CheckingAPacketAddressDoesNotAllocate()
    {
        var address = IPAddress.Parse("162.254.196.26");
        LocalNetwork.Contains(address);   // warm up
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            LocalNetwork.Contains(address);
        }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 10_000, "Contains() allocated per call");
    }
}
