using System.Net;
using NetRoute.Core.Traffic;
using NetRoute.Service;
using NetRoute.Windows.Traffic;
using Xunit;

namespace NetRoute.Tests.Traffic;

public class PeerLatencyProbeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakePinger : IPinger
    {
        public readonly List<(IPAddress Source, IPAddress Peer, ulong Luid)> Calls = [];
        public Func<IPAddress, PingOutcome> Answer = _ => new PingOutcome(PingStatus.Reply, 21);

        public PingOutcome Ping(IPAddress source, IPAddress destination, ulong adapterLuid, TimeSpan timeout)
        {
            Calls.Add((source, destination, adapterLuid));
            return Answer(destination);
        }
    }

    private sealed class Harness
    {
        public readonly FakePinger Pinger = new();
        public DateTimeOffset Now = Start;
        public readonly List<Action> Deferred = [];
        public readonly PeerLatencyProbe Probe;

        public Harness(bool inline = true)
        {
            Probe = new PeerLatencyProbe(Pinger, () => Now, inline ? work => { work(); return Task.CompletedTask; } : work => { Deferred.Add(work); return Task.CompletedTask; });
        }

        public IReadOnlyDictionary<(int, ulong), PeerLatency> Ask(params PeerTarget[] targets)
        {
            Now += PeerLatencyProbe.MinInterval;
            Probe.Measure(targets);          // starts a pass; its answers land in the cache
            return Probe.Measure(targets);   // and the next look sees them
        }
    }

    private static PeerTarget Target(int pid, ulong luid, string local, params string[] peers)
        => new(pid, luid, IPAddress.Parse(local), peers.Select(IPAddress.Parse).ToList());

    private const ulong Ethernet = 100;
    private const ulong Wifi = 200;

    [Fact]
    public void AnAnsweringPeerGivesAPeerProbeFigure()
    {
        var h = new Harness();
        var result = h.Ask(Target(1, Ethernet, "192.168.0.54", "20.42.73.15"));

        var latency = Assert.Single(result).Value;
        Assert.Equal(LatencyKind.PeerProbe, latency.Kind);
        Assert.Equal(21, latency.Rtt!.Value.MedianMs);
    }

    [Fact]
    public void EachPingLeavesFromTheProgramsOwnAddressAndMustRouteOutOfItsAdapter()
    {
        var h = new Harness();
        h.Ask(Target(1, Ethernet, "192.168.0.54", "20.42.73.15"), Target(2, Wifi, "192.168.7.8", "35.1.1.1"));

        Assert.Contains((IPAddress.Parse("192.168.0.54"), IPAddress.Parse("20.42.73.15"), Ethernet), h.Pinger.Calls);
        Assert.Contains((IPAddress.Parse("192.168.7.8"), IPAddress.Parse("35.1.1.1"), Wifi), h.Pinger.Calls);
        Assert.Equal(2, h.Pinger.Calls.Count);
    }

    [Fact]
    public void TheSamePeerOnTwoNetworksIsMeasuredOnEachNotSharedBetweenThem()
    {
        var h = new Harness();
        var result = h.Ask(Target(1, Ethernet, "192.168.0.54", "20.42.73.15"), Target(2, Wifi, "192.168.7.8", "20.42.73.15"));

        Assert.Equal(2, h.Pinger.Calls.Count);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void APeerThatDoesNotAnswerIsNotMeasurableNeverAZeroOrAFigure()
    {
        var h = new Harness { Pinger = { Answer = _ => new PingOutcome(PingStatus.NoReply) } };
        var result = h.Ask(Target(1, Ethernet, "192.168.0.54", "20.42.73.15"));

        var latency = Assert.Single(result).Value;
        Assert.Equal(LatencyKind.NotMeasurable, latency.Kind);
        Assert.Null(latency.Rtt);
    }

    [Fact]
    public void ARefusalToSendOverTheWrongAdapterIsNotMeasurableAndIsNotRetriedSoon()
    {
        var h = new Harness { Pinger = { Answer = _ => new PingOutcome(PingStatus.WrongInterface) } };
        var target = Target(1, Ethernet, "192.168.0.54", "20.42.73.15");
        Assert.Equal(LatencyKind.NotMeasurable, h.Ask(target).Single().Value.Kind);

        for (var i = 0; i < 10; i++)
        {
            h.Ask(target);   // ten more passes, a minute or so in all
        }
        Assert.Single(h.Pinger.Calls);
    }

    [Fact]
    public void OneAnsweringPeerIsEnoughEvenIfOthersAreSilent()
    {
        var silent = IPAddress.Parse("20.42.73.16");
        var h = new Harness();
        h.Pinger.Answer = peer => peer.Equals(silent) ? new PingOutcome(PingStatus.NoReply) : new PingOutcome(PingStatus.Reply, 30);
        var result = h.Ask(Target(1, Ethernet, "192.168.0.54", "20.42.73.15", "20.42.73.16"));

        var latency = Assert.Single(result).Value;
        Assert.Equal(LatencyKind.PeerProbe, latency.Kind);
        Assert.Equal(30, latency.Rtt!.Value.MedianMs);
        Assert.Equal(1, latency.Rtt.Value.Connections);
    }

    [Fact]
    public void ResultsAreCachedSoAskingOftenDoesNotPingOften()
    {
        var h = new Harness();
        var target = Target(1, Ethernet, "192.168.0.54", "20.42.73.15");
        h.Ask(target);
        for (var i = 0; i < 4; i++)
        {
            h.Ask(target);   // 4 s apart: 16 s, inside RefreshAfter
        }
        Assert.Single(h.Pinger.Calls);

        h.Now += PeerLatencyProbe.RefreshAfter;
        h.Ask(target);
        Assert.Equal(2, h.Pinger.Calls.Count);
    }

    [Fact]
    public void NoMoreThanThreePeersPerProgramAndFourPingsPerPassAreSent()
    {
        var h = new Harness();
        var targets = Enumerable.Range(1, 10).Select(i => Target(i, Ethernet, "192.168.0.54", $"20.42.{i}.1", $"20.42.{i}.2", $"20.42.{i}.3", $"20.42.{i}.4", $"20.42.{i}.5")).ToArray();

        h.Now += PeerLatencyProbe.MinInterval;
        h.Probe.Measure(targets);
        Assert.Equal(PeerLatencyProbe.MaxPingsPerPass, h.Pinger.Calls.Count);

        // Drain everything over many passes: no program ever has more than three peers pinged.
        for (var i = 0; i < 30; i++)
        {
            h.Now += PeerLatencyProbe.MinInterval;
            h.Probe.Measure(targets);
        }
        Assert.All(h.Pinger.Calls.GroupBy(c => c.Peer.ToString().Split('.')[2]), g => Assert.True(g.Select(c => c.Peer).Distinct().Count() <= PeerLatencyProbe.MaxPeersPerApp));
        Assert.Equal(30, h.Pinger.Calls.Select(c => c.Peer).Distinct().Count());
    }

    [Fact]
    public void AStaleAnswerWithNothingNewerIsUnknownRatherThanPresentedAsCurrent()
    {
        var h = new Harness(inline: false);   // the new ping is queued, not yet run
        var target = Target(1, Ethernet, "192.168.0.54", "20.42.73.15");
        h.Now += PeerLatencyProbe.MinInterval;
        h.Probe.Measure([target]);
        h.Deferred.ForEach(w => w());
        h.Deferred.Clear();
        Assert.Equal(LatencyKind.PeerProbe, h.Probe.Measure([target])[(1, Ethernet)].Kind);

        h.Now += PeerLatencyProbe.FreshFor + TimeSpan.FromSeconds(1);
        var result = h.Probe.Measure([target]);
        Assert.Empty(result);   // not the 21 ms from over 45 s ago, and not "not measurable" either
    }

    [Fact]
    public void MeasureNeverWaitsForAPing()
    {
        var h = new Harness(inline: false);
        h.Now += PeerLatencyProbe.MinInterval;
        var result = h.Probe.Measure([Target(1, Ethernet, "192.168.0.54", "20.42.73.15")]);

        Assert.Empty(result);
        Assert.Empty(h.Pinger.Calls);   // the ping is queued for the background, not run on the caller's thread
        Assert.Single(h.Deferred);
    }

    [Fact]
    public void OnlyOnePassRunsAtATime()
    {
        var h = new Harness(inline: false);
        var target = Target(1, Ethernet, "192.168.0.54", "20.42.73.15");
        h.Now += PeerLatencyProbe.MinInterval;
        h.Probe.Measure([target]);
        h.Now += PeerLatencyProbe.MinInterval;
        h.Probe.Measure([Target(2, Ethernet, "192.168.0.54", "20.42.73.99")]);

        Assert.Single(h.Deferred);
    }

    [Fact]
    public void ThrowingPingerIsTreatedAsNoAnswer()
    {
        var h = new Harness();
        h.Pinger.Answer = _ => throw new InvalidOperationException("boom");
        Assert.Equal(LatencyKind.NotMeasurable, h.Ask(Target(1, Ethernet, "192.168.0.54", "20.42.73.15")).Single().Value.Kind);
    }

    [Fact]
    public void ThePeerCacheIsBounded()
    {
        var h = new Harness();
        for (var i = 0; i < PeerLatencyProbe.MaxCached + 40; i++)
        {
            h.Now += PeerLatencyProbe.MinInterval;
            h.Probe.Measure([Target(1, Ethernet, "192.168.0.54", $"20.{i / 250}.{i % 250}.1")]);
        }
        Assert.True(h.Probe.CachedCount <= PeerLatencyProbe.MaxCached);
    }
}

