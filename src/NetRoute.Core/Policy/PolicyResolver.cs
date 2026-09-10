using NetRoute.Core.Adapters;
using NetRoute.Core.Config;

namespace NetRoute.Core.Policy;

/// <summary>
/// Turns configuration into an enforcement plan.
///
/// <para>This is where the four concepts in §3 meet: user intent (the rule), logical
/// role, the adapter the role currently resolves to, and the resulting enforcement
/// action. Keeping the resolution here — rather than in the WFP layer — means the
/// "Why?" explanation and the filters are derived from the same reasoning, so the
/// explanation cannot drift away from what is actually enforced.</para>
/// </summary>
public sealed class PolicyResolver
{
    private readonly IAdapterSource _discovery;

    public PolicyResolver(IAdapterSource discovery) => _discovery = discovery;

    public EnforcementPlan Resolve(NetRouteConfig config)
    {
        var adapters = _discovery.DiscoverAll();

        // Resolve each bound role once, so every rule sees a consistent snapshot.
        var resolvedRoles = new Dictionary<RoleId, NetworkAdapter?>();
        foreach (var role in Role.Bindable)
        {
            var binding = config.BindingFor(role);
            resolvedRoles[role] = binding is null ? null : ResolveBinding(binding, adapters);
        }

        // Resolve versioned install folders first, so enforcement always targets the program
        // that actually runs today rather than the one that ran when the rule was added.
        var applications = config.AppRules
            .Select(rule => Resolve(rule with { App = VersionedPaths.Resolve(rule.App) }, config, resolvedRoles))
            .ToList();

        var degraded = resolvedRoles
            .Where(kv => kv.Value is null || kv.Value.State != AdapterState.Connected)
            .Where(kv => config.BindingFor(kv.Key) is not null)
            .Select(kv => kv.Key)
            .ToList();

        return new EnforcementPlan { Applications = applications, DegradedRoles = degraded };
    }

    private static NetworkAdapter? ResolveBinding(RoleBinding binding, IReadOnlyList<NetworkAdapter> adapters)
        => adapters.FirstOrDefault(a => a.Luid == binding.AdapterLuid)
           ?? (binding.AdapterGuid is null
               ? null
               : adapters.FirstOrDefault(a => string.Equals(a.Guid, binding.AdapterGuid, StringComparison.OrdinalIgnoreCase)));

    private AppEnforcement Resolve(
        AppRule rule,
        NetRouteConfig config,
        IReadOnlyDictionary<RoleId, NetworkAdapter?> resolvedRoles)
    {
        var reasons = new List<Reason>();

        if (config.EnforcementPaused)
        {
            reasons.Add(Reason.Bad("NetRoute enforcement is paused globally."));
            return Plan(rule, EnforcementAction.None, null, false, reasons);
        }

        if (rule.Paused)
        {
            reasons.Add(Reason.Bad($"{rule.App.DisplayName} is paused."));
            return Plan(rule, EnforcementAction.None, null, false, reasons);
        }

        if (rule.Role == RoleId.Default || rule.Mode == RoutingMode.Default)
        {
            reasons.Add(Reason.Ok($"{rule.App.DisplayName} is set to Default, so Windows routing applies."));
            reasons.Add(Reason.Ok("NetRoute is deliberately not interfering."));
            return Plan(rule, EnforcementAction.None, null, false, reasons);
        }

        reasons.Add(Reason.Ok($"You assigned {rule.App.DisplayName} to {rule.Role.DisplayName()}."));

        var binding = config.BindingFor(rule.Role);
        if (binding is null)
        {
            reasons.Add(Reason.Bad($"{rule.Role.DisplayName()} has not been pointed at a network yet."));
            return UnavailableRole(rule, null, reasons);
        }

        var adapter = resolvedRoles.GetValueOrDefault(rule.Role);
        if (adapter is null)
        {
            var name = binding.LastKnownName ?? "the selected adapter";
            reasons.Add(Reason.Bad($"{rule.Role.DisplayName()} points at {name}, which is no longer present."));
            return UnavailableRole(rule, null, reasons);
        }

        reasons.Add(Reason.Ok($"{rule.Role.DisplayName()} currently points to {adapter.Name}."));

        if (adapter.State != AdapterState.Connected)
        {
            reasons.Add(Reason.Bad($"{adapter.Name} is {Describe(adapter.State)}."));
            return UnavailableRole(rule, adapter, reasons);
        }

        reasons.Add(Reason.Ok($"{adapter.Name} is connected."));

        if (rule.Mode == RoutingMode.Strict)
        {
            reasons.Add(Reason.Ok("Strict mode is enabled, so no other network may be used."));
        }

        // The IPv6 decision has to be made here rather than left to routing. See §23 and
        // the note on NetworkAdapter.IsIpv4Only.
        var blockIpv6 = rule.EnforceIpv6 && adapter.IsIpv4Only;
        if (blockIpv6)
        {
            reasons.Add(Reason.Bad(
                $"{adapter.Name} has no IPv6 connection, so IPv6 is being blocked to stop it " +
                "leaking out of another network."));
        }
        else if (rule.EnforceIpv6 && adapter.Ipv6Address is not null)
        {
            reasons.Add(Reason.Ok("IPv6 is being enforced on the same network."));
        }

        return Plan(rule, EnforcementAction.PinToAdapter, adapter, blockIpv6, reasons);
    }

    private static AppEnforcement UnavailableRole(AppRule rule, NetworkAdapter? adapter, List<Reason> reasons)
    {
        // Strict never silently degrades onto another network — that is the entire
        // point of the mode, and §15/§39 are explicit that falling back is wrong.
        if (rule.Mode == RoutingMode.Strict && rule.KillSwitch)
        {
            reasons.Add(Reason.Bad("Kill Switch is on, so traffic is blocked rather than sent over another network."));
            return Plan(rule, EnforcementAction.BlockAll, adapter, true, reasons);
        }

        if (rule.Mode == RoutingMode.Strict)
        {
            reasons.Add(Reason.Bad("Strict mode is on but Kill Switch is off, so traffic is blocked while the network is down."));
            return Plan(rule, EnforcementAction.BlockAll, adapter, true, reasons);
        }

        reasons.Add(Reason.Bad("Preferred mode is on, so Windows routing is being allowed to take over."));
        return Plan(rule, EnforcementAction.FallBackToWindows, adapter, false, reasons);
    }

    private static AppEnforcement Plan(
        AppRule rule, EnforcementAction action, NetworkAdapter? adapter, bool blockIpv6, List<Reason> reasons)
        => new()
        {
            Rule = rule,
            Action = action,
            ResolvedAdapter = adapter,
            BlockIpv6 = blockIpv6,
            Reasons = reasons
        };

    private static string Describe(AdapterState state) => state switch
    {
        AdapterState.Disconnected => "offline",
        AdapterState.NoInternet => "connected but has no route to the internet",
        AdapterState.Missing => "missing",
        _ => state.ToString().ToLowerInvariant()
    };
}
