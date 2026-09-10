using NetRoute.Core.Adapters;
using NetRoute.Core.Config;

namespace NetRoute.Core.Policy;

/// <summary>
/// What NetRoute asks of the split-tunnel driver and of Windows' routing table.
///
/// <para>The driver moves a set of processes ONTO one adapter and keeps them off another,
/// and child processes inherit membership of that set. If the set were Downloads apps,
/// every game Steam launches would be dragged onto Downloads with it (§36, §37). So the
/// mapping is inverted:</para>
/// <list type="bullet">
/// <item>the Downloads adapter becomes Windows' default route, where launchers, browsers and
/// updates go without being told;</item>
/// <item>apps assigned to Gaming are the split set, moved onto the Gaming adapter.</item>
/// </list>
/// <para>A game Steam launches is split by its own program file, not by Steam's.</para>
/// </summary>
public sealed record RedirectPlan
{
    /// <summary>Make <see cref="Downloads"/> win Windows' IPv4 default route over <see cref="Gaming"/>.</summary>
    public required bool ManageDefaultRoute { get; init; }

    /// <summary>Engage the driver for <see cref="SplitRules"/>.</summary>
    public required bool Split { get; init; }

    public NetworkAdapter? Downloads { get; init; }
    public NetworkAdapter? Gaming { get; init; }
    public IReadOnlyList<AppRule> SplitRules { get; init; } = [];

    /// <summary>Why apps are not being moved, in words for the user. Null while they are.</summary>
    public string? Reason { get; init; }

    public string Fingerprint => string.Join('|',
        ManageDefaultRoute, Split,
        Downloads?.Luid, Downloads?.InterfaceIndex, Downloads?.Ipv4Address, Downloads?.Ipv6Address,
        Gaming?.Luid, Gaming?.InterfaceIndex, Gaming?.Ipv4Address, Gaming?.Ipv6Address,
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

        var gaming = Find(config.BindingFor(RoleId.Gaming), adapters);
        var downloads = Find(config.BindingFor(RoleId.Downloads), adapters);
        if (gaming is null || downloads is null)
        {
            return Off("The Gaming or Downloads network isn't present.");
        }
        if (gaming.Luid == downloads.Luid)
        {
            return Off("Gaming and Downloads are the same connection.");
        }

        // From here the routing preference holds even while one side is briefly offline, so
        // an unplugged cable doesn't flip Windows' default connection back and forth.
        var baseline = new RedirectPlan { ManageDefaultRoute = true, Split = false, Downloads = downloads, Gaming = gaming };

        if (downloads.State != AdapterState.Connected || downloads.Ipv4Address is null)
        {
            return baseline with { Reason = $"{downloads.Name} isn't connected." };
        }
        if (gaming.State != AdapterState.Connected || gaming.Ipv4Address is null)
        {
            // Strict Gaming apps are held by the kill switch's WFP filters meanwhile (§39).
            return baseline with { Reason = $"{gaming.Name} isn't connected." };
        }

        var rules = plan.Applications
            .Where(a => a.Rule.Role == RoleId.Gaming && a.Action == EnforcementAction.PinToAdapter)
            .Select(a => a.Rule)
            .ToList();

        return rules.Count == 0
            ? baseline with { Reason = "No apps are assigned to Gaming yet." }
            : baseline with { Split = true, SplitRules = rules };
    }

    private static RedirectPlan Off(string reason) => new() { ManageDefaultRoute = false, Split = false, Reason = reason };

    private static NetworkAdapter? Find(RoleBinding? binding, IReadOnlyList<NetworkAdapter> adapters)
        => binding is null ? null : adapters.FirstOrDefault(a => a.Luid == binding.AdapterLuid);
}