public class AppRateMonitorLatencyTests
{
    private static ProcessRate Rate(int pid, ulong? luid, string? local, double bytes, params string[] peers)
        => new(pid, "game", @"C:\game.exe", null, luid, luid is null ? null : "Ethernet", bytes, 0)
        {
            LocalAddress = local is null ? null : IPAddress.Parse(local),
            UdpPeers = peers.Select(IPAddress.Parse).ToList()
        };

    [Fact]
    public void OnlyUdpProgramsWithoutATcpFigureAreProbedFromTheirOwnAddressAndAdapter()
    {
        var rates = new[]
        {
            Rate(1, 100, "192.168.0.54", 40_000, "20.42.73.15"),        // a game: UDP peers, no TCP figure
            Rate(2, 200, "192.168.7.8", 9_000_000, "35.1.1.1"),         // has a TCP figure already
            Rate(3, 100, "192.168.0.54", 1_000),                         // TCP-only: no peers to talk about
            Rate(4, null, "10.9.9.9", 5_000, "35.2.2.2"),                // address no adapter owns: can't promise the line
            Rate(5, 200, null, 5_000, "35.3.3.3")                        // unknown local address: can't bind
        };

        var targets = AppRateMonitor.PeerTargets(rates, new HashSet<(int, ulong)> { (2, 200UL) });

        var only = Assert.Single(targets);
        Assert.Equal(1, only.ProcessId);
        Assert.Equal(100UL, only.AdapterLuid);
        Assert.Equal(IPAddress.Parse("192.168.0.54"), only.Local);
        Assert.Equal([IPAddress.Parse("20.42.73.15")], only.Peers);
    }

