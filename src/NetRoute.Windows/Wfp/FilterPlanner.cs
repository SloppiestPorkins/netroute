using System.Net.Sockets;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Windows.Split;
using static NetRoute.Windows.Wfp.WfpNative;

namespace NetRoute.Windows.Wfp;

/// <summary>
/// The filters one owner (an app rule, the Windows-downloads set, or the always-on permits) wants
/// installed. <see cref="Error"/> is set instead of filters when the owner cannot be enforced at
/// all, which leaves every other owner untouched.
/// </summary>
internal sealed record FilterGroup(string Owner, IReadOnlyList<FilterSpec> Filters, WfpException? Error = null)
{
    public AppEnforcement? App { get; init; }
    public bool IsSystemDownloads { get; init; }
}

/// <summary>
/// Turns an <see cref="EnforcementPlan"/> into the filters it calls for, as data. Nothing here
/// touches the filter engine or allocates native memory.
///
/// <para>The enforcement shape for a pinned application is a permit and a block, both scoped to
/// that application: permit it on the role's interface, block it everywhere else. The block is
/// what makes the policy real; without it an application would follow the Windows routing table
/// whenever the permit did not apply, which is the silent fallback §15 forbids.</para>
/// </summary>
internal static class FilterPlanner
{
    // Relative weights within NetRoute's sublayer. Loopback outranks everything so
    // local IPC survives; the catch-all block sits below the permits by construction.
    private const byte WeightLoopbackPermit = 14;
    private const byte WeightLocalNetworkPermit = 13;
    private const byte WeightRolePermit = 12;
    private const byte WeightCatchAllBlock = 8;

    public const string AlwaysOwner = "always";
    public const string SystemOwner = "system-downloads";

    private static readonly Guid[] V4Layers = [FWPM_LAYER_ALE_AUTH_CONNECT_V4, FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4];
    private static readonly Guid[] V6Layers = [FWPM_LAYER_ALE_AUTH_CONNECT_V6, FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6];

    public static string OwnerOf(AppRule rule) => $"rule:{rule.Id}";

    /// <param name="collapseProgramFiles">
    /// One filter per layer holding every program file as an <c>ALE_APP_ID</c> condition, instead
    /// of one filter per program file. Only correct if WFP ORs same-field conditions; see
    /// <see cref="WfpEnforcer"/> for how that is established.
    /// </param>
    /// <param name="programFiles">The program files an app covers; <see cref="SplitImagePaths.For"/> unless a test says otherwise.</param>
    /// <param name="fileExists">Whether a helper program is still on disk.</param>
    public static List<FilterGroup> Build(
        EnforcementPlan plan,
        ulong? loopbackLuid,
        bool collapseProgramFiles,
        Func<AppIdentity, IReadOnlyList<string>>? programFiles = null,
        Func<string, bool>? fileExists = null)
    {
        programFiles ??= SplitImagePaths.For;
        fileExists ??= File.Exists;

        var groups = new List<FilterGroup>();
        foreach (var app in plan.Applications)
        {
            var owner = OwnerOf(app.Rule);
            try
            {
                groups.Add(new FilterGroup(owner, ForApplication(app, loopbackLuid, collapseProgramFiles, programFiles, fileExists)) { App = app });
            }
            catch (WfpException ex)
            {
                // One unenforceable application must not cost every other application
                // its protection, so this is collected and reported rather than thrown.
                groups.Add(new FilterGroup(owner, [], ex) { App = app });
            }
        }

        if (plan.SystemDownloads is { } system)
        {
            groups.Add(new FilterGroup(SystemOwner, ForSystemDownloads(system, loopbackLuid)) { IsSystemDownloads = true });
        }

        if (groups.Any(g => g.Filters.Count > 0))
        {
            groups.Add(new FilterGroup(AlwaysOwner, AlwaysPermits()));
        }
        return groups;
    }

    private static List<FilterSpec> ForApplication(
        AppEnforcement app, ulong? loopbackLuid, bool collapse,
        Func<AppIdentity, IReadOnlyList<string>> programFiles, Func<string, bool> fileExists)
    {
        var filters = new List<FilterSpec>();
        if (app.Action is EnforcementAction.None or EnforcementAction.FallBackToWindows)
        {
            return filters;
        }

        foreach (var identity in Identities(app.Rule.App, collapse, programFiles, fileExists))
        {
            AddIdentity(filters, identity, app, loopbackLuid);
        }

        return filters.Distinct().ToList();
    }

