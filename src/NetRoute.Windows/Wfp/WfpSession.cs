using System.Runtime.InteropServices;
using static NetRoute.Windows.Wfp.WfpNative;

namespace NetRoute.Windows.Wfp;

/// <summary>
/// An open connection to the WFP filter engine, owning one <see cref="WfpIdentity"/>'s provider
/// and sublayer (NetRoute's own unless a proof harness asked for another).
///
/// <para>The session is dynamic: every filter NetRoute installs is torn down by Windows
/// when this handle closes, including on an abnormal exit. See the note on
/// <see cref="WfpNative.FWPM_SESSION_FLAG_DYNAMIC"/> for why that matters.</para>
///
/// <para>Because the provider and sublayer are session-scoped too, only one live session can own a
/// given identity. <see cref="Open"/> refuses with <see cref="AnotherInstanceException"/> when
/// someone else already does, instead of returning a session whose every filter add would fail
/// with FWP_E_WRONG_SESSION.</para>
/// </summary>
public sealed class WfpSession : IDisposable
{
    /// <summary>Identifies every object NetRoute owns, so uninstall can remove ours and nothing else (§44).</summary>
    public static readonly Guid ProviderKey = WfpIdentity.Default.ProviderKey;

    public static readonly Guid SubLayerKey = WfpIdentity.Default.SubLayerKey;

    private IntPtr _engine;
    private bool _disposed;

    /// <summary>The provider, sublayer and session name this session files everything under.</summary>
    public WfpIdentity Identity { get; }

    /// <summary>
    /// How long the last <see cref="InTransaction"/> spent in FwpmTransactionCommit0. The commit is
    /// where the engine takes its lock and swaps the filter set in, so it is the part that could
    /// stall other traffic, and the service logs it to be able to say whether it ever did.
    /// </summary>
    public TimeSpan LastCommitDuration { get; private set; }

    private WfpSession(IntPtr engine, WfpIdentity identity)
    {
        _engine = engine;
        Identity = identity;
    }

    public IntPtr Handle => _engine;

    /// <summary>True when the current process can actually manage WFP filters.</summary>
    public static bool CanOpen(out string? reason)
    {
        try
        {
            using var probe = Open();
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = DescribeOpenFailure(ex);
            return false;
        }
    }

    /// <summary>What to tell the user when <see cref="Open"/> failed: a cause where one is known, not a bare Windows error.</summary>
    public static string DescribeOpenFailure(Exception ex) => ex switch
    {
        AnotherInstanceException => ex.Message,
        WfpException { ErrorCode: WfpException.ERROR_ACCESS_DENIED } => "NetRoute needs administrator rights to manage network policy.",
        WfpException { Operation: "FwpmEngineOpen0", ErrorCode: WfpException.RPC_S_SERVER_UNAVAILABLE or WfpException.EPT_S_NOT_REGISTERED } =>
            "Windows' network filtering service (Base Filtering Engine) isn't ready, so network policy is off. NetRoute will keep trying.",
        _ => ex.Message
    };

