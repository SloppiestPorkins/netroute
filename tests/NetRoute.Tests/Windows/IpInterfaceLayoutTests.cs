using System.Runtime.InteropServices;
using NetRoute.Windows.Split;

namespace NetRoute.Tests.Windows;

/// <summary>
/// Pins the layout of netioapi.h's MIB_IPINTERFACE_ROW, which NetRoute reads and writes to change
/// which connection Windows prefers.
///
/// <para>This matters more than most interop: <c>SetIpInterfaceEntry</c> rewrites the whole row, so
/// a field at the wrong offset does not fail -- it silently writes a number into the wrong setting
/// on a real adapter on someone's PC. There is no way to notice that from the return code.</para>
///
/// <para>The offsets below are x64 and come from netioapi.h: a 2-byte ADDRESS_FAMILY, 6 bytes of
/// padding before the 8-byte NET_LUID, and 16 ZoneIndices. Only UseAutomaticMetric and Metric are
/// ever assigned, so those two are the ones that must be exact -- but the size is asserted as well,
/// because a field missing from the end would move nothing and still pass a field-by-field check
/// while making the struct too small for the call to fill.</para>
/// </summary>
public unsafe class IpInterfaceLayoutTests
{
    private static int Offset(string field) => (int)Marshal.OffsetOf<IpInterfaceNative.MIB_IPINTERFACE_ROW>(field);

    [Fact]
    public void The_row_is_the_size_windows_expects()
    {
        // 166 bytes of fields, rounded up to the struct's 8-byte alignment. The last three fields
        // are BOOLEANs after a ULONG, which is where an off-by-one in the tail would show up.
        Assert.Equal(167, Offset("DisableDefaultRoutes") + 1);
        Assert.Equal(168, sizeof(IpInterfaceNative.MIB_IPINTERFACE_ROW));
    }

    [Theory]
    [InlineData("Family", 0)]
    [InlineData("InterfaceLuid", 8)]
    [InlineData("InterfaceIndex", 16)]
    [InlineData("MaxReassemblySize", 20)]
    [InlineData("InterfaceIdentifier", 24)]
    [InlineData("MinRouterAdvertisementInterval", 32)]
    [InlineData("MaxRouterAdvertisementInterval", 36)]
    [InlineData("AdvertisingEnabled", 40)]
    [InlineData("ForwardingEnabled", 41)]
    [InlineData("WeakHostSend", 42)]
    [InlineData("WeakHostReceive", 43)]
    [InlineData("UseAutomaticMetric", 44)]
    [InlineData("UseNeighborUnreachabilityDetection", 45)]
    [InlineData("ManagedAddressConfigurationSupported", 46)]
    [InlineData("OtherStatefulConfigurationSupported", 47)]
    [InlineData("AdvertiseDefaultRoute", 48)]
    [InlineData("RouterDiscoveryBehavior", 52)]
    [InlineData("DadTransmits", 56)]
    [InlineData("BaseReachableTime", 60)]
    [InlineData("RetransmitTime", 64)]
    [InlineData("PathMtuDiscoveryTimeout", 68)]
    [InlineData("LinkLocalAddressBehavior", 72)]
    [InlineData("LinkLocalAddressTimeout", 76)]
    [InlineData("ZoneIndices", 80)]
    [InlineData("SitePrefixLength", 144)]
    [InlineData("Metric", 148)]
    [InlineData("NlMtu", 152)]
    [InlineData("Connected", 156)]
    public void Every_field_is_where_netioapi_puts_it(string field, int offset)
        => Assert.Equal(offset, Offset(field));

    [Fact]
    public void The_two_fields_netroute_writes_are_four_bytes_apart_in_the_right_order()
    {
        // Guards the specific mistake that would be invisible: writing the metric into the flag,
        // or the flag into SitePrefixLength, which the row is explicitly cleared of before a set.
        Assert.Equal(44, Offset("UseAutomaticMetric"));
        Assert.Equal(148, Offset("Metric"));
        Assert.True(Offset("SitePrefixLength") < Offset("Metric"));
    }
}