    /// <summary>
    /// The identity conditions a rule covers, one array per filter set. Every program file it covers:
    /// the package, or the .exe plus the other programs in its own folder (see
    /// <see cref="SplitImagePaths"/>, which leaves game libraries out). Steam downloads through
    /// steam.exe but shows its store through steamwebhelper.exe, and a rule for "Steam" has to mean both.
    /// </summary>
    private static List<ConditionSpec[]> Identities(
        AppIdentity app, bool collapse,
        Func<AppIdentity, IReadOnlyList<string>> programFiles, Func<string, bool> fileExists)
    {
        if (app.Kind == AppIdentityKind.Packaged)
        {
            return app.PackageFamilyName is { } pfn
                ? [[new PackageCondition(pfn)]]
                : throw new WfpException($"Packaged app '{app.DisplayName}' has no package family name", WfpException.FWP_E_INVALID_PARAMETER);
        }

        List<string> paths;
        if (app.Kind == AppIdentityKind.Folder)
        {
            paths = [.. programFiles(app)];
            if (paths.Count == 0)
            {
                throw new WfpException($"No programs found in '{app.DisplayName}'", WfpException.FWP_E_INVALID_PARAMETER);
            }
        }
        else
        {
            if (app.ExecutablePath is not { } main)
            {
                throw new WfpException($"App '{app.DisplayName}' has no executable path", WfpException.FWP_E_INVALID_PARAMETER);
            }

            // A helper that disappeared between listing and filtering is skipped. The main program still counts.
            paths = programFiles(app)
                .Where(p => string.Equals(p, main, StringComparison.OrdinalIgnoreCase) || fileExists(p))
                .ToList();
            if (paths.Count == 0)
            {
                paths.Add(main);
            }
        }

        // Sorted so the same set of files always describes the same filter, whatever order the folder listed them in.
        paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        return collapse
            ? [[.. paths.Select(p => (ConditionSpec)new AppIdCondition(p))]]
            : paths.Select(p => new ConditionSpec[] { new AppIdCondition(p) }).ToList();
    }

    /// <summary>The permit-and-block shape for one program file (or package, or collapsed set of files) of an app.</summary>
    private static void AddIdentity(List<FilterSpec> filters, ConditionSpec[] identity, AppEnforcement app, ulong? loopbackLuid)
    {
        var rule = app.Rule;
        var name = rule.App.DisplayName;

        if (app.Action == EnforcementAction.BlockAll)
        {
            foreach (var layer in V4Layers.Concat(V6Layers))
            {
                filters.Add(Block(layer, identity, rule, $"{name}: role unavailable"));
            }
            return;
        }

        var luid = app.ResolvedAdapter!.Luid;

        foreach (var layer in V4Layers)
        {
            AddPinned(filters, layer, identity, rule, luid, loopbackLuid);
        }

        if (rule.EnforceIpv6)
        {
            if (app.BlockIpv6)
            {
                // The role's adapter has no IPv6 path. Permitting IPv6 on it would achieve
                // nothing and omitting IPv6 filters entirely would let the traffic escape
                // via the adapter that does have a v6 route. Blocking is the only option
                // that keeps the policy honest (§23).
                foreach (var layer in V6Layers)
                {
                    filters.Add(Block(layer, identity, rule, $"{name}: IPv6 blocked, role network is IPv4-only"));
                }
            }
            else
            {
                foreach (var layer in V6Layers)
                {
                    AddPinned(filters, layer, identity, rule, luid, loopbackLuid);
                }
            }
        }
    }

    /// <summary>Permit on the role's interface (and loopback), block anywhere else.</summary>
    private static void AddPinned(
        List<FilterSpec> filters, Guid layer, ConditionSpec[] identity, AppRule rule, ulong luid, ulong? loopbackLuid)
    {
        var name = rule.App.DisplayName;

        filters.Add(new FilterSpec(layer, FWP_ACTION_PERMIT, WeightRolePermit,
            $"{name}: permit on {rule.Role.DisplayName()}",
            [.. identity, new LocalInterfaceCondition(luid)]));

        // Without this, blocking "everything except the role interface" would also cut
        // 127.0.0.1. Launchers, overlays and anti-cheat components talk to each other
        // over loopback constantly, so this is the difference between a working policy
        // and an application that appears to be broken.
        if (loopbackLuid is { } loopback)
        {
            filters.Add(new FilterSpec(layer, FWP_ACTION_PERMIT, WeightLoopbackPermit,
                $"{name}: permit loopback",
                [.. identity, new LocalInterfaceCondition(loopback)]));
        }

        filters.Add(Block(layer, identity, rule, $"{name}: block outside {rule.Role.DisplayName()}"));
    }

