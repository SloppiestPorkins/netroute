using System.Net.Sockets;
using System.Runtime.InteropServices;
using NetRoute.Core.Adapters;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Windows.Split;
using static NetRoute.Windows.Wfp.WfpNative;

namespace NetRoute.Windows.Wfp;

/// <summary>
/// Translates an <see cref="EnforcementPlan"/> into WFP filters.
///
/// <para>The enforcement shape for a pinned application is a permit and a block, both
/// scoped to that application: permit it on the role's interface, block it everywhere
/// else. The block is what makes the policy real — without it, an application would
/// simply follow the Windows routing table whenever the permit did not apply, which
/// is the silent fallback §15 forbids.</para>
/// </summary>
public sealed class WfpEnforcer
{
    // Relative weights within NetRoute's sublayer. Loopback outranks everything so
    // local IPC survives; the catch-all block sits below the permits by construction.
    private const byte WeightLoopbackPermit = 14;
    private const byte WeightLocalNetworkPermit = 13;
    private const byte WeightRolePermit = 12;
    private const byte WeightCatchAllBlock = 8;

    private readonly WfpSession _session;
    private readonly List<ulong> _installedFilters = [];
    private readonly ulong? _loopbackLuid;

    public WfpEnforcer(WfpSession session, AdapterDiscovery discovery)
    {
        _session = session;
        _loopbackLuid = discovery.DiscoverAll()
            .FirstOrDefault(a => a.Kind == AdapterKind.Loopback)?.Luid;
    }

    public int InstalledFilterCount => _installedFilters.Count;

    /// <summary>
    /// Replaces all NetRoute filters with those described by <paramref name="plan"/>.
    ///
    /// <para>Done as one transaction so policy never exists half-applied, which also
    /// gives §19 its behaviour: changing an application's role takes effect for new
    /// connections immediately, without restarting NetRoute.</para>
    /// </summary>
    public EnforcementResult Apply(EnforcementPlan plan)
    {
        var applied = new List<AppliedRule>();
        var failed = new List<FailedRule>();
        WfpException? systemError = null;

        _session.InTransaction(() =>
        {
            RemoveAllFilters();

            foreach (var app in plan.Applications)
            {
                try
                {
                    var count = ApplyApplication(app);
                    if (count > 0)
                    {
                        applied.Add(new AppliedRule(app, count));
                    }
                }
                catch (WfpException ex)
                {
                    // One unenforceable application must not cost every other application
                    // its protection, so this is collected and reported rather than thrown.
                    failed.Add(new FailedRule(app, ex));
                }
            }

            if (plan.SystemDownloads is { } system)
            {
                try
                {
                    ApplySystemDownloads(system);
                }
                catch (WfpException ex)
                {
                    systemError = ex;
                }
            }

            if (_installedFilters.Count > 0)
            {
                try
                {
                    ApplyAlwaysPermits();
                }
                catch (WfpException)
                {
                    // Local traffic then simply follows each app's rule, as it used to.
                }
            }
        });

        return new EnforcementResult(applied, failed, systemError);
    }

    private static readonly Guid[] V4Layers = [FWPM_LAYER_ALE_AUTH_CONNECT_V4, FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4];
    private static readonly Guid[] V6Layers = [FWPM_LAYER_ALE_AUTH_CONNECT_V6, FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6];

    /// <summary>
    /// Keeps Windows' download services on Downloads: the same permit-there, block-elsewhere
    /// shape as an app, keyed on each service's SID (see <see cref="SystemDownloadsPlan"/>).
    /// </summary>
    private void ApplySystemDownloads(SystemDownloadsPlan plan)
    {
        using var scope = new ConditionScope();
        foreach (var service in plan.Services)
        {
            var identity = scope.ServiceUser(service);
            var label = $"Windows downloads ({service})";
            foreach (var layer in V4Layers)
            {
                AddServicePinned(scope, layer, identity, plan.Adapter.Luid, label);
            }
            foreach (var layer in V6Layers)
            {
                if (plan.BlockIpv6)
                {
                    AddFilter(scope, layer, [identity], FWP_ACTION_BLOCK, WeightCatchAllBlock, $"{label}: IPv6 blocked, Downloads is IPv4-only");
                }
                else
                {
                    AddServicePinned(scope, layer, identity, plan.Adapter.Luid, label);
                }
            }
        }
    }