    [Fact]
    public void AProgramOnTwoNetworksGetsATargetForEachSoEachLineIsMeasuredOnItsOwn()
    {
        var rates = new[]
        {
            Rate(1, 100, "192.168.0.54", 40_000, "20.42.73.15"),
            Rate(1, 200, "192.168.7.8", 20_000, "20.42.73.15")
        };

        var targets = AppRateMonitor.PeerTargets(rates, new HashSet<(int, ulong)>());

        Assert.Equal(2, targets.Count);
        Assert.Equal(new[] { 100UL, 200UL }, targets.Select(t => t.AdapterLuid).Order());
        Assert.Contains(targets, t => t.AdapterLuid == 200 && t.Local.Equals(IPAddress.Parse("192.168.7.8")));
    }

    [Fact]
    public void TheBusiestProgramsAreFirstAndPeersAreCapped()
    {
        var rates = new[]
        {
            Rate(1, 100, "192.168.0.54", 1_000, "1.1.1.1"),
            Rate(2, 100, "192.168.0.54", 90_000, "2.2.2.1", "2.2.2.2", "2.2.2.3", "2.2.2.4")
        };

        var targets = AppRateMonitor.PeerTargets(rates, new HashSet<(int, ulong)>());

        Assert.Equal([2, 1], targets.Select(t => t.ProcessId));
        Assert.Equal(PeerLatencyProbe.MaxPeersPerApp, targets[0].Peers.Count);
    }

    [Fact]
    public void ATcpFigureWinsOverAPeerPing()
    {
        var rate = Rate(1, 100, "192.168.0.54", 1_000, "1.1.1.1");
        var dto = AppRateMonitor.ToDto(rate, new RttSummary(40, 80, 3), new PeerLatency(LatencyKind.PeerProbe, new RttSummary(12, 12, 1)));

        Assert.Equal(LatencyKind.Tcp, dto.LatencyKind);
        Assert.Equal(40, dto.MedianRttMs);
    }

    [Fact]
    public void APeerFigureIsLabelledAsOne()
    {
        var dto = AppRateMonitor.ToDto(Rate(1, 100, "192.168.0.54", 1_000, "1.1.1.1"), null, new PeerLatency(LatencyKind.PeerProbe, new RttSummary(12, 15, 2)));

        Assert.Equal(LatencyKind.PeerProbe, dto.LatencyKind);
        Assert.Equal(12, dto.MedianRttMs);
        Assert.Equal(15, dto.WorstRttMs);
        Assert.Equal(2, dto.RttConnections);
    }

