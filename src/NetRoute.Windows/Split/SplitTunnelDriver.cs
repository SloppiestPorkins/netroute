using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetRoute.Windows.Split;

/// <summary>
/// NetRoute's user-mode agent for Mullvad's Microsoft-signed split-tunnel driver.
///
/// <para>Why a third-party driver: moving an unmodified app onto a non-default adapter
/// requires rewriting its socket's local address at WFP bind/connect redirect, which only
/// a kernel callout can do. On a Secure Boot machine running BattlEye, EAC or Javelin, a
/// self-built or test-signed driver is not an option, while this one is already signed and
/// in daily use. See docs/RESEARCH.md.</para>
///
/// <para>What the driver does, as far as NetRoute is concerned: processes whose image path
/// is in the configuration (and their descendants; exclusion is inherited) get their
/// wildcard binds and connects rebound to the "internet" address, and are blocked from
/// using the "tunnel" address. Everything else is left alone.</para>
///
/// <para>Two hazards this class exists to contain:</para>
/// <list type="bullet">
/// <item>The device is exclusive. Only one handle can be open, so this object must be the
/// only owner and must be disposed promptly.</item>
/// <item>Unloading the driver while it is not reset makes it bugcheck the machine on
/// purpose. NetRoute therefore never stops the driver service; <see cref="Reset"/> is how
/// splitting is turned off.</item>
/// </list>
/// </summary>
public sealed class SplitTunnelDriver : IDisposable
{
    private readonly SafeFileHandle _handle;

    private SplitTunnelDriver(SafeFileHandle handle) => _handle = handle;

    /// <summary>Opens the driver's device. Fails if the driver is not loaded, or another program holds it.</summary>
    public static SplitTunnelDriver Open()
    {
        var handle = CreateFileW(
            SplitTunnelProtocol.DevicePath, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero,
            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new SplitTunnelException(error switch
            {
                ERROR_FILE_NOT_FOUND => "The split-tunnel driver is not running.",
                ERROR_ACCESS_DENIED => "Another program (probably Mullvad VPN) is already using the split-tunnel driver, or NetRoute is not running as administrator.",
                _ => "NetRoute could not open the split-tunnel driver."
            }, new Win32Exception(error));
        }

        return new SplitTunnelDriver(handle);
    }

    public SplitTunnelState GetState()
    {
        var output = Control(SplitTunnelProtocol.IoctlGetState, null, sizeof(ulong));
        return (SplitTunnelState)BitConverter.ToUInt64(output, 0);
    }

    /// <summary>
    /// Returns the driver to STARTED: splitting off, every filter and registration dropped.
    /// This is NetRoute's off switch. See the class notes for why the service is never stopped instead.
    /// </summary>
    public void Reset() => Control(SplitTunnelProtocol.IoctlReset, null, 0);

    /// <summary>
    /// Brings the driver from any state to READY with a fresh process registry, following
    /// the same order as Mullvad's client: GetState, Reset if needed, Initialize, RegisterProcesses.
    /// The sublayers must already exist (see <see cref="SplitTunnelSublayers"/>).
    /// </summary>
    public void Reinitialize(Guid baselineSublayer, Guid dnsSublayer)
    {
        if (GetState() != SplitTunnelState.Started)
        {
            Reset();
        }

        Control(SplitTunnelProtocol.IoctlInitialize, SplitTunnelProtocol.SublayerGuids(baselineSublayer, dnsSublayer), 0);
        Control(SplitTunnelProtocol.IoctlRegisterProcesses, SplitTunnelProtocol.ProcessRegistry(ProcessSnapshot.Capture()), 0);
    }

    /// <summary>
    /// "Tunnel" is the address split apps are kept OFF; "internet" is the address they are moved ONTO.
    /// The driver only engages when a tunnel address is valid.
    /// </summary>
    public void RegisterIpAddresses(IPAddress? tunnelV4, IPAddress? internetV4, IPAddress? tunnelV6, IPAddress? internetV6)
        => Control(SplitTunnelProtocol.IoctlRegisterIpAddresses,
            SplitTunnelProtocol.IpAddresses(tunnelV4, internetV4, tunnelV6, internetV6), 0);

    /// <summary>Sets the complete list of split image paths (NT device paths). Replaces the previous list.</summary>
    public void SetConfiguration(IReadOnlyList<string> ntImagePaths)
    {
        if (ntImagePaths.Count == 0)
        {
            ClearConfiguration();
            return;
        }
        Control(SplitTunnelProtocol.IoctlSetConfiguration, SplitTunnelProtocol.Configuration(ntImagePaths), 0);
    }

    public void ClearConfiguration() => Control(SplitTunnelProtocol.IoctlClearConfiguration, null, 0);

    private byte[] Control(uint code, byte[]? input, int outputSize)
    {
        var output = outputSize > 0 ? new byte[outputSize] : null;
        if (!DeviceIoControl(_handle, code, input, input?.Length ?? 0, output, outputSize, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            throw new SplitTunnelException(
                $"The split-tunnel driver rejected a request (IOCTL 0x{code:X8}).", new Win32Exception(error));
        }
        return output ?? [];
    }

    public void Dispose() => _handle.Dispose();

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_ACCESS_DENIED = 5;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize,
        byte[]? output, int outputSize, out int returned, IntPtr overlapped);
}

/// <summary>A split-tunnel driver failure, with a message fit for the user (§31).</summary>
public sealed class SplitTunnelException(string friendlyMessage, Exception? inner = null)
    : Exception(friendlyMessage, inner);
