using NetRoute.Core.Adapters;

namespace NetRoute.Core.Policy;

/// <summary>
/// Keeps Windows' own download services on the Downloads network.
///
/// <para>Game Pass and Xbox app downloads aren't made by the Xbox app. Gaming Services fetches
/// them, the Store's install service installs them, and Delivery Optimization, Windows Update
/// and BITS do the rest. None of them can be picked as an app, and they run either inside
/// svchost or as a service of their own, so they're matched by their service SIDs instead.</para>
///
/// <para>With the Downloads adapter as Windows' default they already use it for IPv4; the
/// filters make that strict, and block their IPv6 when Downloads has none, which is the path
/// they'd otherwise take onto Gaming (§23). That path is not hypothetical: with Gaming Services
/// missing from this list, a Game Pass install measured here put 1.27 GB down the Gaming line
/// and 763 MB down Downloads in one afternoon, because Microsoft's CDN answers on IPv6 and only
/// the Gaming adapter has any.</para>
/// </summary>
public sealed record SystemDownloadsPlan(NetworkAdapter Adapter, bool BlockIpv6, IReadOnlyList<string> Services)
{
    /// <summary>
    /// Delivery Optimization, BITS, Windows Update, the Store's install service, and the two
    /// Gaming Services that fetch and install Game Pass titles. All are configured with an
    /// unrestricted service SID, so their SID is in the token WFP sees.
    /// </summary>
    public static IReadOnlyList<string> WindowsDownloadServices { get; } =
        ["DoSvc", "BITS", "wuauserv", "InstallService", "GamingServices", "GamingServicesNet"];
}
