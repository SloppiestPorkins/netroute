using NetRoute.Core.Adapters;

namespace NetRoute.Core.Policy;

/// <summary>
/// Keeps Windows' own download services on the Downloads network.
///
/// <para>Game Pass and Xbox app downloads aren't made by the Xbox app. Delivery Optimization
/// fetches them, the Store's install service installs them, and Windows Update and BITS do
/// the rest. None of them can be picked as an app, and all of them run inside svchost, so
/// they're matched by their service SIDs instead. With the Downloads adapter as Windows'
/// default they already use it for IPv4; the filters make that strict, and block their IPv6
/// when Downloads has none, which is the path they'd otherwise take onto Gaming (§23).</para>
/// </summary>
public sealed record SystemDownloadsPlan(NetworkAdapter Adapter, bool BlockIpv6, IReadOnlyList<string> Services)
{
    /// <summary>Delivery Optimization, BITS, Windows Update, Microsoft Store install service.</summary>
    public static IReadOnlyList<string> WindowsDownloadServices { get; } = ["DoSvc", "BITS", "wuauserv", "InstallService"];
}
