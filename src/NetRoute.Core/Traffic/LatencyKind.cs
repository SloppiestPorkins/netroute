namespace NetRoute.Core.Traffic;

/// <summary>
/// Where a program's latency figure came from. The two measurements are not the same thing and
/// the GUI says which one it is showing.
/// </summary>
public enum LatencyKind
{
    /// <summary>No figure: nothing measured yet, or the program has nothing to measure.</summary>
    None = 0,

    /// <summary>The program's own TCP connections, timed by Windows' TCP stack (ESTATS). Exact for that traffic.</summary>
    Tcp = 1,

    /// <summary>
    /// An ICMP echo to the remote addresses the program is exchanging UDP with, sent from the
    /// network it uses. It travels the same line, but it is a ping to the peer, not the program's
    /// own packets: the peer may answer ICMP faster or slower than it serves the game.
    /// </summary>
    PeerProbe = 2,

    /// <summary>
    /// Peers were tried and none answered (many game servers drop ICMP), or the probe could not be
    /// sent over the program's network. There is no number, and the GUI says so.
    /// </summary>
    NotMeasurable = 3
}
