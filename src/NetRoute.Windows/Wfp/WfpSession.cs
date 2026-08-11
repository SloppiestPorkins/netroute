using System.Runtime.InteropServices;
using static NetRoute.Windows.Wfp.WfpNative;

namespace NetRoute.Windows.Wfp;

/// <summary>
/// An open connection to the WFP filter engine, owning NetRoute's provider and sublayer.
///
/// <para>The session is dynamic: every filter NetRoute installs is torn down by Windows
/// when this handle closes, including on an abnormal exit. See the note on
/// <see cref="WfpNative.FWPM_SESSION_FLAG_DYNAMIC"/> for why that matters.</para>
/// </summary>
public sealed class WfpSession : IDisposable
{
    /// <summary>Identifies every object NetRoute owns, so uninstall can remove ours and nothing else (§44).</summary>
    public static readonly Guid ProviderKey = new("6E9D1F2A-3B7C-4E58-9A21-D4C8B5E70A13");

    public static readonly Guid SubLayerKey = new("B41C7E90-52A8-4D3F-8C16-7F2E9A4B60D5");

    private IntPtr _engine;
    private bool _disposed;

    private WfpSession(IntPtr engine) => _engine = engine;

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
        catch (WfpException ex) when (ex.ErrorCode == WfpException.ERROR_ACCESS_DENIED)
        {
            reason = "NetRoute needs administrator rights to manage network policy.";
            return false;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    public static WfpSession Open()
    {
        var session = new FWPM_SESSION0
        {
            displayData = new FWPM_DISPLAY_DATA0
            {
                name = "NetRoute",
                description = "NetRoute per-application network policy"
            },
            flags = FWPM_SESSION_FLAG_DYNAMIC,
            txnWaitTimeoutInMSec = 0
        };

        var result = FwpmEngineOpen0(null, RPC_C_AUTHN_WINNT, IntPtr.Zero, ref session, out var engine);
        WfpException.ThrowIfFailed("FwpmEngineOpen0", result);

        var opened = new WfpSession(engine);
        try
        {
            opened.RegisterProviderAndSubLayer();
            return opened;
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    private void RegisterProviderAndSubLayer()
    {
        InTransaction(() =>
        {
            var provider = new FWPM_PROVIDER0
            {
                providerKey = ProviderKey,
                displayData = new FWPM_DISPLAY_DATA0
                {
                    name = "NetRoute",
                    description = "NetRoute per-application network policy"
                }
            };

            // The provider and sublayer are session-scoped like everything else, so
            // ALREADY_EXISTS should not occur — but tolerate it rather than fail
            // startup if a previous session is still tearing down.
            var addProvider = FwpmProviderAdd0(_engine, ref provider, IntPtr.Zero);
            if (addProvider != 0 && addProvider != WfpException.FWP_E_ALREADY_EXISTS)
            {
                throw new WfpException("FwpmProviderAdd0", addProvider);
            }

            var providerKey = ProviderKey;
            var providerKeyPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
            try
            {
                Marshal.StructureToPtr(providerKey, providerKeyPtr, false);

                var subLayer = new FWPM_SUBLAYER0
                {
                    subLayerKey = SubLayerKey,
                    displayData = new FWPM_DISPLAY_DATA0
                    {
                        name = "NetRoute",
                        description = "NetRoute application routing policy"
                    },
                    providerKey = providerKeyPtr,

                    // High weight so NetRoute's decisions are evaluated ahead of most
                    // third-party sublayers, but below the Windows built-ins — we are
                    // adding policy, not overriding the firewall (§45).
                    weight = 0x8000
                };

                var addSubLayer = FwpmSubLayerAdd0(_engine, ref subLayer, IntPtr.Zero);
                if (addSubLayer != 0 && addSubLayer != WfpException.FWP_E_ALREADY_EXISTS)
                {
                    throw new WfpException("FwpmSubLayerAdd0", addSubLayer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(providerKeyPtr);
            }
        });
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

        WfpException.ThrowIfFailed("FwpmTransactionCommit0", FwpmTransactionCommit0(_engine));
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