    /// <summary>Opens a dynamic session and registers the identity's provider and sublayer.</summary>
    /// <param name="identity">Whose objects to own; <see cref="WfpIdentity.Default"/> unless a proof must coexist with the service.</param>
    /// <exception cref="AnotherInstanceException">Another live session owns this identity's objects.</exception>
    public static WfpSession Open(WfpIdentity? identity = null)
    {
        identity ??= WfpIdentity.Default;
        var session = new FWPM_SESSION0
        {
            displayData = new FWPM_DISPLAY_DATA0
            {
                name = identity.SessionName,
                description = "NetRoute per-application network policy"
            },
            flags = FWPM_SESSION_FLAG_DYNAMIC,
            txnWaitTimeoutInMSec = 0
        };

        var result = FwpmEngineOpen0(null, RPC_C_AUTHN_WINNT, IntPtr.Zero, ref session, out var engine);
        WfpException.ThrowIfFailed("FwpmEngineOpen0", result);

        var opened = new WfpSession(engine, identity);
        try
        {
            bool existed;
            try
            {
                existed = opened.RegisterProviderAndSubLayer();
            }
            catch (WfpException ex) when (ex.ErrorCode == WfpException.FWP_E_WRONG_SESSION)
            {
                existed = true;
            }

            if (existed)
            {
                // The objects were already there. If they are a previous session of ours still going
                // away that is fine; if another live session owns them nothing can be installed.
                SessionClaim.Verify(opened.ProbeOwnership, opened.OtherSessionsOfSameName, Thread.Sleep);
            }
            return opened;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    /// <summary>Registers the provider and sublayer; returns true when either was already there.</summary>
    private bool RegisterProviderAndSubLayer()
    {
        var existed = false;
        InTransaction(() =>
        {
            var provider = new FWPM_PROVIDER0
            {
                providerKey = Identity.ProviderKey,
                displayData = new FWPM_DISPLAY_DATA0
                {
                    name = Identity.SessionName,
                    description = "NetRoute per-application network policy"
                }
            };

            // ALREADY_EXISTS is not an error but it is not "fine" either: it means our own previous
            // session is still tearing down, or another live session owns the object. Open tells
            // the two apart (see SessionClaim).
            var addProvider = FwpmProviderAdd0(_engine, ref provider, IntPtr.Zero);
            if (addProvider == WfpException.FWP_E_ALREADY_EXISTS)
            {
                existed = true;
            }
            else if (addProvider != 0)
            {
                throw new WfpException("FwpmProviderAdd0", addProvider);
            }

            var providerKey = Identity.ProviderKey;
            var providerKeyPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
            try
            {
                Marshal.StructureToPtr(providerKey, providerKeyPtr, false);

                var subLayer = new FWPM_SUBLAYER0
                {
                    subLayerKey = Identity.SubLayerKey,
                    displayData = new FWPM_DISPLAY_DATA0
                    {
                        name = Identity.SessionName,
                        description = "NetRoute application routing policy"
                    },
                    providerKey = providerKeyPtr,

                    // High weight so NetRoute's decisions are evaluated ahead of most
                    // third-party sublayers, but below the Windows built-ins — we are
                    // adding policy, not overriding the firewall (§45).
                    weight = 0x8000
                };

                var addSubLayer = FwpmSubLayerAdd0(_engine, ref subLayer, IntPtr.Zero);
                if (addSubLayer == WfpException.FWP_E_ALREADY_EXISTS)
                {
                    existed = true;
                }
                else if (addSubLayer != 0)
                {
                    throw new WfpException("FwpmSubLayerAdd0", addSubLayer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(providerKeyPtr);
            }
        });
        return existed;
    }

    /// <summary>
    /// Adds a filter that can never match anything into our sublayer, inside a transaction that is
    /// aborted, and returns the engine's answer. Nothing is ever committed.
    /// </summary>
    private uint ProbeOwnership()
    {
        var begin = FwpmTransactionBegin0(_engine, 0);
        if (begin != 0)
        {
            return begin;
        }

        try
        {
            // IP protocol 253 is reserved for experiments, and the permit is never committed: inert twice over.
            var spec = new FilterSpec(FWPM_LAYER_ALE_AUTH_CONNECT_V4, FWP_ACTION_PERMIT, 1,
                "NetRoute ownership probe (never committed)", [new ProtocolCondition(253)]);
            try
            {
                new WfpFilterStore(this).Add(spec);
                return 0;
            }
            catch (WfpException ex)
            {
                return ex.ErrorCode;
            }
        }
        finally
        {
            FwpmTransactionAbort0(_engine);
        }
    }

    /// <summary>Sessions carrying this identity's name that belong to other processes.</summary>
    private IReadOnlyList<OtherSession> OtherSessionsOfSameName()
    {
        var result = new List<OtherSession>();
        WfpException.ThrowIfFailed("FwpmSessionCreateEnumHandle0", FwpmSessionCreateEnumHandle0(_engine, IntPtr.Zero, out var handle));
        try
        {
            while (true)
            {
                WfpException.ThrowIfFailed("FwpmSessionEnum0", FwpmSessionEnum0(_engine, handle, 100, out var entries, out var returned));
                try
                {
                    for (var i = 0; i < returned; i++)
                    {
                        var entry = Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                        var info = Marshal.PtrToStructure<FWPM_SESSION0>(entry);
                        if (info.kernelMode == 0
                            && info.processId != (uint)Environment.ProcessId
                            && string.Equals(info.displayData.name, Identity.SessionName, StringComparison.Ordinal))
                        {
                            result.Add(new OtherSession(info.processId, ProcessNameOf(info.processId)));
                        }
                    }
                }
                finally
                {
                    if (entries != IntPtr.Zero)
                    {
                        FwpmFreeMemory0(ref entries);
                    }
                }

                if (returned < 100)
                {
                    break;
                }
            }
        }
        finally
        {
            FwpmSessionDestroyEnumHandle0(_engine, handle);
        }
        return result;
    }

    private static string? ProcessNameOf(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The engine ids of every filter filed under <paramref name="providerKey"/>, whichever session
    /// installed it. Each result is re-checked against the key, so a template the engine reads more
    /// loosely than expected cannot hand back anyone else's filters.
    /// </summary>
    public IReadOnlyList<ulong> EnumerateFilterIds(Guid providerKey)
    {
        var ids = new List<ulong>();
        var keyPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(providerKey, keyPtr, false);
            var template = new FWPM_FILTER_ENUM_TEMPLATE0
            {
                providerKey = keyPtr,
                enumType = FWP_FILTER_ENUM_OVERLAPPING,
                actionMask = 0xFFFFFFFF
            };
            WfpException.ThrowIfFailed("FwpmFilterCreateEnumHandle0", FwpmFilterCreateEnumHandle0(_engine, ref template, out var handle));
            try
            {
                while (true)
                {
                    WfpException.ThrowIfFailed("FwpmFilterEnum0", FwpmFilterEnum0(_engine, handle, 200, out var entries, out var returned));
                    try
                    {
                        for (var i = 0; i < returned; i++)
                        {
                            var entry = Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                            var filter = Marshal.PtrToStructure<FWPM_FILTER0>(entry);
                            if (filter.providerKey != IntPtr.Zero && Marshal.PtrToStructure<Guid>(filter.providerKey) == providerKey)
                            {
                                ids.Add(filter.filterId);
                            }
                        }
                    }
                    finally
                    {
                        if (entries != IntPtr.Zero)
                        {
                            FwpmFreeMemory0(ref entries);
                        }
                    }

                    if (returned < 200)
                    {
                        break;
                    }
                }
            }
            finally
            {
                FwpmFilterDestroyEnumHandle0(_engine, handle);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(keyPtr);
        }
        return ids;
    }

    /// <summary>
    /// Runs <paramref name="action"/> inside a WFP transaction, so a batch of filter
    /// changes either all apply or none do. A partially applied policy is worse than
    /// no policy: it can leave an application permitted on one family and unfiltered
    /// on the other, which is exactly the leak the product exists to prevent.
    /// </summary>
    public void InTransaction(Action action)
    {
        WfpException.ThrowIfFailed("FwpmTransactionBegin0", FwpmTransactionBegin0(_engine, 0));

        try
        {
            action();
        }
        catch
        {
            FwpmTransactionAbort0(_engine);
            throw;
        }

        var commit = System.Diagnostics.Stopwatch.StartNew();
        var committed = FwpmTransactionCommit0(_engine);
        LastCommitDuration = commit.Elapsed;
        WfpException.ThrowIfFailed("FwpmTransactionCommit0", committed);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_engine != IntPtr.Zero)
        {
            FwpmEngineClose0(_engine);
            _engine = IntPtr.Zero;
        }
    }
}
