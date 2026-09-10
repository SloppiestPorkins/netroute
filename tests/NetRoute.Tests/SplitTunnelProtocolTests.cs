using System.Net;
using System.Text;
using NetRoute.Windows.Split;
using Xunit;

namespace NetRoute.Tests;

/// <summary>
/// Pins the byte layout NetRoute sends to Mullvad's split-tunnel driver.
///
/// <para>The expected layouts come from the driver's own client in mullvadvpn-app
/// (driver.rs) and its header (ipaddr.h). These buffers go straight into a kernel driver,
/// so a wrong offset is not a test failure in production; it is a rejected request at
/// best. These tests are the only place the layout is checked without a live driver.</para>
/// </summary>
public class SplitTunnelProtocolTests
{
    [Fact]
    public void IoctlCodesMatchCtlCodeOfDeviceType0x8000()
    {
        Assert.Equal(0x80000004u, SplitTunnelProtocol.IoctlInitialize);
        Assert.Equal(0x8000000Cu, SplitTunnelProtocol.IoctlRegisterProcesses);
        Assert.Equal(0x80000010u, SplitTunnelProtocol.IoctlRegisterIpAddresses);
        Assert.Equal(0x80000018u, SplitTunnelProtocol.IoctlSetConfiguration);
        Assert.Equal(0x80000023u, SplitTunnelProtocol.IoctlClearConfiguration);  // METHOD_NEITHER
        Assert.Equal(0x80000024u, SplitTunnelProtocol.IoctlGetState);
        Assert.Equal(0x8000002Fu, SplitTunnelProtocol.IoctlReset);                // METHOD_NEITHER
    }

    [Fact]
    public void ConfigurationUsesStringAreaRelativeOffsetsAndByteLengths()
    {
        var paths = new[] { @"\Device\HarddiskVolume3\a.exe", @"\Device\HarddiskVolume3\bb.exe" };
        var buffer = SplitTunnelProtocol.Configuration(paths);

        var a = Encoding.Unicode.GetBytes(paths[0]);
        var b = Encoding.Unicode.GetBytes(paths[1]);
        var stringsStart = 16 + (2 * 16);

        Assert.Equal(stringsStart + a.Length + b.Length, buffer.Length);
        Assert.Equal(2UL, BitConverter.ToUInt64(buffer, 0));
        Assert.Equal((ulong)buffer.Length, BitConverter.ToUInt64(buffer, 8));

        Assert.Equal(0UL, BitConverter.ToUInt64(buffer, 16));
        Assert.Equal((ushort)a.Length, BitConverter.ToUInt16(buffer, 24));
        Assert.Equal((ulong)a.Length, BitConverter.ToUInt64(buffer, 32));      // relative to string area
        Assert.Equal((ushort)b.Length, BitConverter.ToUInt16(buffer, 40));

        Assert.Equal(paths[0], Encoding.Unicode.GetString(buffer, stringsStart, a.Length));
        Assert.Equal(paths[1], Encoding.Unicode.GetString(buffer, stringsStart + a.Length, b.Length));
    }

    [Fact]
    public void ProcessRegistryEntriesAre32BytesAndEmptyPathsHaveNoString()
    {
        var entries = new[]
        {
            new ProcessEntry(4, 0, null),
            new ProcessEntry(1234, 4, @"\Device\HarddiskVolume3\x.exe")
        };
        var buffer = SplitTunnelProtocol.ProcessRegistry(entries);
        var name = Encoding.Unicode.GetBytes(entries[1].DevicePath!);
        var stringsStart = 16 + (2 * 32);

        Assert.Equal(stringsStart + name.Length, buffer.Length);
        Assert.Equal(2UL, BitConverter.ToUInt64(buffer, 0));
        Assert.Equal((ulong)buffer.Length, BitConverter.ToUInt64(buffer, 8));

        Assert.Equal(4UL, BitConverter.ToUInt64(buffer, 16));
        Assert.Equal(0, BitConverter.ToUInt16(buffer, 16 + 24));

        Assert.Equal(1234UL, BitConverter.ToUInt64(buffer, 48));
        Assert.Equal(4UL, BitConverter.ToUInt64(buffer, 56));
        Assert.Equal(0UL, BitConverter.ToUInt64(buffer, 64));
        Assert.Equal((ushort)name.Length, BitConverter.ToUInt16(buffer, 72));
        Assert.Equal(entries[1].DevicePath, Encoding.Unicode.GetString(buffer, stringsStart, name.Length));
    }

    [Fact]
    public void IpAddressesAreNetworkOrderOctetsInHeaderFieldOrder()
    {
        var buffer = SplitTunnelProtocol.IpAddresses(
            IPAddress.Parse("192.168.7.6"), IPAddress.Parse("192.168.0.51"),
            null, IPAddress.Parse("2001:db8::1"));

        Assert.Equal(40, buffer.Length);
        Assert.Equal(new byte[] { 192, 168, 7, 6 }, buffer[0..4]);
        Assert.Equal(new byte[] { 192, 168, 0, 51 }, buffer[4..8]);
        Assert.All(buffer[8..24], b => Assert.Equal(0, b));                 // no tunnel IPv6
        Assert.Equal(IPAddress.Parse("2001:db8::1").GetAddressBytes(), buffer[24..40]);
    }

    [Fact]
    public void IpAddressesRejectTheWrongFamily()
    {
        Assert.Throws<ArgumentException>(() =>
            SplitTunnelProtocol.IpAddresses(IPAddress.Parse("::1"), null, null, null));
    }

    [Fact]
    public void SublayerGuidsAreBaselineThenDns()
    {
        var baseline = Guid.NewGuid();
        var dns = Guid.NewGuid();
        var buffer = SplitTunnelProtocol.SublayerGuids(baseline, dns);
        Assert.Equal(baseline, new Guid(buffer[0..16]));
        Assert.Equal(dns, new Guid(buffer[16..32]));
    }

    [Fact]
    public void DevicePathOfARealFileIsAnNtPath()
    {
        var path = DevicePaths.ToNtPath(@"C:\Windows\System32\curl.exe");
        Assert.StartsWith(@"\Device\", path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"\Windows\System32\curl.exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessSnapshotIncludesThisProcessWithADevicePath()
    {
        var self = ProcessSnapshot.Capture().SingleOrDefault(p => p.ProcessId == (uint)Environment.ProcessId);
        Assert.NotNull(self);
        Assert.StartsWith(@"\Device\", self!.DevicePath, StringComparison.OrdinalIgnoreCase);
    }
}
