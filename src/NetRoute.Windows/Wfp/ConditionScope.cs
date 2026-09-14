using System.Runtime.InteropServices;
using static NetRoute.Windows.Wfp.WfpNative;

namespace NetRoute.Windows.Wfp;

/// <summary>
/// Builds WFP filter conditions and owns the unmanaged memory they point at.
///
/// <para>Several condition types (app ID blobs, 64-bit interface LUIDs, package SIDs)
/// are passed to WFP by pointer rather than by value, and that memory has to stay
/// valid until <c>FwpmFilterAdd0</c> returns. Collecting it in one scope makes the
/// lifetime obvious and the cleanup unconditional.</para>
/// </summary>
internal sealed class ConditionScope : IDisposable
{
    private readonly List<IntPtr> _hGlobal = [];
    private readonly List<IntPtr> _wfpAllocated = [];
    private readonly List<IntPtr> _sids = [];
    private readonly List<IntPtr> _localAlloc = [];

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr securityDescriptor, out uint size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("userenv.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(
        string appContainerName, out IntPtr sid);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern IntPtr FreeSid(IntPtr sid);

    /// <summary>Matches a desktop application by executable path.</summary>
    public FWPM_FILTER_CONDITION0 AppId(string executablePath)
    {
        var result = FwpmGetAppIdFromFileName0(executablePath, out var blob);
        WfpException.ThrowIfFailed($"FwpmGetAppIdFromFileName0({executablePath})", result);
        _wfpAllocated.Add(blob);

        return new FWPM_FILTER_CONDITION0
        {
            fieldKey = FWPM_CONDITION_ALE_APP_ID,
            matchType = FwpMatchType.Equal,
            conditionValue = new FWP_VALUE0 { type = FwpDataType.ByteBlob, value = blob }
        };
    }

    /// <summary>
    /// Matches a packaged (Store / Game Pass) application by its package family name.
    ///
    /// <para>This keys on package identity, which is coarser than the AUMID: if a package
    /// contains several applications they share this condition. §11 warns about exactly
    /// that distinction, so the AUMID is still what the rule stores and what process
    /// discovery uses — this is only the enforcement key.</para>
    /// </summary>
    public FWPM_FILTER_CONDITION0 PackageId(string packageFamilyName)
    {
        var hr = DeriveAppContainerSidFromAppContainerName(packageFamilyName, out var sid);
        if (hr != 0)
        {
            throw new WfpException(
                $"DeriveAppContainerSidFromAppContainerName({packageFamilyName})", unchecked((uint)hr));
        }
        _sids.Add(sid);

        return new FWPM_FILTER_CONDITION0
        {
            fieldKey = FWPM_CONDITION_ALE_PACKAGE_ID,
            matchType = FwpMatchType.Equal,
            conditionValue = new FWP_VALUE0 { type = FwpDataType.Sid, value = sid }
        };
    }

    /// <summary>
    /// Matches a Windows service's traffic by its service SID, the same way Windows Firewall
    /// scopes a rule to a service. The rest of a shared svchost is left alone.
    /// </summary>
    public FWPM_FILTER_CONDITION0 ServiceUser(string serviceName)
    {
        var sddl = $"O:LSD:(A;;CC;;;{ServiceSids.For(serviceName)})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out var size))
        {
            throw new WfpException(
                $"ConvertStringSecurityDescriptorToSecurityDescriptorW({serviceName})", unchecked((uint)Marshal.GetHRForLastWin32Error()));
        }
        _localAlloc.Add(descriptor);

        var blob = Marshal.AllocHGlobal(Marshal.SizeOf<FWP_BYTE_BLOB>());
        _hGlobal.Add(blob);
        Marshal.StructureToPtr(new FWP_BYTE_BLOB { size = size, data = descriptor }, blob, false);

        return new FWPM_FILTER_CONDITION0
        {
            fieldKey = FWPM_CONDITION_ALE_USER_ID,
            matchType = FwpMatchType.Equal,
            conditionValue = new FWP_VALUE0 { type = FwpDataType.SecurityDescriptor, value = blob }
        };
    }

    /// <summary>Matches a remote address range: an IPv4 address and mask, or an IPv6 prefix.</summary>
    public FWPM_FILTER_CONDITION0 RemoteRange(System.Net.IPAddress network, int prefixLength)
    {
        var bytes = network.GetAddressBytes();
        IntPtr buffer;
        FwpDataType type;
        if (bytes.Length == 4)
        {
            // FWP_V4_ADDR_AND_MASK: address, then mask, both host-order UINT32.
            buffer = Marshal.AllocHGlobal(8);
            var address = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
            var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
            Marshal.WriteInt32(buffer, 0, unchecked((int)address));
            Marshal.WriteInt32(buffer, 4, unchecked((int)mask));
            type = FwpDataType.V4AddrMask;
        }
        else
        {
            // FWP_V6_ADDR_AND_MASK: 16 address bytes, then the prefix length.
            buffer = Marshal.AllocHGlobal(17);
            Marshal.Copy(bytes, 0, buffer, 16);
            Marshal.WriteByte(buffer, 16, (byte)prefixLength);
            type = FwpDataType.V6AddrMask;
        }
        _hGlobal.Add(buffer);

        return new FWPM_FILTER_CONDITION0
        {
            fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS,
            matchType = FwpMatchType.Equal,
            conditionValue = new FWP_VALUE0 { type = type, value = buffer }
        };
    }

    /// <summary>Matches traffic leaving via a specific interface, identified by LUID.</summary>
    public FWPM_FILTER_CONDITION0 LocalInterface(ulong luid)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(ulong));
        _hGlobal.Add(buffer);
        Marshal.WriteInt64(buffer, unchecked((long)luid));

        return new FWPM_FILTER_CONDITION0
        {
            fieldKey = FWPM_CONDITION_IP_LOCAL_INTERFACE,
            matchType = FwpMatchType.Equal,
            conditionValue = new FWP_VALUE0 { type = FwpDataType.Uint64, value = buffer }
        };
    }

    /// <summary>Matches a transport protocol. UINT8 values are stored inline, not by pointer.</summary>
    public FWPM_FILTER_CONDITION0 Protocol(uint protocol) => new()
    {
        fieldKey = FWPM_CONDITION_IP_PROTOCOL,
        matchType = FwpMatchType.Equal,
        conditionValue = new FWP_VALUE0 { type = FwpDataType.Uint8, value = (IntPtr)protocol }
    };

    /// <summary>Copies conditions into contiguous unmanaged memory for the filter struct.</summary>
    public IntPtr MarshalConditions(IReadOnlyList<FWPM_FILTER_CONDITION0> conditions)
    {
        if (conditions.Count == 0)
        {
            return IntPtr.Zero;
        }

        var elementSize = Marshal.SizeOf<FWPM_FILTER_CONDITION0>();
        var block = Marshal.AllocHGlobal(elementSize * conditions.Count);
        _hGlobal.Add(block);

        for (var i = 0; i < conditions.Count; i++)
        {
            Marshal.StructureToPtr(conditions[i], block + (i * elementSize), false);
        }

        return block;
    }

    public void Dispose()
    {
        foreach (var p in _hGlobal)
        {
            Marshal.FreeHGlobal(p);
        }
        _hGlobal.Clear();

        foreach (var p in _wfpAllocated)
        {
            var copy = p;
            FwpmFreeMemory0(ref copy);
        }
        _wfpAllocated.Clear();

        foreach (var p in _sids)
        {
            FreeSid(p);
        }
        _sids.Clear();

        foreach (var p in _localAlloc)
        {
            LocalFree(p);
        }
        _localAlloc.Clear();
    }
}