    private static FilterSpec Block(Guid layer, ConditionSpec[] identity, AppRule rule, string name)
        => new(layer, FWP_ACTION_BLOCK, WeightCatchAllBlock, name, identity);

    // A rule has never narrowed the protocol -- there is no UI or CLI for it -- so there was never
    // a protocol condition to add. The per-protocol settings are gone (24 Oct 2026); if one comes
    // back, note that conditions on the same field within one filter are not ANDed, so TCP and UDP
    // as two equality conditions would not mean "either".

    /// <summary>
    /// Keeps Windows' download services on Downloads: the same permit-there, block-elsewhere
    /// shape as an app, keyed on each service's SID (see <see cref="SystemDownloadsPlan"/>).
    /// </summary>
    private static List<FilterSpec> ForSystemDownloads(SystemDownloadsPlan plan, ulong? loopbackLuid)
    {
        var filters = new List<FilterSpec>();
        foreach (var service in plan.Services)
        {
            ConditionSpec[] identity = [new ServiceCondition(service)];
            var label = $"Windows downloads ({service})";
            foreach (var layer in V4Layers)
            {
                AddServicePinned(filters, layer, identity, plan.Adapter.Luid, loopbackLuid, label);
            }
            foreach (var layer in V6Layers)
            {
                if (plan.BlockIpv6)
                {
                    filters.Add(new FilterSpec(layer, FWP_ACTION_BLOCK, WeightCatchAllBlock,
                        $"{label}: IPv6 blocked, Downloads is IPv4-only", identity));
                }
                else
                {
                    AddServicePinned(filters, layer, identity, plan.Adapter.Luid, loopbackLuid, label);
                }
            }
        }
        return filters;
    }

    private static void AddServicePinned(
        List<FilterSpec> filters, Guid layer, ConditionSpec[] identity, ulong luid, ulong? loopbackLuid, string label)
    {
        filters.Add(new FilterSpec(layer, FWP_ACTION_PERMIT, WeightRolePermit, $"{label}: permit on Downloads",
            [.. identity, new LocalInterfaceCondition(luid)]));
        if (loopbackLuid is { } loopback)
        {
            filters.Add(new FilterSpec(layer, FWP_ACTION_PERMIT, WeightLoopbackPermit, $"{label}: permit loopback",
                [.. identity, new LocalInterfaceCondition(loopback)]));
        }
        filters.Add(new FilterSpec(layer, FWP_ACTION_BLOCK, WeightCatchAllBlock, $"{label}: block outside Downloads", identity));
    }

    /// <summary>
    /// One set of permits for local-network destinations (see <see cref="LocalNetwork"/>), for
    /// every app. A TV, a NAS or Steam Remote Play on the Ethernet LAN is reachable only through
    /// Ethernet and isn't internet traffic, so a Downloads rule mustn't cut it off. They outrank
    /// NetRoute's own blocks, but live in NetRoute's sublayer, so Windows Firewall still decides
    /// independently: nothing here allows anything Windows Firewall blocks.
    /// </summary>
    private static List<FilterSpec> AlwaysPermits()
    {
        var filters = new List<FilterSpec>();
        foreach (var layer in V4Layers.Concat(V6Layers))
        {
            // Loopback, including the AppContainer loopback a Store app uses to talk to itself.
            filters.Add(new FilterSpec(layer, FWP_ACTION_PERMIT, WeightLoopbackPermit, "Loopback", [new LoopbackFlagsCondition()]));

            var v6 = V6Layers.Contains(layer);
            foreach (var (network, prefix) in LocalNetwork.Ranges.Where(r => (r.Network.AddressFamily == AddressFamily.InterNetworkV6) == v6))
            {
                filters.Add(new FilterSpec(layer, FWP_ACTION_PERMIT, WeightLocalNetworkPermit,
                    $"Local network {network}/{prefix}", [new RemoteRangeCondition(network, prefix)]));
            }
        }
        return filters;
    }
}
