using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetRoute.Windows.Traffic;

public enum PingStatus
{
    /// <summary>An echo reply came back.</summary>
    Reply,

    /// <summary>Nothing came back in time, or Windows reported the destination unreachable.</summary>
    NoReply,

    /// <summary>
    /// Windows would not send this from the named network: its route for that source address
    /// leaves by a different adapter. Not sent, because a number from the wrong line would be a lie.
    /// </summary>
    WrongInterface,

    /// <summary>The call itself failed (no ICMP handle, unsupported address). Treated like no reply.</summary>
    Failed
}

public readonly record struct PingOutcome(PingStatus Status, int Milliseconds = 0);

/// <summary>Sends one echo from a given local address, for the peer latency probe. Exists so the probe's bookkeeping can be tested without a network.</summary>
internal interface IPinger
{
    /// <param name="source">The local address of the network the app uses; the echo leaves from it.</param>
    /// <param name="adapterLuid">The network that address must route out of, checked before sending.</param>
    PingOutcome Ping(IPAddress source, IPAddress destination, ulong adapterLuid, TimeSpan timeout);
}

/// <summary>
/// ICMP echo through iphlpapi (IcmpSendEcho2Ex / Icmp6SendEcho2), with the source address set and
/// the route checked first.
///
/// <para>Why check: on Windows a packet leaves by the adapter that owns its source address, so
/// binding to the app's address is what puts the probe on the app's line. But if that adapter has
/// no usable route to the destination, Windows can quietly send it some other way, and the number
/// would then describe a line the app is not on. <c>GetBestRoute2</c> is asked which adapter that
/// source and destination would use, and the ping is only sent if it is the expected one.</para>
/// </summary>
internal sealed class NativeIcmpPinger : IPinger
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const uint IP_SUCCESS = 0;
    private const int ReplyBufferSize = 256;   // ICMP_ECHO_REPLY plus the 8 bytes of data plus room for an ICMP error

    public PingOutcome Ping(IPAddress source, IPAddress destination, ulong adapterLuid, TimeSpan timeout)
    {
        if (source.AddressFamily != destination.AddressFamily
            || source.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return new(PingStatus.Failed);
        }

        try
        {
            var route = RouteLuid(source, destination);
            if (route is { } luid && luid != adapterLuid)
            {
                return new(PingStatus.WrongInterface);
            }
            if (route is null)
            {
                return new(PingStatus.NoReply);   // no route at all from that address
            }

            return source.AddressFamily == AddressFamily.InterNetwork
                ? PingV4(source, destination, timeout)
                : PingV6(source, destination, timeout);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return new(PingStatus.Failed);
        }
    }

    private static PingOutcome PingV4(IPAddress source, IPAddress destination, TimeSpan timeout)
    {
        var handle = IcmpCreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return new(PingStatus.Failed);
        }
        try
        {
            var payload = new byte[] { 0x4e, 0x52, 0x74, 0x65, 0x73, 0x74, 0x00, 0x00 };   // "NRtest"
            var reply = new byte[ReplyBufferSize];
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                BitConverter.ToUInt32(source.GetAddressBytes(), 0), BitConverter.ToUInt32(destination.GetAddressBytes(), 0),
                payload, (ushort)payload.Length, IntPtr.Zero, reply, (uint)reply.Length, (uint)timeout.TotalMilliseconds);
            return Read(count, reply, status: 4, rtt: 8, started);
        }
        finally
        {
            IcmpCloseHandle(handle);
        }
    }

    private static PingOutcome PingV6(IPAddress source, IPAddress destination, TimeSpan timeout)
    {
        var handle = Icmp6CreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return new(PingStatus.Failed);
        }
        try
        {
            var payload = new byte[] { 0x4e, 0x52, 0x74, 0x65, 0x73, 0x74, 0x00, 0x00 };
            var reply = new byte[ReplyBufferSize];
            var src = SockAddr(source);
            var dst = SockAddr(destination);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var count = Icmp6SendEcho2(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, src, dst,
                payload, (ushort)payload.Length, IntPtr.Zero, reply, (uint)reply.Length, (uint)timeout.TotalMilliseconds);

            // ICMPV6_ECHO_REPLY: IPV6_ADDRESS_EX is packed to 26 bytes, so Status is at 26 and RoundTripTime at 30.
            return Read(count, reply, status: 26, rtt: 30, started);
        }
        finally
        {
            IcmpCloseHandle(handle);
        }
    }

    private static PingOutcome Read(uint count, byte[] reply, int status, int rtt, long started)
    {
        if (count == 0 || BitConverter.ToUInt32(reply, status) != IP_SUCCESS)
        {
            return new(PingStatus.NoReply);
        }

        // The reply's own figure is whole milliseconds. It is checked against the wall clock around
        // the call, which cannot be less than the true round trip, so a mis-read field can't show up
        // as a wild number.
        var elapsed = (int)Math.Ceiling(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var reported = (int)BitConverter.ToUInt32(reply, rtt);
        var ms = reported > elapsed + 1 ? elapsed : reported;
        return new(PingStatus.Reply, Math.Max(1, ms));
    }

    /// <summary>The adapter Windows would send source→destination out of, or null when it has no route.</summary>
    internal static ulong? RouteLuid(IPAddress source, IPAddress destination)
    {
        var src = SockAddrInet(source);
        var dst = SockAddrInet(destination);
        var row = new byte[256];   // MIB_IPFORWARD_ROW2 is 104 bytes; its first field is the adapter's LUID
        var best = new byte[28];
        var result = GetBestRoute2(IntPtr.Zero, 0, src, dst, 0, row, best);
        return result == 0 ? BitConverter.ToUInt64(row, 0) : null;
    }

    /// <summary>SOCKADDR_INET (28 bytes): family, port, then the address at the offset for its family.</summary>
    internal static byte[] SockAddrInet(IPAddress address)
    {
        var bytes = new byte[28];
        var raw = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            BitConverter.GetBytes((ushort)AF_INET).CopyTo(bytes, 0);
            raw.CopyTo(bytes, 4);
        }
        else
        {
            BitConverter.GetBytes((ushort)AF_INET6).CopyTo(bytes, 0);
            raw.CopyTo(bytes, 8);
            // sin6_scope_id at 24 stays 0: the addresses probed are global, not link-local.
        }
        return bytes;
    }

    /// <summary>A sockaddr_in6 is the same 28 bytes the SOCKADDR_INET holds for IPv6.</summary>
    private static byte[] SockAddr(IPAddress address) => SockAddrInet(address);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr Icmp6CreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(IntPtr handle, IntPtr evt, IntPtr apc, IntPtr apcContext, uint source, uint destination,
        byte[] request, ushort requestSize, IntPtr options, byte[] reply, uint replySize, uint timeout);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint Icmp6SendEcho2(IntPtr handle, IntPtr evt, IntPtr apc, IntPtr apcContext, byte[] source, byte[] destination,
        byte[] request, ushort requestSize, IntPtr options, byte[] reply, uint replySize, uint timeout);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestRoute2(IntPtr interfaceLuid, uint interfaceIndex, byte[] source, byte[] destination,
        uint sortOptions, byte[] bestRoute, byte[] bestSource);
}