    /// <summary>
    /// One set of permits for local-network destinations (see <see cref="LocalNetwork"/>), for
    /// every app. A TV, a NAS or Steam Remote Play on the Ethernet LAN is reachable only through
    /// Ethernet and isn't internet traffic, so a Downloads rule mustn't cut it off. They outrank
    /// NetRoute's own blocks, but live in NetRoute's sublayer, so Windows Firewall still decides
    /// independently: nothing here allows anything Windows Firewall blocks.
    /// </summary>
    private void ApplyAlwaysPermits()
    {
        using var scope = new ConditionScope();
        foreach (var layer in V4Layers.Concat(V6Layers))
        {
            // Loopback, including the AppContainer loopback a Store app uses to talk to itself.
            AddFilter(scope, layer, [scope.LoopbackFlags()], FWP_ACTION_PERMIT, WeightLoopbackPermit, "Loopback");

            var v6 = V6Layers.Contains(layer);
            foreach (var (network, prefix) in LocalNetwork.Ranges.Where(r => (r.Network.AddressFamily == AddressFamily.InterNetworkV6) == v6))
            {
                AddFilter(scope, layer, [scope.RemoteRange(network, prefix)], FWP_ACTION_PERMIT, WeightLocalNetworkPermit,
                    $"Local network {network}/{prefix}");
            }
        }
    }

    private void AddServicePinned(ConditionScope scope, Guid layer, FWPM_FILTER_CONDITION0 identity, ulong luid, string label)
    {
        AddFilter(scope, layer, [identity, scope.LocalInterface(luid)], FWP_ACTION_PERMIT, WeightRolePermit, $"{label}: permit on Downloads");
        if (_loopbackLuid is { } loopback)
        {
            AddFilter(scope, layer, [identity, scope.LocalInterface(loopback)], FWP_ACTION_PERMIT, WeightLoopbackPermit, $"{label}: permit loopback");
        }
        AddFilter(scope, layer, [identity], FWP_ACTION_BLOCK, WeightCatchAllBlock, $"{label}: block outside Downloads");
    }

    /// <summary>
    /// Removes every NetRoute filter, restoring normal Windows networking (§43).
    /// </summary>
    public void EmergencyDisable()
    {
        _session.InTransaction(RemoveAllFilters);
    }

    private void RemoveAllFilters()
    {
        foreach (var id in _installedFilters)
        {
            // A filter that has already gone is the desired state, not an error.
            var result = FwpmFilterDeleteById0(_session.Handle, id);
            if (result != 0 && result != WfpException.FWP_E_NOT_FOUND)
            {
                throw new WfpException("FwpmFilterDeleteById0", result);
            }
        }
        _installedFilters.Clear();
    }

    private int ApplyApplication(AppEnforcement app)
    {
        if (app.Action is EnforcementAction.None or EnforcementAction.FallBackToWindows)
        {
            return 0;
        }

        using var scope = new ConditionScope();
        var installed = 0;
        foreach (var identity in BuildIdentityConditions(scope, app.Rule.App))
        {
            installed += ApplyIdentity(scope, identity, app);
        }
        return installed;
    }

    /// <summary>The permit-and-block shape for one program file (or package) of an app.</summary>
    private int ApplyIdentity(ConditionScope scope, FWPM_FILTER_CONDITION0 identity, AppEnforcement app)
    {
        var rule = app.Rule;
        var installed = 0;
        var v4Layers = V4Layers;
        var v6Layers = V6Layers;

        if (app.Action == EnforcementAction.BlockAll)
        {
            foreach (var layer in v4Layers.Concat(v6Layers))
            {
                installed += AddBlock(scope, layer, identity, rule, $"{rule.App.DisplayName}: role unavailable");
            }
            return installed;
        }

        var luid = app.ResolvedAdapter!.Luid;

        if (rule.EnforceIpv4)
        {
            foreach (var layer in v4Layers)
            {
                installed += AddPinned(scope, layer, identity, rule, luid);
            }
        }

        if (rule.EnforceIpv6)
        {
            if (app.BlockIpv6)
            {
                // The role's adapter has no IPv6 path. Permitting IPv6 on it would achieve
                // nothing and omitting IPv6 filters entirely would let the traffic escape
                // via the adapter that does have a v6 route. Blocking is the only option
                // that keeps the policy honest (§23).
                foreach (var layer in v6Layers)
                {
                    installed += AddBlock(
                        scope, layer, identity, rule,
                        $"{rule.App.DisplayName}: IPv6 blocked, role network is IPv4-only");
                }
            }
            else
            {
                foreach (var layer in v6Layers)
                {
                    installed += AddPinned(scope, layer, identity, rule, luid);
                }
            }
        }

        return installed;
    }

    /// <summary>Permit on the role's interface (and loopback), block anywhere else.</summary>
    private int AddPinned(
        ConditionScope scope, Guid layer, FWPM_FILTER_CONDITION0 identity, AppRule rule, ulong luid)
    {
        var installed = 0;

        installed += AddFilter(
            scope, layer,
            [identity, scope.LocalInterface(luid), .. ProtocolConditions(scope, rule)],
            FWP_ACTION_PERMIT, WeightRolePermit,
            $"{rule.App.DisplayName}: permit on {rule.Role.DisplayName()}");

        // Without this, blocking "everything except the role interface" would also cut
        // 127.0.0.1. Launchers, overlays and anti-cheat components talk to each other
        // over loopback constantly, so this is the difference between a working policy
        // and an application that appears to be broken.
        if (_loopbackLuid is { } loopback)
        {
            installed += AddFilter(
                scope, layer,
                [identity, scope.LocalInterface(loopback)],
                FWP_ACTION_PERMIT, WeightLoopbackPermit,
                $"{rule.App.DisplayName}: permit loopback");
        }

        installed += AddBlock(
            scope, layer, identity, rule,
            $"{rule.App.DisplayName}: block outside {rule.Role.DisplayName()}");

        return installed;
    }

