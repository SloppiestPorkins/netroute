using System.Net;
using System.Runtime.InteropServices;
using NetRoute.Core.Traffic;
using NetRoute.Windows.Traffic;
using Xunit;

namespace NetRoute.Tests.Traffic;

/// <summary>
/// Layout checks for the TCP ESTATS interop, in the spirit of <see cref="WfpStructLayoutTests"/>.
///
/// <para>Get/SetPerTcpConnectionEStats take raw structs, and a wrong offset does not fail: the
/// v6 row in particular looks like the owner-pid row the connection table uses but has the
/// state first, so a copy-paste would match no connection and every read would come back
/// not-found. A wrong ROD offset would show a retransmit counter as the round-trip time.
/// Expected values are from tcpmib.h and tcpestats.h (Windows SDK 10.0.26100, x64).</para>
/// </summary>
public class TcpEstatsLayoutTests
{
    [Theory]
    [InlineData(typeof(NativeTcpEstats.MIB_TCPROW), 20)]
    [InlineData(typeof(NativeTcpEstats.MIB_TCP6ROW), 52)]
    [InlineData(typeof(NativeTcpEstats.TCP_ESTATS_PATH_RW_v0), 1)]
    [InlineData(typeof(NativeTcpEstats.TCP_ESTATS_PATH_ROD_v0), 160)]   // 40 ULONGs
    public void StructSizeMatchesNativeLayout(Type type, int expectedSize)
        => Assert.Equal(expectedSize, Marshal.SizeOf(type));

    [Theory]
    [InlineData("State", 0)]
    [InlineData("LocalAddr", 4)]
    [InlineData("LocalPort", 8)]
    [InlineData("RemoteAddr", 12)]
    [InlineData("RemotePort", 16)]
    public void V4RowOffsetsMatchNativeLayout(string field, int expected)
        => Assert.Equal(expected, Marshal.OffsetOf<NativeTcpEstats.MIB_TCPROW>(field).ToInt32());

    [Theory]
    [InlineData("State", 0)]
    [InlineData("LocalAddr", 4)]
    [InlineData("LocalScopeId", 20)]
    [InlineData("LocalPort", 24)]
    [InlineData("RemoteAddr", 28)]
    [InlineData("RemoteScopeId", 44)]
    [InlineData("RemotePort", 48)]
    public void V6RowOffsetsMatchNativeLayout(string field, int expected)
        => Assert.Equal(expected, Marshal.OffsetOf<NativeTcpEstats.MIB_TCP6ROW>(field).ToInt32());

    [Theory]
    [InlineData("SampleRtt", 104)]    // field 26 of 40
    [InlineData("SmoothedRtt", 108)]
    [InlineData("RttVar", 112)]
    [InlineData("MaxRtt", 116)]
    [InlineData("MinRtt", 120)]
    [InlineData("CountRtt", 128)]
    public void PathRodOffsetsMatchNativeLayout(string field, int expected)
        => Assert.Equal(expected, Marshal.OffsetOf<NativeTcpEstats.TCP_ESTATS_PATH_ROD_v0>(field).ToInt32());

    [Fact]
    public void PortsAreInNetworkByteOrderLikeTheConnectionTables()
    {
        // 443 = 0x01BB; the bytes in memory are 01 BB, which a little-endian DWORD reads as 0xBB01.
        Assert.Equal(0xBB01u, NativeTcpEstats.PortWord(443));
        Assert.Equal(0x5000u, NativeTcpEstats.PortWord(80));
    }

    [Fact]
    public void V4RowCarriesTheFourTuple()
    {
        var row = NativeTcpEstats.Row4(Tcp("192.168.1.10:50000", "162.254.196.26:443", 1));

        Assert.Equal(5u, row.State);
        Assert.Equal(BitConverter.ToUInt32(IPAddress.Parse("192.168.1.10").GetAddressBytes()), row.LocalAddr);
        Assert.Equal(NativeTcpEstats.PortWord(50000), row.LocalPort);
        Assert.Equal(BitConverter.ToUInt32(IPAddress.Parse("162.254.196.26").GetAddressBytes()), row.RemoteAddr);
        Assert.Equal(NativeTcpEstats.PortWord(443), row.RemotePort);
    }

