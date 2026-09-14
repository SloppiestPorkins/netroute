using NetRoute.Core.Interop;

namespace NetRoute.Core.Adapters;

/// <summary>One IPv4 default route (0.0.0.0/0) and its route metric.</summary>
public sealed record DefaultRoute(ulong Luid, uint InterfaceIndex, uint RouteMetric);

public interface IDefaultRouteSource
{
    IReadOnlyList<DefaultRoute> ReadIpv4();
}

/// <summary>Windows' routing table, via GetIpForwardTable2. Needs no privileges and is cheap enough to read every refresh.</summary>
public sealed class DefaultRouteTable : IDefaultRouteSource
{
    public IReadOnlyList<DefaultRoute> ReadIpv4()
        => IpHelper.ReadIpv4DefaultRoutes().Select(r => new DefaultRoute(r.Luid, r.InterfaceIndex, r.Metric)).ToList();
}

/// <summary>Two or more connections that Windows ranks exactly equal for internet traffic.</summary>
public sealed record RouteTie(IReadOnlyList<NetworkAdapter> Adapters, long Cost)
{
    public string Names => Adapters.Count == 2
        ? $"{Adapters[0].Name} and {Adapters[1].Name}"
        : string.Join(", ", Adapters.Take(Adapters.Count - 1).Select(a => a.Name)) + " and " + Adapters[^1].Name;
}

public static class RouteTies
{
    /// <summary>
    /// Finds connections tied for Windows' default route.
    ///
    /// <para>Windows ranks default routes by route metric plus interface metric. When the
    /// lowest total is shared, it has no single default connection and spreads new
    /// connections across the tied ones, so downloads end up on both networks at once.
    /// NetRoute removes the tie itself while it manages the default route. This catches the
    /// times it doesn't: paused, turned off, or a tie left behind by other settings.</para>
    /// </summary>
    public static RouteTie? Find(IReadOnlyList<NetworkAdapter> adapters, IReadOnlyList<DefaultRoute> routes)
    {
        var costs = adapters
            .Where(a => a.State == AdapterState.Connected && a.HasIpv4Gateway)
            .Select(a => (Adapter: a, Routes: routes.Where(r => r.Luid == a.Luid).ToList()))
            .Where(x => x.Routes.Count > 0)
            .Select(x => (x.Adapter, Cost: x.Routes.Min(r => (long)r.RouteMetric) + x.Adapter.Ipv4Metric))
            .ToList();
        if (costs.Count < 2)
        {
            return null;
        }

        var best = costs.Min(c => c.Cost);
        var tied = costs.Where(c => c.Cost == best).Select(c => c.Adapter).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return tied.Count >= 2 ? new RouteTie(tied, best) : null;
    }
}