    private int AddBlock(
        ConditionScope scope, Guid layer, FWPM_FILTER_CONDITION0 identity, AppRule rule, string name)
        => AddFilter(
            scope, layer,
            [identity, .. ProtocolConditions(scope, rule)],
            FWP_ACTION_BLOCK, WeightCatchAllBlock, name);

    /// <summary>
    /// Protocol conditions, but only when the rule actually narrows the protocol.
    /// Adding both TCP and UDP as separate equality conditions would match neither,
    /// since conditions on the same field are ANDed within a filter.
    /// </summary>
    private static List<FWPM_FILTER_CONDITION0> ProtocolConditions(ConditionScope scope, AppRule rule)
    {
        if (rule.EnforceTcp == rule.EnforceUdp)
        {
            return [];
        }

        return [scope.Protocol(rule.EnforceTcp ? IPPROTO_TCP : IPPROTO_UDP)];
    }

    /// <summary>
    /// Every program file a rule covers: the package, or the .exe plus the other programs in
    /// its own folder (see <see cref="SplitImagePaths"/>, which leaves game libraries out). Steam
    /// downloads through steam.exe but shows its store through steamwebhelper.exe, and a rule for
    /// "Steam" has to mean both.
    /// </summary>
    private static List<FWPM_FILTER_CONDITION0> BuildIdentityConditions(ConditionScope scope, AppIdentity app)
    {
        if (app.Kind == AppIdentityKind.Packaged)
        {
            return app.PackageFamilyName is { } pfn
                ? [scope.PackageId(pfn)]
                : throw new WfpException($"Packaged app '{app.DisplayName}' has no package family name", WfpException.FWP_E_INVALID_PARAMETER);
        }
        if (app.ExecutablePath is not { } main)
        {
            throw new WfpException($"App '{app.DisplayName}' has no executable path", WfpException.FWP_E_INVALID_PARAMETER);
        }

        var conditions = new List<FWPM_FILTER_CONDITION0>();
        foreach (var path in SplitImagePaths.For(app).DefaultIfEmpty(main))
        {
            try
            {
                conditions.Add(scope.AppId(path));
            }
            catch (WfpException) when (!string.Equals(path, main, StringComparison.OrdinalIgnoreCase))
            {
                // A helper that disappeared between listing and filtering. The main program still counts.
            }
        }
        return conditions;
    }

    private int AddFilter(
        ConditionScope scope,
        Guid layer,
        List<FWPM_FILTER_CONDITION0> conditions,
        uint action,
        byte weight,
        string name)
    {
        var providerKey = WfpSession.ProviderKey;
        var providerKeyPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());

        try
        {
            Marshal.StructureToPtr(providerKey, providerKeyPtr, false);

            var filter = new FWPM_FILTER0
            {
                filterKey = Guid.NewGuid(),
                displayData = new FWPM_DISPLAY_DATA0 { name = name, description = "NetRoute policy" },
                providerKey = providerKeyPtr,
                layerKey = layer,
                subLayerKey = WfpSession.SubLayerKey,

                // UINT8 weights are stored inline and give 16 relative levels within the
                // sublayer, which is ample here and avoids managing a pinned UINT64.
                weight = new FWP_VALUE0 { type = FwpDataType.Uint8, value = (IntPtr)weight },
                numFilterConditions = (uint)conditions.Count,
                filterCondition = scope.MarshalConditions(conditions),
                action = new FWPM_ACTION0 { type = action }
            };

            var result = FwpmFilterAdd0(_session.Handle, ref filter, IntPtr.Zero, out var id);
            WfpException.ThrowIfFailed($"FwpmFilterAdd0({name})", result);

            _installedFilters.Add(id);
            return 1;
        }
        finally
        {
            Marshal.FreeHGlobal(providerKeyPtr);
        }
    }
}

public sealed record AppliedRule(AppEnforcement Enforcement, int FilterCount);

public sealed record FailedRule(AppEnforcement Enforcement, WfpException Error);

public sealed record EnforcementResult(
    IReadOnlyList<AppliedRule> Applied,
    IReadOnlyList<FailedRule> Failed,
    WfpException? SystemDownloadsError = null)
{
    public int TotalFilters => Applied.Sum(a => a.FilterCount);
    public bool FullySucceeded => Failed.Count == 0;
}
