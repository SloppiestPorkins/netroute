namespace NetRoute.Core.Traffic;

/// <summary>
/// When the network meter should rebuild its map of local addresses to adapters.
///
/// <para>The rebuild is a full adapter enumeration, which the meter used to do on every one-second
/// reading. Addresses change rarely, and Windows announces it when they do, so the answer is: when
/// notified, when it has been a while, or, rarely, when traffic appears on an address no adapter
/// owns, which is what a DHCP renewal looks like from the packet side.</para>
/// </summary>
public static class AddressRefresh
{
    /// <summary>Longest the map is trusted without a refresh, whatever Windows has or hasn't said.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    /// <summary>Least time between refreshes prompted by a change notice, which can arrive in bursts.</summary>
    public static readonly TimeSpan AfterChange = TimeSpan.FromSeconds(1);

    /// <summary>Least time between refreshes prompted by unexplained traffic.</summary>
    public static readonly TimeSpan AfterUnexplained = TimeSpan.FromSeconds(3);

    public static bool ShouldRefresh(TimeSpan sinceLast, bool changeNotified, bool unattributedTraffic)
        => sinceLast >= MaxAge
           || (changeNotified && sinceLast >= AfterChange)
           || (unattributedTraffic && sinceLast >= AfterUnexplained);
}