    [Fact]
    public void V6RowCarriesTheFourTupleWithStateFirst()
    {
        var local = IPAddress.Parse("2a00:23c8::5");
        var remote = IPAddress.Parse("2606:4700::1111");
        var row = NativeTcpEstats.Row6(new SocketEntry(TransportProtocol.Tcp, new IPEndPoint(local, 50001), new IPEndPoint(remote, 443), 1, "Established"));

        Assert.Equal(5u, row.State);
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref row, 1)).ToArray();
        Assert.Equal(local.GetAddressBytes(), bytes[4..20]);
        Assert.Equal(remote.GetAddressBytes(), bytes[28..44]);
        Assert.Equal(NativeTcpEstats.PortWord(50001), row.LocalPort);
        Assert.Equal(NativeTcpEstats.PortWord(443), row.RemotePort);
    }

    [Fact]
    public void V6RowKeepsALinkLocalScope()
    {
        var local = IPAddress.Parse("fe80::1%12");
        var row = NativeTcpEstats.Row6(new SocketEntry(TransportProtocol.Tcp, new IPEndPoint(local, 1), new IPEndPoint(IPAddress.Parse("2606:4700::1111"), 443), 1, "Established"));
        Assert.Equal(12u, row.LocalScopeId);
    }

    private static SocketEntry Tcp(string local, string remote, int pid)
        => new(TransportProtocol.Tcp, IPEndPoint.Parse(local), IPEndPoint.Parse(remote), pid, "Established");
}

public class RttSummaryTests
{
    [Fact]
    public void MedianOfAnOddCountIsTheMiddleValue()
    {
        var summary = RttSummary.Of([300u, 12u, 14u])!.Value;
        Assert.Equal(14, summary.MedianMs);
        Assert.Equal(300, summary.WorstMs);
        Assert.Equal(3, summary.Connections);
    }

    [Fact]
    public void OneStalledConnectionDoesNotDragTheMedianUp()
    {
        var summary = RttSummary.Of([14u, 15u, 15u, 16u, 9000u])!.Value;
        Assert.Equal(15, summary.MedianMs);
        Assert.Equal(9000, summary.WorstMs);
    }

    [Fact]
    public void MedianOfAnEvenCountIsTheMidpoint()
    {
        // Not 300: two connections at 10 and 300 must not report the whole app as being at 300.
        Assert.Equal(155, RttSummary.Of([10u, 300u])!.Value.MedianMs);
    }

    [Fact]
    public void NoSamplesMeansNoSummary() => Assert.Null(RttSummary.Of([]));

    [Fact]
    public void CombiningProcessesWeightsByConnectionAndKeepsTheTrueWorst()
    {
        var combined = RttSummary.Combine([new RttSummary(20, 25, 9), new RttSummary(200, 400, 1)])!.Value;
        Assert.Equal(20, combined.MedianMs);
        Assert.Equal(400, combined.WorstMs);
        Assert.Equal(10, combined.Connections);
    }

    [Fact]
    public void CombiningNothingIsNothing() => Assert.Null(RttSummary.Combine([]));

    [Theory]
    [InlineData(14, 14, 1, "14 ms")]
    [InlineData(14, 14, 5, "14 ms")]
    [InlineData(14, 300, 1, "14 ms")]
    [InlineData(14, 300, 5, "14 ms (worst 300 ms)")]
    public void DescribesTheWorstOnlyWhenItAddsSomething(int median, int worst, int connections, string expected)
        => Assert.Equal(expected, new RttSummary(median, worst, connections).Describe());
}