    [Fact]
    public void NotMeasurableCarriesNoNumberAtAll()
    {
        var dto = AppRateMonitor.ToDto(Rate(1, 100, "192.168.0.54", 1_000, "1.1.1.1"), null, new PeerLatency(LatencyKind.NotMeasurable, new RttSummary(0, 0, 0)));

        Assert.Equal(LatencyKind.NotMeasurable, dto.LatencyKind);
        Assert.Null(dto.MedianRttMs);
        Assert.Null(dto.WorstRttMs);
        Assert.Null(dto.RttConnections);
    }

    [Fact]
    public void NothingMeasuredIsNone()
    {
        var dto = AppRateMonitor.ToDto(Rate(1, 100, "192.168.0.54", 1_000), null, null);
        Assert.Equal(LatencyKind.None, dto.LatencyKind);
        Assert.Null(dto.MedianRttMs);
    }
}

public class IcmpPingerLayoutTests
{
    [Fact]
    public void SockAddrInetPutsTheAddressWhereWindowsExpectsIt()
    {
        var v4 = NativeIcmpPinger.SockAddrInet(IPAddress.Parse("1.2.3.4"));
        Assert.Equal(28, v4.Length);
        Assert.Equal(2, BitConverter.ToUInt16(v4, 0));         // AF_INET
        Assert.Equal([1, 2, 3, 4], v4[4..8]);                  // sin_addr after the port

        var v6 = NativeIcmpPinger.SockAddrInet(IPAddress.Parse("2606:4700:4700::1111"));
        Assert.Equal(23, BitConverter.ToUInt16(v6, 0));        // AF_INET6
        Assert.Equal(IPAddress.Parse("2606:4700:4700::1111").GetAddressBytes(), v6[8..24]);   // sin6_addr after flowinfo
    }

    [Fact]
    public void ARefusedRouteIsReportedAsWrongInterfaceWithoutSendingAnything()
    {
        // Loopback's route is the loopback adapter, never some made-up LUID, so a ping that claims to
        // belong to another adapter is refused before a packet is sent.
        var outcome = new NativeIcmpPinger().Ping(IPAddress.Loopback, IPAddress.Loopback, adapterLuid: 12345, TimeSpan.FromMilliseconds(200));
        Assert.Equal(PingStatus.WrongInterface, outcome.Status);
    }
}

public class MeterPeerTrackingTests
{
    [Fact]
    public void TheBusiestPeersAreReturnedAndTheNextReadingStartsFresh()
    {
        var c = new ProcessNetworkMeter.Counter();
        c.NotePeer(IPAddress.Parse("1.1.1.1"), 100);
        c.NotePeer(IPAddress.Parse("2.2.2.2"), 5_000);
        c.NotePeer(IPAddress.Parse("2.2.2.2"), 5_000);
        c.NotePeer(IPAddress.Parse("3.3.3.3"), 900);
        c.NotePeer(IPAddress.Parse("4.4.4.4"), 800);
        c.NotePeer(IPAddress.Parse("5.5.5.5"), 700);

        var top = c.TakePeers();
        Assert.Equal(ProcessNetworkMeter.PeersPerReading, top.Count);
        Assert.Equal(IPAddress.Parse("2.2.2.2"), top[0]);
        Assert.DoesNotContain(IPAddress.Parse("1.1.1.1"), top);   // the quietest is the one that falls off

        Assert.Empty(c.TakePeers());   // a peer we stopped hearing from doesn't linger into the next reading
    }

    [Fact]
    public void AProgramTalkingToThousandsOfAddressesCannotGrowTheTable()
    {
        var c = new ProcessNetworkMeter.Counter();
        for (var i = 0; i < 5_000; i++)
        {
            c.NotePeer(new IPAddress([20, (byte)(i >> 8), (byte)i, 1]), 100);
        }
        Assert.True(c.TakePeers().Count <= ProcessNetworkMeter.PeersPerReading);
        // Internal bound: only MaxPeersTracked distinct addresses were ever held.
        for (var i = 0; i < 5_000; i++)
        {
            c.NotePeer(new IPAddress([20, (byte)(i >> 8), (byte)i, 1]), 100);
        }
        Assert.Equal(ProcessNetworkMeter.PeersPerReading, c.TakePeers().Count);
    }
}
