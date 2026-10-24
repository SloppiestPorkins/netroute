using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using NetRoute.Core.Adapters;

namespace NetRoute.Windows.Split;

/// <summary>
/// What <see cref="DefaultRouteManager"/> needs from Windows' IP stack. An interface so the
/// save/restore logic can be tested without touching a real adapter.
/// </summary>
internal interface IMetricApi
{
    /// <summary>The IPv4 interface's metric settings, or null when no such interface exists right now.</summary>
    InterfaceMetric? GetIpv4Interface(ulong luid);

    /// <summary>Sets automatic metric on, or off with <paramref name="metric"/>. Throws on any failure.</summary>
    void SetIpv4Interface(ulong luid, bool automatic, uint metric);

    /// <summary>Lowest route metric among the interface's IPv4 default routes, or null when it has none.</summary>
    uint? LowestDefaultRouteMetric(ulong luid);

    /// <summary>The interface Windows would use to reach <paramref name="destination"/>, or null.</summary>
    uint? BestInterfaceIndex(IPAddress destination);
}

internal readonly record struct InterfaceMetric(bool Automatic, uint Metric, bool Connected);

/// <summary>
/// Mirrors of the IP Helper structures and calls used to read and set IPv4 interface metrics.
///
/// <para>Same conventions as <c>NetRoute.Core.Interop.IpHelper</c>: blittable sequential structs,
/// the interface LUID as the identity, a non-zero return code turned into a
/// <see cref="Win32Exception"/>. Layouts are x64 and are pinned by <c>IpInterfaceLayoutTests</c>.
/// SetIpInterfaceEntry rewrites the whole row, so the row is always fetched first and only
/// UseAutomaticMetric and Metric are changed; the offsets of those two are the ones that matter.</para>
/// </summary>
internal static unsafe class IpInterfaceNative
{
    internal const int AF_INET = 2;
    internal const int ERROR_SUCCESS = 0;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_NOT_FOUND = 1168;

    /// <summary>netioapi.h MIB_IPINTERFACE_ROW. ScopeLevelCount is 16.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_IPINTERFACE_ROW
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled;
        public byte ForwardingEnabled;
        public byte WeakHostSend;
        public byte WeakHostReceive;
        public byte UseAutomaticMetric;
        public byte UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported;
        public byte OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;
        public uint RouterDiscoveryBehavior;
        public uint DadTransmits;
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;
        public uint LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;
        public fixed uint ZoneIndices[16];
        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;
        public byte Connected;
        public byte SupportsWakeUpPatterns;
        public byte SupportsNeighborDiscovery;
        public byte SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload;
        public byte ReceiveOffload;
        public byte DisableDefaultRoutes;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern void InitializeIpInterfaceEntry(MIB_IPINTERFACE_ROW* row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern int GetIpInterfaceEntry(MIB_IPINTERFACE_ROW* row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern int SetIpInterfaceEntry(MIB_IPINTERFACE_ROW* row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern int GetBestInterfaceEx(byte* destination, uint* bestIfIndex);

    internal static Win32Exception Fail(string what, int code)
        => code == ERROR_ACCESS_DENIED
            ? new Win32Exception(code, $"{what}: NetRoute needs administrator rights to change connection settings.")
            : new Win32Exception(code, $"{what}: {new Win32Exception(code).Message} (error {code}).");
}

/// <summary>The real thing. Setting needs administrator rights; reading does not.</summary>
internal sealed unsafe class WindowsMetricApi : IMetricApi
{
    private readonly IDefaultRouteSource _routes;

    public WindowsMetricApi(IDefaultRouteSource? routes = null) => _routes = routes ?? new DefaultRouteTable();

    public InterfaceMetric? GetIpv4Interface(ulong luid)
    {
        var row = Fetch(luid, out var code);
        if (code == IpInterfaceNative.ERROR_NOT_FOUND)
        {
            return null;
        }
        if (code != IpInterfaceNative.ERROR_SUCCESS)
        {
            throw IpInterfaceNative.Fail("GetIpInterfaceEntry", code);
        }
        return new InterfaceMetric(row.UseAutomaticMetric != 0, row.Metric, row.Connected != 0);
    }

    public void SetIpv4Interface(ulong luid, bool automatic, uint metric)
    {
        var row = Fetch(luid, out var code);
        if (code != IpInterfaceNative.ERROR_SUCCESS)
        {
            throw IpInterfaceNative.Fail("GetIpInterfaceEntry", code);
        }

        // IPv4 rows come back with a site prefix length that SetIpInterfaceEntry rejects.
        row.SitePrefixLength = 0;
        row.UseAutomaticMetric = automatic ? (byte)1 : (byte)0;
        if (!automatic)
        {
            row.Metric = metric;
        }

        code = IpInterfaceNative.SetIpInterfaceEntry(&row);
        if (code != IpInterfaceNative.ERROR_SUCCESS)
        {
            throw IpInterfaceNative.Fail("SetIpInterfaceEntry", code);
        }
    }

    public uint? LowestDefaultRouteMetric(ulong luid)
    {
        uint? lowest = null;
        foreach (var route in _routes.ReadIpv4().Where(r => r.Luid == luid))
        {
            lowest = lowest is null ? route.RouteMetric : Math.Min(lowest.Value, route.RouteMetric);
        }
        return lowest;
    }

    public uint? BestInterfaceIndex(IPAddress destination)
    {
        // sockaddr_in: family (2), port (2), address (4), zero (8).
        var sockaddr = stackalloc byte[16];
        new Span<byte>(sockaddr, 16).Clear();
        sockaddr[0] = IpInterfaceNative.AF_INET;
        destination.GetAddressBytes().CopyTo(new Span<byte>(sockaddr + 4, 4));
        uint index = 0;
        var code = IpInterfaceNative.GetBestInterfaceEx(sockaddr, &index);
        return code == IpInterfaceNative.ERROR_SUCCESS ? index : null;
    }

    private static IpInterfaceNative.MIB_IPINTERFACE_ROW Fetch(ulong luid, out int code)
    {
        IpInterfaceNative.MIB_IPINTERFACE_ROW row = default;
        IpInterfaceNative.InitializeIpInterfaceEntry(&row);
        row.Family = IpInterfaceNative.AF_INET;
        row.InterfaceLuid = luid;
        code = IpInterfaceNative.GetIpInterfaceEntry(&row);
        return row;
    }
}
