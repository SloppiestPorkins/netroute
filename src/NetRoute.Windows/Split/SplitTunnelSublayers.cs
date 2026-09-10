using System.Runtime.InteropServices;
using NetRoute.Windows.Wfp;
using static NetRoute.Windows.Wfp.WfpNative;

namespace NetRoute.Windows.Split;

/// <summary>
/// The two WFP sublayers the split-tunnel driver files its filters under.
///
/// <para>The driver does not create sublayers. Its Initialize call takes the keys of a
/// "baseline" and a "DNS" sublayer that must already exist (Mullvad passes its own
/// firewall's). NetRoute creates them here.</para>
///
/// <para>They are persistent on purpose. The driver's filters live in the driver's own
/// kernel session and keep running if NetRoute's service exits. Sublayers owned by
/// NetRoute's dynamic session would disappear from under them. They are removed only by
/// <see cref="Remove"/>, which uninstall calls after the driver has been reset (§44).</para>
///
/// <para>The weights are deliberately low. The driver adds hard permits for split apps,
/// and a hard permit in a high-priority sublayer can override blocks from lower-priority
/// sublayers such as the Windows Firewall's. Ranking below them keeps NetRoute from
/// weakening the firewall (§45).</para>
/// </summary>
public static class SplitTunnelSublayers
{
    public static readonly Guid ProviderKey = new("2D6F4B8E-91C3-4A57-B0E2-5C8A13F7D946");
    public static readonly Guid Baseline = new("8A41C2E7-6D3B-4F90-9E15-B27C4D0A8F63");
    public static readonly Guid Dns = new("C5E90B34-1F7A-42D8-A6C3-9D2E58B17F04");

    private const uint FWPM_PROVIDER_FLAG_PERSISTENT = 0x00000001;
    private const uint FWPM_SUBLAYER_FLAG_PERSISTENT = 0x00000001;
    private const ushort BaselineWeight = 0x0200;
    private const ushort DnsWeight = 0x0100;

    /// <summary>Creates the provider and both sublayers if missing. Idempotent. Requires administrator rights.</summary>
    public static void EnsureCreated()
    {
        using var engine = OpenPersistentSession();
        InTransaction(engine.Handle, () =>
        {
            var provider = new FWPM_PROVIDER0
            {
                providerKey = ProviderKey,
                displayData = new FWPM_DISPLAY_DATA0 { name = "NetRoute split tunnel", description = "Sublayers for the split-tunnel driver" },
                flags = FWPM_PROVIDER_FLAG_PERSISTENT
            };
            Tolerate("FwpmProviderAdd0", FwpmProviderAdd0(engine.Handle, ref provider, IntPtr.Zero));

            AddSublayer(engine.Handle, Baseline, "NetRoute split tunnel baseline", BaselineWeight);
            AddSublayer(engine.Handle, Dns, "NetRoute split tunnel DNS", DnsWeight);
        });
    }

    /// <summary>Deletes both sublayers and the provider. Call only after the driver has been reset.</summary>
    public static void Remove()
    {
        using var engine = OpenPersistentSession();
        InTransaction(engine.Handle, () =>
        {
            foreach (var key in new[] { Baseline, Dns })
            {
                var k = key;
                var result = FwpmSubLayerDeleteByKey0(engine.Handle, ref k);
                if (result != 0 && result != WfpException.FWP_E_NOT_FOUND)
                {
                    throw new WfpException("FwpmSubLayerDeleteByKey0", result);
                }
            }
            var p = ProviderKey;
            var removed = FwpmProviderDeleteByKey0(engine.Handle, ref p);
            if (removed != 0 && removed != WfpException.FWP_E_NOT_FOUND)
            {
                throw new WfpException("FwpmProviderDeleteByKey0", removed);
            }
        });
    }

    private static void AddSublayer(IntPtr engine, Guid key, string name, ushort weight)
    {
        var providerKey = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(ProviderKey, providerKey, false);
            var sublayer = new FWPM_SUBLAYER0
            {
                subLayerKey = key,
                displayData = new FWPM_DISPLAY_DATA0 { name = name, description = "Used by the split-tunnel driver on NetRoute's behalf" },
                flags = FWPM_SUBLAYER_FLAG_PERSISTENT,
                providerKey = providerKey,
                weight = weight
            };
            Tolerate("FwpmSubLayerAdd0", FwpmSubLayerAdd0(engine, ref sublayer, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(providerKey);
        }
    }

    private static void Tolerate(string operation, uint result)
    {
        if (result != 0 && result != WfpException.FWP_E_ALREADY_EXISTS)
        {
            throw new WfpException(operation, result);
        }
    }

    private static void InTransaction(IntPtr engine, Action action)
    {
        WfpException.ThrowIfFailed("FwpmTransactionBegin0", FwpmTransactionBegin0(engine, 0));
        try
        {
            action();
        }
        catch
        {
            FwpmTransactionAbort0(engine);
            throw;
        }
        WfpException.ThrowIfFailed("FwpmTransactionCommit0", FwpmTransactionCommit0(engine));
    }

    /// <summary>A non-dynamic session: objects created in it outlive the process.</summary>
    private static EngineHandle OpenPersistentSession()
    {
        var session = new FWPM_SESSION0 { displayData = new FWPM_DISPLAY_DATA0 { name = "NetRoute setup" } };
        WfpException.ThrowIfFailed("FwpmEngineOpen0",
            FwpmEngineOpen0(null, RPC_C_AUTHN_WINNT, IntPtr.Zero, ref session, out var handle));
        return new EngineHandle(handle);
    }

    private sealed class EngineHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public void Dispose() => FwpmEngineClose0(Handle);
    }
}
