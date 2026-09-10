using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetRoute.Windows.Split;

/// <summary>
/// Wire format of Mullvad's split-tunnel driver (github.com/mullvad/win-split-tunnel).
///
/// <para>Everything here is taken from the driver's own user-mode client in
/// mullvadvpn-app (talpid-core/src/split_tunnel/windows/driver.rs) and its C headers
/// (src/ipaddr.h). That client is what the signed driver is actually exercised by, so it
/// is the practical specification: offsets are relative to the start of the string area,
/// lengths are in BYTES, strings are UTF-16LE without terminators, and IP addresses are
/// raw network-order octets.</para>
///
/// <para>The serialisers are pure so their byte layout can be asserted in unit tests. A
/// malformed buffer handed to a kernel driver is not a recoverable error.</para>
/// </summary>
public static class SplitTunnelProtocol
{
    public const string DevicePath = @"\\.\MULLVADSPLITTUNNEL";

    private const uint DeviceType = 0x8000;
    private const uint MethodBuffered = 0;
    private const uint MethodNeither = 3;

    private static uint Ctl(uint function, uint method) => (DeviceType << 16) | (function << 2) | method;

    public static readonly uint IoctlInitialize = Ctl(1, MethodBuffered);
    public static readonly uint IoctlDequeueEvent = Ctl(2, MethodBuffered);
    public static readonly uint IoctlRegisterProcesses = Ctl(3, MethodBuffered);
    public static readonly uint IoctlRegisterIpAddresses = Ctl(4, MethodBuffered);
    public static readonly uint IoctlGetIpAddresses = Ctl(5, MethodBuffered);
    public static readonly uint IoctlSetConfiguration = Ctl(6, MethodBuffered);
    public static readonly uint IoctlGetConfiguration = Ctl(7, MethodBuffered);
    public static readonly uint IoctlClearConfiguration = Ctl(8, MethodNeither);
    public static readonly uint IoctlGetState = Ctl(9, MethodBuffered);
    public static readonly uint IoctlQueryProcess = Ctl(10, MethodBuffered);
    public static readonly uint IoctlReset = Ctl(11, MethodNeither);

    /// <summary>ST_SUBLAYER_GUIDS: the baseline sublayer, then the DNS sublayer.</summary>
    public static byte[] SublayerGuids(Guid baseline, Guid dns)
    {
        var buffer = new byte[32];
        baseline.ToByteArray().CopyTo(buffer, 0);   // Guid.ToByteArray matches the Windows GUID layout
        dns.ToByteArray().CopyTo(buffer, 16);
        return buffer;
    }

    /// <summary>ST_IP_ADDRESSES: TunnelIpv4, InternetIpv4, TunnelIpv6, InternetIpv6. 40 bytes. Null = all zero.</summary>
    public static byte[] IpAddresses(IPAddress? tunnelV4, IPAddress? internetV4, IPAddress? tunnelV6, IPAddress? internetV6)
    {
        var buffer = new byte[40];
        Put(tunnelV4, AddressFamily.InterNetwork, buffer, 0);
        Put(internetV4, AddressFamily.InterNetwork, buffer, 4);
        Put(tunnelV6, AddressFamily.InterNetworkV6, buffer, 8);
        Put(internetV6, AddressFamily.InterNetworkV6, buffer, 24);
        return buffer;
    }

    private static void Put(IPAddress? address, AddressFamily family, byte[] buffer, int offset)
    {
        if (address is null)
        {
            return;
        }
        if (address.AddressFamily != family)
        {
            throw new ArgumentException($"{address} is not {family}.");
        }
        address.GetAddressBytes().CopyTo(buffer, offset);
    }

    /// <summary>
    /// SET_CONFIGURATION: header { SIZE_T numEntries; SIZE_T totalLength } (16 bytes),
    /// then entries { SIZE_T nameOffset; USHORT nameLength } (16 bytes each, 6 bytes of
    /// padding), then the UTF-16LE image names back to back.
    /// </summary>
    public static byte[] Configuration(IReadOnlyList<string> ntImagePaths)
    {
        var names = ntImagePaths.Select(p => Encoding.Unicode.GetBytes(p)).ToList();
        foreach (var name in names)
        {
            if (name.Length > ushort.MaxValue)
            {
                throw new ArgumentException("Image path too long for the driver's USHORT length field.");
            }
        }

        const int headerSize = 16;
        const int entrySize = 16;
        var stringsStart = headerSize + (entrySize * names.Count);
        var total = stringsStart + names.Sum(n => n.Length);
        var buffer = new byte[total];

        WriteSizeT(buffer, 0, (ulong)names.Count);
        WriteSizeT(buffer, 8, (ulong)total);

        var stringOffset = 0;
        for (var i = 0; i < names.Count; i++)
        {
            var entry = headerSize + (i * entrySize);
            WriteSizeT(buffer, entry, (ulong)stringOffset);
            BitConverter.GetBytes((ushort)names[i].Length).CopyTo(buffer, entry + 8);
            names[i].CopyTo(buffer, stringsStart + stringOffset);
            stringOffset += names[i].Length;
        }

        return buffer;
    }

    /// <summary>
    /// REGISTER_PROCESSES: header { SIZE_T numEntries; SIZE_T totalLength } (16 bytes), then
    /// entries { HANDLE pid; HANDLE parentPid; SIZE_T imageNameOffset; USHORT imageNameSize }
    /// (32 bytes each), then UTF-16LE device paths. An empty path is sent as size 0, offset 0.
    /// </summary>
    public static byte[] ProcessRegistry(IReadOnlyList<ProcessEntry> processes)
    {
        var names = processes.Select(p => Encoding.Unicode.GetBytes(p.DevicePath ?? string.Empty)).ToList();

        const int headerSize = 16;
        const int entrySize = 32;
        var stringsStart = headerSize + (entrySize * processes.Count);
        var total = stringsStart + names.Sum(n => n.Length);
        var buffer = new byte[total];

        WriteSizeT(buffer, 0, (ulong)processes.Count);
        WriteSizeT(buffer, 8, (ulong)total);

        var stringOffset = 0;
        for (var i = 0; i < processes.Count; i++)
        {
            var entry = headerSize + (i * entrySize);
            WriteSizeT(buffer, entry, processes[i].ProcessId);
            WriteSizeT(buffer, entry + 8, processes[i].ParentProcessId);

            var name = names[i];
            if (name.Length > 0 && name.Length <= ushort.MaxValue)
            {
                WriteSizeT(buffer, entry + 16, (ulong)stringOffset);
                BitConverter.GetBytes((ushort)name.Length).CopyTo(buffer, entry + 24);
                name.CopyTo(buffer, stringsStart + stringOffset);
                stringOffset += name.Length;
            }
        }

        return buffer;
    }

    private static void WriteSizeT(byte[] buffer, int offset, ulong value)
        => BitConverter.GetBytes(value).CopyTo(buffer, offset);
}

/// <summary>One process as the driver's initial process registry needs it.</summary>
public sealed record ProcessEntry(uint ProcessId, uint ParentProcessId, string? DevicePath);

public enum SplitTunnelState : ulong
{
    None = 0,
    Started = 1,
    Initialized = 2,
    Ready = 3,
    Engaged = 4,
    Terminating = 5
}
