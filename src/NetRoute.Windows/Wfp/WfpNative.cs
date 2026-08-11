using System.Runtime.InteropServices;

namespace NetRoute.Windows.Wfp;

/// <summary>
/// P/Invoke surface for the Windows Filtering Platform management API (fwpuclnt.dll).
///
/// <para>Struct layouts mirror fwptypes.h / fwpmtypes.h for x64. They are declared only
/// as far as NetRoute reads or writes; trailing members that are never touched are
/// omitted rather than mirrored, since an unread field cannot be got wrong but a
/// wrongly-padded one silently corrupts everything after it.</para>
/// </summary>
internal static class WfpNative
{
    private const string Fwpuclnt = "fwpuclnt.dll";

    // ---- Well-known GUIDs (fwpmu.h) ----

    internal static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V4 =
        new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    internal static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V6 =
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    internal static readonly Guid FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4 =
        new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    internal static readonly Guid FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6 =
        new("a3b42c97-9f04-4672-b87e-cee9c483257f");

    internal static readonly Guid FWPM_CONDITION_ALE_APP_ID =
        new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    internal static readonly Guid FWPM_CONDITION_ALE_PACKAGE_ID =
        new("71bc78fa-f17c-4997-a602-6abb261f351c");
    internal static readonly Guid FWPM_CONDITION_IP_LOCAL_INTERFACE =
        new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
    internal static readonly Guid FWPM_CONDITION_IP_PROTOCOL =
        new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");

    // ---- Enumerations ----

    internal enum FwpDataType : uint
    {
        Empty = 0,
        Uint8 = 1,
        Uint16 = 2,
        Uint32 = 3,
        Uint64 = 4,
        ByteBlob = 12,
        Sid = 13
    }

    internal enum FwpMatchType : uint
    {
        Equal = 0,
        NotEqual = 5
    }

    internal const uint FWP_ACTION_FLAG_TERMINATING = 0x00001000;
    internal const uint FWP_ACTION_BLOCK = 0x00000001 | FWP_ACTION_FLAG_TERMINATING;
    internal const uint FWP_ACTION_PERMIT = 0x00000002 | FWP_ACTION_FLAG_TERMINATING;

    /// <summary>
    /// Filters live only as long as the engine session.
    ///
    /// <para>Deliberate for the MVP: if the service crashes or is killed, every NetRoute
    /// filter disappears and normal networking returns by itself. That makes §43's
    /// emergency disable true even in the case where NetRoute is the thing that is
    /// broken. The cost is no protection across a reboot before the service starts,
    /// which is a trade worth revisiting only once the service is hardened.</para>
    /// </summary>
    internal const uint FWPM_SESSION_FLAG_DYNAMIC = 0x00000001;

    internal const uint IPPROTO_TCP = 6;
    internal const uint IPPROTO_UDP = 17;

    internal const uint RPC_C_AUTHN_WINNT = 10;

    // ---- Structures ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_DISPLAY_DATA0
    {
        [MarshalAs(UnmanagedType.LPWStr)] internal string? name;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? description;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_BYTE_BLOB
    {
        internal uint size;
        internal IntPtr data;
    }

    /// <summary>
    /// FWP_VALUE0 / FWP_CONDITION_VALUE0.
    ///
    /// <para>The union is pointer-sized. Small integer types are stored inline; blob and
    /// UINT64 values are stored as a pointer to the real value, which is why callers
    /// have to keep that memory alive for the duration of the filter add.</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FWP_VALUE0
    {
        internal FwpDataType type;
        private readonly uint _padding;
        internal IntPtr value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_FILTER_CONDITION0
    {
        internal Guid fieldKey;
        internal FwpMatchType matchType;
        private readonly uint _padding;
        internal FWP_VALUE0 conditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_ACTION0
    {
        internal uint type;
        internal Guid filterOrCalloutKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_SESSION0
    {
        internal Guid sessionKey;
        internal FWPM_DISPLAY_DATA0 displayData;
        internal uint flags;
        internal uint txnWaitTimeoutInMSec;
        internal uint processId;
        internal IntPtr sid;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? username;
        internal int kernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_PROVIDER0
    {
        internal Guid providerKey;
        internal FWPM_DISPLAY_DATA0 displayData;
        internal uint flags;
        internal FWP_BYTE_BLOB providerData;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? serviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_SUBLAYER0
    {
        internal Guid subLayerKey;
        internal FWPM_DISPLAY_DATA0 displayData;
        internal uint flags;
        internal IntPtr providerKey;
        internal FWP_BYTE_BLOB providerData;
        internal ushort weight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FWPM_FILTER0
    {
        internal Guid filterKey;
        internal FWPM_DISPLAY_DATA0 displayData;
        internal uint flags;
        internal IntPtr providerKey;
        internal FWP_BYTE_BLOB providerData;
        internal Guid layerKey;
        internal Guid subLayerKey;
        internal FWP_VALUE0 weight;
        internal uint numFilterConditions;
        private readonly uint _padding;
        internal IntPtr filterCondition;
        internal FWPM_ACTION0 action;

        // Union of { UINT64 rawContext; GUID providerContextKey; } — 16 bytes, 8-aligned.
        private readonly ulong _context0;
        private readonly ulong _context1;

        internal IntPtr reserved;
        internal ulong filterId;
        internal FWP_VALUE0 effectiveWeight;
    }

    // ---- Engine session ----

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmEngineOpen0(
        [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
        uint authnService,
        IntPtr authIdentity,
        ref FWPM_SESSION0 session,
        out IntPtr engineHandle);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmEngineClose0(IntPtr engineHandle);

    // ---- Transactions ----

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

    // ---- Objects ----

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmProviderAdd0(IntPtr engineHandle, ref FWPM_PROVIDER0 provider, IntPtr sd);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmProviderDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmFilterAdd0(
        IntPtr engineHandle, ref FWPM_FILTER0 filter, IntPtr sd, out ulong id);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

    // ---- Helpers ----

    /// <summary>
    /// Produces the normalised application identifier WFP matches against
    /// FWPM_CONDITION_ALE_APP_ID. Must be freed with <see cref="FwpmFreeMemory0"/>.
    /// </summary>
    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern uint FwpmGetAppIdFromFileName0(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName, out IntPtr appId);

    [DllImport(Fwpuclnt, ExactSpelling = true)]
    internal static extern void FwpmFreeMemory0(ref IntPtr p);
}