public class ConnectionLatencyProbeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeEstats : ITcpEstats
    {
        public readonly HashSet<(IPEndPoint, IPEndPoint)> Enabled = [];
        public int EnableCalls;
        public int DisableCalls;
        public uint EnableError;
        public uint SampleRtt = 25;
        public uint RttCount = 3;

        public uint SetCollection(SocketEntry socket, bool enabled)
        {
            if (enabled)
            {
                EnableCalls++;
                if (EnableError != 0)
                {
                    return EnableError;
                }
                Enabled.Add((socket.Local, socket.Remote!));
            }
            else
            {
                DisableCalls++;
                Enabled.Remove((socket.Local, socket.Remote!));
            }
            return 0;
        }

        public TcpPathReading Read(SocketEntry socket)
            => Enabled.Contains((socket.Local, socket.Remote!)) ? new TcpPathReading(0, SampleRtt, RttCount) : new TcpPathReading(1168, 0, 0);
    }

    private sealed class Harness
    {
        public readonly FakeEstats Api = new();
        public List<SocketEntry> Table = [];
        public DateTimeOffset Now = Start;
        public int TableReads;
        public readonly ConnectionLatencyProbe Probe;

        public Harness()
        {
            Probe = new ConnectionLatencyProbe(Api, () => { TableReads++; return Table; }, () => Now);
        }

        public IReadOnlyList<RttSample> Pass(params int[] pids)
        {
            Now += ConnectionLatencyProbe.MinInterval;
            return Probe.Measure(pids);
        }
    }

    private static SocketEntry Conn(int pid, int index, string remote = "162.254.196.26:443", string state = "Established", string local = "192.168.1.10")
        => new(TransportProtocol.Tcp, IPEndPoint.Parse($"{local}:{(pid * 1000) + index + 1000}"), IPEndPoint.Parse(remote), pid, state);

    private static List<SocketEntry> Many(int pid, int count) => Enumerable.Range(0, count).Select(i => Conn(pid, i)).ToList();

    [Fact]
    public void ABusyProgramGetsOnlyItsShareOfSlots()
    {
        var h = new Harness { Table = Many(10, 400) };
        h.Pass(10);
        Assert.Equal(ConnectionLatencyProbe.MaxPerProcess, h.Api.Enabled.Count);
    }

    [Fact]
    public void NewConnectionsAreSpreadOverPassesAndTheTotalIsCapped()
    {
        var h = new Harness { Table = Enumerable.Range(1, 20).SelectMany(p => Many(p, 16)).ToList() };
        var pids = Enumerable.Range(1, 20).ToArray();

        h.Pass(pids);
        Assert.Equal(ConnectionLatencyProbe.MaxEnablesPerPass, h.Api.EnableCalls);

        for (var i = 0; i < 10; i++)
        {
            h.Pass(pids);
        }
        Assert.Equal(ConnectionLatencyProbe.MaxTracked, h.Probe.TrackedCount);
        Assert.Equal(ConnectionLatencyProbe.MaxTracked, h.Api.Enabled.Count);
        Assert.Equal(ConnectionLatencyProbe.MaxTracked, h.Api.EnableCalls);   // none enabled twice
    }

    [Fact]
    public void TheBusiestProgramsAreServedFirstWhenSlotsRunOut()
    {
        var h = new Harness { Table = Enumerable.Range(1, 20).SelectMany(p => Many(p, 16)).ToList() };
        var pids = Enumerable.Range(1, 20).ToArray();
        for (var i = 0; i < 6; i++)
        {
            h.Pass(pids);
        }

        var covered = h.Api.Enabled.Select(e => h.Table.First(s => s.Local.Equals(e.Item1) && s.Remote!.Equals(e.Item2)).ProcessId).Distinct().ToList();
        Assert.Equal(Enumerable.Range(1, 8), covered.Order());
    }

    [Fact]
    public void OnlyInternetTcpConnectionsOfWantedProgramsAreTouched()
    {
        var h = new Harness
        {
            Table =
            [
                Conn(1, 0),                                                 // wanted
                Conn(1, 1, remote: "192.168.0.81:8009"),                    // LAN
                Conn(1, 2, remote: "127.0.0.1:5000"),                       // loopback
                Conn(1, 3, state: "CloseWait"),                             // not established
                new SocketEntry(TransportProtocol.Udp, IPEndPoint.Parse("192.168.1.10:5353"), null, 1, null),
                Conn(2, 4)                                                  // a program nobody asked about
            ]
        };

        h.Pass(1);

        Assert.Single(h.Api.Enabled);
    }

    [Fact]
    public void AlreadyEnabledConnectionsAreNotEnabledAgain()
    {
        var h = new Harness { Table = Many(1, 4) };
        h.Pass(1);
        h.Pass(1);
        h.Pass(1);
        Assert.Equal(4, h.Api.EnableCalls);
    }

    [Fact]
    public void ASampleAppearsOnTheNextPassNotTheOneThatEnabledIt()
    {
        var h = new Harness { Table = Many(1, 3) };

        Assert.Empty(h.Pass(1));
        var second = h.Pass(1);

        Assert.Equal(3, second.Count);
        Assert.All(second, s => Assert.Equal(25u, s.Milliseconds));
        Assert.All(second, s => Assert.Equal(1, s.ProcessId));
    }

    [Fact]
    public void ZeroAndMissingSamplesAreNotReportedAsZeroMilliseconds()
    {
        var h = new Harness { Table = Many(1, 2) };
        h.Pass(1);

        h.Api.SampleRtt = 0;
        Assert.Empty(h.Pass(1));

        h.Api.SampleRtt = 30;
        h.Api.RttCount = 0;
        Assert.Empty(h.Pass(1));

        h.Api.RttCount = 1;
        h.Api.SampleRtt = uint.MaxValue;
        Assert.Empty(h.Pass(1));
    }

    [Fact]
    public void AConnectionThatStopsBeingWantedIsSwitchedBackOffWhileItIsStillOpen()
    {
        var h = new Harness { Table = Many(1, 2) };
        h.Pass(1);
        Assert.Equal(2, h.Api.Enabled.Count);

        h.Pass(2);   // program 1 went quiet; its sockets are still in the table

        Assert.Empty(h.Api.Enabled);
        Assert.Equal(2, h.Api.DisableCalls);
        Assert.Equal(0, h.Probe.TrackedCount);
    }

    [Fact]
    public void AConnectionThatHasClosedIsForgottenWithoutACall()
    {
        var h = new Harness { Table = Many(1, 2) };
        h.Pass(1);

        h.Table = [];
        h.Pass(1);

        Assert.Equal(0, h.Api.DisableCalls);
        Assert.Equal(0, h.Probe.TrackedCount);
    }

    [Fact]
    public void RefusalToEnableSwitchesTheProbeOffQuietly()
    {
        var h = new Harness { Table = Many(1, 3) };
        h.Api.EnableError = 5;   // access denied

        Assert.Empty(h.Pass(1));
        Assert.NotNull(h.Probe.Problem);

        var calls = h.Api.EnableCalls;
        var reads = h.TableReads;
        Assert.Empty(h.Pass(1));
        Assert.Equal(calls, h.Api.EnableCalls);
        Assert.Equal(reads, h.TableReads);
    }

    [Fact]
    public void ConnectionsThatClosedMidPassDoNotSwitchTheProbeOff()
    {
        var h = new Harness { Table = Many(1, 3) };
        h.Api.EnableError = 1168;   // not found: the usual race

        h.Pass(1);
        Assert.Null(h.Probe.Problem);
    }

    [Fact]
    public void ManyRefusalsWithNoSuccessEverSwitchTheProbeOff()
    {
        var h = new Harness { Table = Many(1, 12) };
        h.Api.EnableError = 87;   // invalid parameter, e.g. a layout this Windows doesn't accept

        h.Pass(1);
        Assert.NotNull(h.Probe.Problem);
    }

    [Fact]
    public void ASlowCallerDoesNotMakeThePassRunMoreOftenThanTheInterval()
    {
        var h = new Harness { Table = Many(1, 2) };
        h.Probe.Measure([1]);
        var reads = h.TableReads;

        h.Now += TimeSpan.FromSeconds(1);
        h.Probe.Measure([1]);
        h.Probe.Measure([1]);

        Assert.Equal(reads, h.TableReads);
    }

    [Fact]
    public void ALinkLocalScopeIsDroppedSoTheAdapterMapStillMatches()
    {
        var local = IPAddress.Parse("2a00:23c8::5%7");
        var h = new Harness
        {
            Table = [new SocketEntry(TransportProtocol.Tcp, new IPEndPoint(local, 50000), IPEndPoint.Parse("[2606:4700::1111]:443"), 1, "Established")]
        };
        h.Pass(1);

        var sample = Assert.Single(h.Pass(1));
        Assert.Equal(IPAddress.Parse("2a00:23c8::5"), sample.Local);
    }

    [Fact]
    public void DisposeSwitchesTrackedConnectionsOff()
    {
        var h = new Harness { Table = Many(1, 3) };
        h.Pass(1);

        h.Probe.Dispose();

        Assert.Empty(h.Api.Enabled);
    }
}
