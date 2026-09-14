using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using NetRoute.Core.Traffic;

namespace NetRoute.Windows.Traffic;

/// <summary>One socket from Windows' connection tables.</summary>
public sealed record SocketEntry(TransportProtocol Protocol, IPEndPoint Local, IPEndPoint? Remote, int ProcessId, string? TcpState);

/// <summary>
/// Reads the TCP and UDP tables with their owning process IDs (GetExtendedTcpTable /
/// GetExtendedUdpTable), for IPv4 and IPv6.
///
/// <para>Rows are parsed from raw bytes at the offsets of the MIB_*_OWNER_PID structures in
/// tcpmib.h / udpmib.h rather than marshalled into structs. A misplaced field here would
/// silently attribute traffic to the wrong adapter, and explicit offsets are easy to check.</para>
///
/// <para>Needs no privileges. Listening TCP sockets are skipped because they carry no traffic.
/// A UDP socket bound to the wildcard address has no fixed local address, so this table can't
/// tie it to an adapter. Apps moved by the split-tunnel driver are the exception: the driver
/// rebinds them to their adapter's address, so they do show up attributed.</para>
/// </summary>
public static class ConnectionTable
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    private delegate uint TableCall(IntPtr buffer, ref int size);

    public static List<SocketEntry> Snapshot()
    {
        var entries = new List<SocketEntry>();
        ParseTcp4(Fetch((IntPtr b, ref int s) => GetExtendedTcpTable(b, ref s, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0)), entries);
        ParseTcp6(Fetch((IntPtr b, ref int s) => GetExtendedTcpTable(b, ref s, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0)), entries);
        ParseUdp4(Fetch((IntPtr b, ref int s) => GetExtendedUdpTable(b, ref s, false, AF_INET, UDP_TABLE_OWNER_PID, 0)), entries);
        ParseUdp6(Fetch((IntPtr b, ref int s) => GetExtendedUdpTable(b, ref s, false, AF_INET6, UDP_TABLE_OWNER_PID, 0)), entries);
        return entries;
    }

    /// <summary>The usual two-call pattern, retried because the table can grow between sizing and reading.</summary>
    private static byte[] Fetch(TableCall call)
    {
        var size = 0;
        call(IntPtr.Zero, ref size);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            size += 4096;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var capacity = size;
                var result = call(buffer, ref capacity);
                if (result == ERROR_INSUFFICIENT_BUFFER)
                {
                    size = capacity;
                    continue;
                }
                if (result != 0)
                {
                    return [];
                }
                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, size);
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    // MIB_TCPROW_OWNER_PID: state@0, localAddr@4, localPort@8, remoteAddr@12, remotePort@16, pid@20 (24 bytes)
    internal static void ParseTcp4(byte[] t, List<SocketEntry> into)
    {
        foreach (var o in Rows(t, 24))
        {
            var state = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(o));
            if (!IsTrafficState(state))
            {
                continue;
            }
            into.Add(new SocketEntry(TransportProtocol.Tcp,
                new IPEndPoint(new IPAddress(t.AsSpan(o + 4, 4)), Port(t, o + 8)),
                new IPEndPoint(new IPAddress(t.AsSpan(o + 12, 4)), Port(t, o + 16)),
                BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(o + 20)), StateName(state)));
        }
    }

    // MIB_TCP6ROW_OWNER_PID: localAddr[16]@0, localScope@16, localPort@20, remoteAddr[16]@24,
    // remoteScope@40, remotePort@44, state@48, pid@52 (56 bytes)
    internal static void ParseTcp6(byte[] t, List<SocketEntry> into)
    {
        foreach (var o in Rows(t, 56))
        {
            var state = BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(o + 48));
            if (!IsTrafficState(state))
            {
                continue;
            }
            into.Add(new SocketEntry(TransportProtocol.Tcp,
                new IPEndPoint(V6(t, o, o + 16), Port(t, o + 20)),
                new IPEndPoint(V6(t, o + 24, o + 40), Port(t, o + 44)),
                BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(o + 52)), StateName(state)));
        }
    }

    // MIB_UDPROW_OWNER_PID: localAddr@0, localPort@4, pid@8 (12 bytes)
    internal static void ParseUdp4(byte[] t, List<SocketEntry> into)
    {
        foreach (var o in Rows(t, 12))
        {
            into.Add(new SocketEntry(TransportProtocol.Udp,
                new IPEndPoint(new IPAddress(t.AsSpan(o, 4)), Port(t, o + 4)), null,
                BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(o + 8)), null));
        }
    }

    // MIB_UDP6ROW_OWNER_PID: localAddr[16]@0, localScope@16, localPort@20, pid@24 (28 bytes)
    internal static void ParseUdp6(byte[] t, List<SocketEntry> into)
    {
        foreach (var o in Rows(t, 28))
        {
            into.Add(new SocketEntry(TransportProtocol.Udp,
                new IPEndPoint(V6(t, o, o + 16), Port(t, o + 20)), null,
                BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(o + 24)), null));
        }
    }

    /// <summary>Row offsets after the DWORD entry count, never running past the buffer.</summary>
    private static IEnumerable<int> Rows(byte[] table, int rowSize)
    {
        if (table.Length < 4)
        {
            yield break;
        }
        var count = BinaryPrimitives.ReadInt32LittleEndian(table);
        for (var i = 0; i < count && 4 + ((i + 1) * rowSize) <= table.Length; i++)
        {
            yield return 4 + (i * rowSize);
        }
    }

    /// <summary>Ports sit in the low 16 bits of a DWORD, in network byte order.</summary>
    private static int Port(byte[] t, int offset) => (t[offset] << 8) | t[offset + 1];

    private static IPAddress V6(byte[] t, int address, int scope)
        => new(t.AsSpan(address, 16), BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(scope)));

    // MIB_TCP_STATE: 2 = LISTEN carries no traffic; 11 and 12 (TIME_WAIT, DELETE_TCB) are finished.
    private static bool IsTrafficState(uint state) => state is >= 3 and <= 10;

    private static string StateName(uint state) => state switch
    {
        3 => "SynSent", 4 => "SynReceived", 5 => "Established", 6 => "FinWait1", 7 => "FinWait2",
        8 => "CloseWait", 9 => "Closing", 10 => "LastAck", _ => state.ToString()
    };

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);
}
