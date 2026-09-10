using System.Net;

namespace NetRoute.Core.Traffic;

public enum TransportProtocol
{
    Other,
    Tcp,
    Udp
}

public enum FlowOutcome
{
    /// <summary>The flow was allowed to use the network.</summary>
    Allowed,

    /// <summary>Windows Filtering Platform refused the flow.</summary>
    Blocked
}

/// <summary>Where an observation came from. The two sources see different things, see below.</summary>
public enum FlowSource
{
    /// <summary>
    /// The TCP/UDP socket tables (GetExtendedTcpTable / GetExtendedUdpTable).
    ///
    /// <para>Cheap and unprivileged, but blind to the case games care about most: a UDP
    /// socket bound to the wildcard address has no fixed local address, so the table
    /// cannot say which adapter its datagrams leave from.</para>
    /// </summary>
    SocketTable,

    /// <summary>
    /// A WFP classify event. Carries the source address Windows actually chose for the
    /// flow, including wildcard-bound UDP, so it is the only way to verify UDP egress.
    /// Needs administrator rights.
    /// </summary>
    WfpEvent
}

/// <summary>
/// One piece of evidence about where an application's traffic actually went.
///
/// <para>This is the raw material for the OBSERVED half of §24. It is deliberately
/// source-neutral so socket-table snapshots and WFP events feed the same verifier and
/// cannot drift into two different definitions of "verified".</para>
/// </summary>
public sealed record ObservedFlow
{
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>
    /// When the underlying socket or flow was created, if known. Lets the verifier tell a
    /// connection opened before a policy change (expected to still be on the old adapter,
    /// §19) from one opened afterwards (which would be a genuine leak).
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }

    public int? ProcessId { get; init; }

    /// <summary>Win32 path of the owning executable, e.g. <c>C:\Steam\steam.exe</c>.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Package family name when the owning process is packaged.</summary>
    public string? PackageFamilyName { get; init; }

    public required TransportProtocol Protocol { get; init; }
    public required IPEndPoint LocalEndPoint { get; init; }
    public IPEndPoint? RemoteEndPoint { get; init; }

    /// <summary>
    /// Interface the flow is attributed to, when it can be determined. Null for
    /// wildcard-bound sockets seen only through the socket table.
    /// </summary>
    public ulong? InterfaceLuid { get; init; }

    public required FlowOutcome Outcome { get; init; }
    public required FlowSource Source { get; init; }
}
