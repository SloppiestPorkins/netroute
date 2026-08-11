using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace NetRoute.Core.Interop;

/// <summary>
/// Minimal P/Invoke surface over iphlpapi's GetAdaptersAddresses.
///
/// We use this rather than System.Net.NetworkInformation because we need the
/// interface LUID, which is the stable identity NetRoute persists roles against.
/// NetworkInterface exposes the GUID but not the LUID, and the LUID is what the
/// WFP filter conditions (FWPM_CONDITION_IP_LOCAL_INTERFACE) actually take.
/// </summary>
internal static unsafe class IpHelper
{
    private const int AF_UNSPEC = 0;
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;

    private const int ERROR_SUCCESS = 0;
    private const int ERROR_BUFFER_OVERFLOW = 111;

    // GAA flags: we want gateways, and we don't need the noise.
    private const uint GAA_FLAG_INCLUDE_GATEWAYS = 0x0080;
    private const uint GAA_FLAG_SKIP_ANYCAST = 0x0002;
    private const uint GAA_FLAG_SKIP_MULTICAST = 0x0004;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetAdaptersAddresses(
        uint family, uint flags, IntPtr reserved, byte* adapterAddresses, uint* sizePointer);

    /// <summary>
    /// Truncated mirror of IP_ADAPTER_ADDRESSES_LH. Only defined through Luid —
    /// everything past it is unused, and stopping early avoids mirroring the
    /// DHCPv6 tail (which is easy to get subtly wrong and buys us nothing).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IP_ADAPTER_ADDRESSES
    {
        public uint Length;
        public uint IfIndex;
        public IntPtr Next;
        public IntPtr AdapterName;          // PCHAR (ANSI) — the {GUID} string
        public IntPtr FirstUnicastAddress;
        public IntPtr FirstAnycastAddress;
        public IntPtr FirstMulticastAddress;
        public IntPtr FirstDnsServerAddress;
        public IntPtr DnsSuffix;
        public IntPtr Description;
        public IntPtr FriendlyName;
        public fixed byte PhysicalAddress[8];
        public uint PhysicalAddressLength;
        public uint Flags;
        public uint Mtu;
        public uint IfType;
        public uint OperStatus;
        public uint Ipv6IfIndex;
        public fixed uint ZoneIndices[16];
        public IntPtr FirstPrefix;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public IntPtr FirstWinsServerAddress;
        public IntPtr FirstGatewayAddress;
        public uint Ipv4Metric;
        public uint Ipv6Metric;
        public ulong Luid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SOCKET_ADDRESS
    {
        public IntPtr lpSockaddr;
        public int iSockaddrLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IP_ADAPTER_UNICAST_ADDRESS
    {
        public uint Length;
        public uint Flags;
        public IntPtr Next;
        public SOCKET_ADDRESS Address;
        public uint PrefixOrigin;
        public uint SuffixOrigin;
        public uint DadState;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint LeaseLifetime;
        public byte OnLinkPrefixLength;
    }

    /// <summary>Shared shape of the gateway and DNS-server list nodes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IP_ADAPTER_ADDRESS_NODE
    {
        public uint Length;
        public uint Reserved;
        public IntPtr Next;
        public SOCKET_ADDRESS Address;
    }

    internal sealed record RawAdapter(
        string Guid,
        string FriendlyName,
        string Description,
        uint IfIndex,
        uint Ipv6IfIndex,
        ulong Luid,
        uint IfType,
        uint OperStatus,
        ulong TransmitLinkSpeed,
        ulong ReceiveLinkSpeed,
        uint Ipv4Metric,
        uint Ipv6Metric,
        IReadOnlyList<IPAddress> UnicastAddresses,
        IReadOnlyList<IPAddress> Gateways,
        IReadOnlyList<IPAddress> DnsServers);

    internal static List<RawAdapter> Enumerate()
    {
        const uint flags = GAA_FLAG_INCLUDE_GATEWAYS | GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST;

        // Standard two-call pattern, retried because the adapter set can grow
        // between sizing and fetching (USB Wi-Fi appearing, VPN connecting).
        uint size = 16 * 1024;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var result = GetAdaptersAddresses(AF_UNSPEC, flags, IntPtr.Zero, (byte*)buffer, &size);
                if (result == ERROR_BUFFER_OVERFLOW)
                {
                    continue;
                }
                if (result != ERROR_SUCCESS)
                {
                    throw new Win32Exception(result);
                }
                return Parse(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new Win32Exception(ERROR_BUFFER_OVERFLOW);
    }

    private static List<RawAdapter> Parse(IntPtr head)
    {
        var adapters = new List<RawAdapter>();

        for (var cursor = head; cursor != IntPtr.Zero;)
        {
            var a = Marshal.PtrToStructure<IP_ADAPTER_ADDRESSES>(cursor);

            adapters.Add(new RawAdapter(
                Guid: Marshal.PtrToStringAnsi(a.AdapterName) ?? string.Empty,
                FriendlyName: Marshal.PtrToStringUni(a.FriendlyName) ?? string.Empty,
                Description: Marshal.PtrToStringUni(a.Description) ?? string.Empty,
                IfIndex: a.IfIndex,
                Ipv6IfIndex: a.Ipv6IfIndex,
                Luid: a.Luid,
                IfType: a.IfType,
                OperStatus: a.OperStatus,
                TransmitLinkSpeed: a.TransmitLinkSpeed,
                ReceiveLinkSpeed: a.ReceiveLinkSpeed,
                Ipv4Metric: a.Ipv4Metric,
                Ipv6Metric: a.Ipv6Metric,
                UnicastAddresses: ReadUnicast(a.FirstUnicastAddress),
                Gateways: ReadAddressNodes(a.FirstGatewayAddress),
                DnsServers: ReadAddressNodes(a.FirstDnsServerAddress)));

            cursor = a.Next;
        }

        return adapters;
    }

    private static List<IPAddress> ReadUnicast(IntPtr head)
    {
        var list = new List<IPAddress>();
        for (var cursor = head; cursor != IntPtr.Zero;)
        {
            var node = Marshal.PtrToStructure<IP_ADAPTER_UNICAST_ADDRESS>(cursor);
            var address = ReadSockAddr(node.Address);
            if (address is not null)
            {
                list.Add(address);
            }
            cursor = node.Next;
        }
        return list;
    }

    private static List<IPAddress> ReadAddressNodes(IntPtr head)
    {
        var list = new List<IPAddress>();
        for (var cursor = head; cursor != IntPtr.Zero;)
        {
            var node = Marshal.PtrToStructure<IP_ADAPTER_ADDRESS_NODE>(cursor);
            var address = ReadSockAddr(node.Address);
            if (address is not null)
            {
                list.Add(address);
            }
            cursor = node.Next;
        }
        return list;
    }

    private static IPAddress? ReadSockAddr(SOCKET_ADDRESS sa)
    {
        if (sa.lpSockaddr == IntPtr.Zero || sa.iSockaddrLength <= 0)
        {
            return null;
        }

        var family = (int)(ushort)Marshal.ReadInt16(sa.lpSockaddr, 0);
        switch (family)
        {
            case AF_INET:
            {
                var bytes = new byte[4];
                Marshal.Copy(sa.lpSockaddr + 4, bytes, 0, 4);
                return new IPAddress(bytes);
            }
            case AF_INET6:
            {
                var bytes = new byte[16];
                Marshal.Copy(sa.lpSockaddr + 8, bytes, 0, 16);
                var scopeId = (uint)Marshal.ReadInt32(sa.lpSockaddr, 24);
                return new IPAddress(bytes, scopeId);
            }
            default:
                return null;
        }
    }
}
