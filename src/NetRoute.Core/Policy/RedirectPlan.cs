using NetRoute.Core.Adapters;
using NetRoute.Core.Config;

namespace NetRoute.Core.Policy;

/// <summary>
/// What NetRoute asks of the split-tunnel driver and of Windows' routing table.
///
/// <para>The driver moves a set of processes ONTO one adapter and keeps them off another,
/// and child processes inherit membership of that set. So the set has to be the apps that
/// are deliberately moved, and the default route has to be the line everything else uses:</para>
/// <list type="bullet">
/// <item>the Gaming (primary) adapter becomes Windows' default route. Everything not in a rule,
/// including launchers, browsers and Discord, goes there without being told;</item>
/// <item>apps assigned to Downloads are the split set, moved onto the Downloads (secondary) adapter.</item>
/// </list>
/// <para>Child processes of a Downloads app inherit the move, so a launcher on Downloads takes its
/// games with it (docs/SPLIT-TUNNEL.md). Keep launchers on the primary line unless you want that.</para>
/// </summary>
public sealed record RedirectPlan
{
    /// <summary>
    /// Make <see cref="DefaultAdapter"/> win Windows' IPv4 default route. Always true while both lines
    /// are present, so an unassigned app never falls back to whichever line Windows happens to pick.
    /// </summary>
    public required bool ManageDefaultRoute { get; init; }

    /// <summary>Engage the driver for <see cref="SplitRules"/>.</summary>
    public required bool Split { get; init; }

    /// <summary>The line everything not assigned to Downloads uses (the primary connection).</summary>
    public NetworkAdapter? DefaultAdapter { get; init; }

    /// <summary>The line the Downloads apps are moved onto (the secondary connection).</summary>
    public NetworkAdapter? SplitAdapter { get; init; }

    public IReadOnlyList<AppRule> SplitRules { get; init; } = [];

    /// <summary>Why apps are not being moved, in words for the user. Null while they are.</summary>
    public string? Reason { get; init; }

    public string Fingerprint => string.Join('|',
        ManageDefaultRoute, Split,
        DefaultAdapter?.Luid, DefaultAdapter?.InterfaceIndex, DefaultAdapter?.Ipv4Address, DefaultAdapter?.Ipv6Address,
        SplitAdapter?.Luid, SplitAdapter?.InterfaceIndex, SplitAdapter?.Ipv4Address, SplitAdapter?.Ipv6Address,
        string.Join(',', SplitRules.OrderBy(r => r.Id).Select(r => $"{r.Id}:{r.App.ExecutablePath}:{r.App.InstallLocation}")));
}

public static class RedirectPlanner
{
    public static RedirectPlan Build(NetRouteConfig config, EnforcementPlan plan, IReadOnlyList<NetworkAdapter> adapters)
    {
        if (!config.SetupCompleted)
        {
            return Off("Setup isn't finished.");
        }
        if (config.EnforcementPaused)
        {
            return Off("Protection is paused.");
        }

        var primary = Find(config.BindingFor(RoleId.Gaming), adapters);
        var secondary = Find(config.BindingFor(RoleId.Downloads), adapters);
        if (primary is null || secondary is null)
        {
            return Off("The primary or Downloads network isn't present.");
        }
        if (primary.Luid == secondary.Luid)
        {
            return Off("The primary and Downloads connections are the same.");
        }

        // From here the routing preference holds even while one side is briefly offline, so
        // an unplugged cable doesn't flip Windows' default connection back and forth.
        var baseline = new RedirectPlan { ManageDefaultRoute = true, Split = false, DefaultAdapter = primary, SplitAdapter = secondary };

        if (secondary.State != AdapterState.Connected || secondary.Ipv4Address is null)
        {
            return baseline with { Reason = $"{secondary.Name} isn't connected." };
        }
        if (primary.State != AdapterState.Connected || primary.Ipv4Address is null)
        {
            // Strict apps are held by their block filters meanwhile (§39).
            return baseline with { Reason = $"{primary.Name} isn't connected." };
        }

        var rules = plan.Applications
            .Where(a => a.Rule.Role == RoleId.Downloads && a.Action == EnforcementAction.PinToAdapter)
            .Select(a => a.Rule)
            .ToList();

        return rules.Count == 0
            ? baseline with { Reason = "No app is on Downloads, so everything uses the primary connection." }
            : baseline with { Split = true, SplitRules = rules };
    }

    private static RedirectPlan Off(string reason) => new() { ManageDefaultRoute = false, Split = false, Reason = reason };

    private static NetworkAdapter? Find(RoleBinding? binding, IReadOnlyList<NetworkAdapter> adapters)
        => binding is null ? null : adapters.FirstOrDefault(a => a.Luid == binding.AdapterLuid);
}
