using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetRoute.Windows.Traffic;

/// <summary>
/// One ICMP echo sent from a chosen local address. Windows sends from the interface that owns
/// the source address, so this measures that adapter's own path to the internet, not whichever
/// adapter happens to be the default. Needs no administrator rights.
/// </summary>
public static unsafe class LinkProbe
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll")]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(
        IntPtr handle, IntPtr eventHandle, IntPtr apcRoutine, IntPtr apcContext,
        uint sourceAddress, uint destinationAddress, byte* requestData, ushort requestSize,
        IntPtr requestOptions, byte* replyBuffer, uint replySize, uint timeout);

    private const int PayloadSize = 32;

    // ICMP_ECHO_REPLY (40 bytes on x64) + the echoed payload + room for an ICMP error message.
    private const int ReplySize = 40 + PayloadSize + 8 + 64;

    /// <summary>Round-trip time in milliseconds, or null when no reply came back in time.</summary>
    public static int? Ping(IPAddress source, IPAddress destination, int timeoutMs = 1000)
    {
        if (source.AddressFamily != AddressFamily.InterNetwork || destination.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Only IPv4 is supported.");
        }

        var handle = IcmpCreateFile();
        if (handle == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var payload = stackalloc byte[PayloadSize];
            var reply = stackalloc byte[ReplySize];
            new Span<byte>(payload, PayloadSize).Fill((byte)'n');

            var replies = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                ToIpAddr(source), ToIpAddr(destination), payload, PayloadSize, IntPtr.Zero, reply, ReplySize, (uint)timeoutMs);
            if (replies == 0)
            {
                return null;
            }
            var status = *(uint*)(reply + 4);         // ICMP_ECHO_REPLY.Status, 0 = IP_SUCCESS
            return status == 0 ? (int)*(uint*)(reply + 8) : null;   // .RoundTripTime
        }
        finally
        {
            IcmpCloseHandle(handle);
        }
    }

    // IPAddr is the address in network byte order, i.e. the bytes as written.
    private static uint ToIpAddr(IPAddress address) => BitConverter.ToUInt32(address.GetAddressBytes(), 0);
}
