using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetRoute.Windows.Traffic;

/// <summary>What one read of a connection's path statistics gave back.</summary>
internal readonly record struct TcpPathReading(uint Error, uint SampleRttMs, uint RttCount);

/// <summary>The part of TCP ESTATS the latency probe needs, so its bookkeeping can be tested without a live TCP stack.</summary>
internal interface ITcpEstats
{
    /// <summary>Turns path-statistics collection on or off for one connection. Returns a Win32 error, 0 for success.</summary>
    uint SetCollection(SocketEntry socket, bool enabled);

    TcpPathReading Read(SocketEntry socket);
}

/// <summary>
/// TCP extended statistics (ESTATS) from iphlpapi: the round-trip time Windows' own TCP stack
/// is already tracking for each connection.
///
/// <para>Collection is off by default and has to be switched on per connection, which needs
/// administrator rights (the service has them, the GUI does not). The calls take the
/// connection's four-tuple in a MIB_TCPROW or MIB_TCP6ROW, and these are NOT the layouts of
/// the MIB_TCPROW_OWNER_PID rows <see cref="ConnectionTable"/> reads: the v4 row is the
/// owner-pid row minus the trailing pid, but the v6 row puts the state first, where the
/// owner-pid v6 row puts it near the end. Passing the wrong shape does not fail loudly; it
/// matches no connection and reports not-found. Layouts are checked against tcpmib.h and
/// tcpestats.h in the tests.</para>
/// </summary>
internal sealed unsafe class NativeTcpEstats : ITcpEstats
{
    private const int TcpConnectionEstatsPath = 3;   // TCP_ESTATS_TYPE
    private const uint Established = 5;              // MIB_TCP_STATE_ESTAB

    /// <summary>MIB_TCPROW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_TCPROW
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    /// <summary>MIB_TCP6ROW. IN6_ADDR is a 16-byte array, hence the fixed buffers.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_TCP6ROW
    {
        public uint State;
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public fixed byte RemoteAddr[16];
        public uint RemoteScopeId;
        public uint RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TCP_ESTATS_PATH_RW_v0
    {
        public byte EnableCollection;
    }

    /// <summary>
    /// TCP_ESTATS_PATH_ROD_v0 is 40 consecutive ULONGs. Only the round-trip fields are read, so
    /// the rest are left unnamed rather than transcribed, but the size is pinned because the
    /// call rejects a buffer of any other size.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 160)]
    internal struct TCP_ESTATS_PATH_ROD_v0
    {
        [FieldOffset(104)] public uint SampleRtt;
        [FieldOffset(108)] public uint SmoothedRtt;
        [FieldOffset(112)] public uint RttVar;
        [FieldOffset(116)] public uint MaxRtt;
        [FieldOffset(120)] public uint MinRtt;
        [FieldOffset(128)] public uint CountRtt;
    }

    public uint SetCollection(SocketEntry socket, bool enabled)
    {
        var rw = new TCP_ESTATS_PATH_RW_v0 { EnableCollection = enabled ? (byte)1 : (byte)0 };
        var size = (uint)sizeof(TCP_ESTATS_PATH_RW_v0);
        if (socket.Local.AddressFamily == AddressFamily.InterNetwork)
        {
            var row = Row4(socket);
            return SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsPath, ref rw, 0, size, 0);
        }
        var row6 = Row6(socket);
        return SetPerTcp6ConnectionEStats(ref row6, TcpConnectionEstatsPath, ref rw, 0, size, 0);
    }

    public TcpPathReading Read(SocketEntry socket)
    {
        var rod = new TCP_ESTATS_PATH_ROD_v0();
        var size = (uint)sizeof(TCP_ESTATS_PATH_ROD_v0);
        uint error;
        if (socket.Local.AddressFamily == AddressFamily.InterNetwork)
        {
            var row = Row4(socket);
            error = GetPerTcpConnectionEStats(ref row, TcpConnectionEstatsPath, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, ref rod, 0, size);
        }
        else
        {
            var row6 = Row6(socket);
            error = GetPerTcp6ConnectionEStats(ref row6, TcpConnectionEstatsPath, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, ref rod, 0, size);
        }
        return new TcpPathReading(error, rod.SampleRtt, rod.CountRtt);
    }

    internal static MIB_TCPROW Row4(SocketEntry socket) => new()
    {
        State = Established,
        LocalAddr = BitConverter.ToUInt32(socket.Local.Address.GetAddressBytes()),
        LocalPort = PortWord(socket.Local.Port),
        RemoteAddr = BitConverter.ToUInt32(socket.Remote!.Address.GetAddressBytes()),
        RemotePort = PortWord(socket.Remote.Port)
    };

    internal static MIB_TCP6ROW Row6(SocketEntry socket)
    {
        var row = new MIB_TCP6ROW
        {
            State = Established,
            LocalScopeId = (uint)socket.Local.Address.ScopeId,
            LocalPort = PortWord(socket.Local.Port),
            RemoteScopeId = (uint)socket.Remote!.Address.ScopeId,
            RemotePort = PortWord(socket.Remote.Port)
        };
        socket.Local.Address.TryWriteBytes(new Span<byte>(row.LocalAddr, 16), out _);
        socket.Remote.Address.TryWriteBytes(new Span<byte>(row.RemoteAddr, 16), out _);
        return row;
    }

    /// <summary>Ports sit in the low 16 bits of a DWORD in network byte order, as in the connection tables.</summary>
    internal static uint PortWord(int port) => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint SetPerTcpConnectionEStats(ref MIB_TCPROW row, int type, ref TCP_ESTATS_PATH_RW_v0 rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint SetPerTcp6ConnectionEStats(ref MIB_TCP6ROW row, int type, ref TCP_ESTATS_PATH_RW_v0 rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetPerTcpConnectionEStats(ref MIB_TCPROW row, int type, IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize, ref TCP_ESTATS_PATH_ROD_v0 rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetPerTcp6ConnectionEStats(ref MIB_TCP6ROW row, int type, IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize, ref TCP_ESTATS_PATH_ROD_v0 rod, uint rodVersion, uint rodSize);
}
