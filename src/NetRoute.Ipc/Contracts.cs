using NetRoute.Core.Adapters;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;

namespace NetRoute.Ipc;

// ---- Requests ----

public sealed record CompleteSetupRequest(ulong GamingLuid, ulong DownloadsLuid);

public sealed record SetRoleAdapterRequest(RoleId Role, ulong AdapterLuid);

public sealed record AddRuleRequest(AppIdentity App, RoleId Role);

public sealed record UpdateRuleRequest(AppRule Rule);

public sealed record RuleIdRequest(Guid RuleId);

public sealed record SetRulePausedRequest(Guid RuleId, bool Paused);

public sealed record SetPausedRequest(bool Paused);

// ---- Responses ----

/// <summary>A network adapter as the GUI needs it. Identity is the LUID (§7).</summary>
public sealed record AdapterDto
{
    public required ulong Luid { get; init; }
    public required string Guid { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required AdapterKind Kind { get; init; }
    public required AdapterState State { get; init; }
    public required string LinkSpeed { get; init; }
    public string? Ipv4Address { get; init; }
    public string? Ipv6Address { get; init; }
    public required bool HasIpv6Route { get; init; }
    public required IReadOnlyList<string> Gateways { get; init; }
    public required IReadOnlyList<string> DnsServers { get; init; }
    public required uint InterfaceIndex { get; init; }

    /// <summary>Whether this adapter should be offered as a Gaming/Downloads target.</summary>
    public required bool Selectable { get; init; }

    public static AdapterDto From(NetworkAdapter a) => new()
    {
        Luid = a.Luid,
        Guid = a.Guid,
        Name = a.Name,
        Description = a.Description,
        Kind = a.Kind,
        State = a.State,
        LinkSpeed = a.LinkSpeedDisplay,
        Ipv4Address = a.Ipv4Address?.ToString(),
        Ipv6Address = a.Ipv6Address?.ToString(),
        HasIpv6Route = a.HasIpv6Gateway,
        Gateways = a.Gateways.Select(g => g.ToString()).ToList(),
        DnsServers = a.DnsServers.Select(d => d.ToString()).ToList(),
        InterfaceIndex = a.InterfaceIndex,
        Selectable = a.IsSelectableAsRole
    };
}

public enum RoleHealth
{
    /// <summary>The user has not chosen an adapter for this role yet.</summary>
    Unassigned,
    Connected,
    Offline,
    Missing,
    NoInternet
}

/// <summary>One logical role and the adapter behind it (§8).</summary>
public sealed record RoleStatusDto
{
    public required RoleId Role { get; init; }
    public AdapterDto? Adapter { get; init; }

    /// <summary>Adapter name remembered from when it was chosen; shown when the adapter is gone.</summary>
    public string? LastKnownName { get; init; }

    public required RoleHealth Health { get; init; }

    /// <summary>Round-trip latency via this adapter, when measured.</summary>
    public int? LatencyMs { get; init; }

    /// <summary>Number of rules that point at this role.</summary>
    public required int AssignedApps { get; init; }

    /// <summary>True when the adapter has no IPv6 route, so IPv6 is blocked for its apps (§23).</summary>
    public required bool Ipv4Only { get; init; }
}

/// <summary>One application rule with everything the main list, "Why?" and diagnostics need.</summary>
public sealed record AppStatusDto
{
    public required AppRule Rule { get; init; }
    public required EnforcementAction Action { get; init; }
    public required VerificationState Verification { get; init; }
    public string? ResolvedAdapterName { get; init; }
    public string? ObservedAdapterName { get; init; }
    public required int ActiveConnections { get; init; }
    public required int PreexistingConnections { get; init; }

    /// <summary>The §30 explanation, generated from the same pass that drives enforcement.</summary>
    public required IReadOnlyList<Reason> Reasons { get; init; }

    public required string VerificationSummary { get; init; }

    /// <summary>Set when enforcement for this rule failed; shown with Advanced Details.</summary>
    public IpcError? Error { get; init; }
}

public enum ServiceEventKind
{
    Info,
    RoleOffline,
    RoleRestored,
    RoleChanged,
    Leak,
    EnforcementError,
    EmergencyDisabled
}

/// <summary>Something worth telling the user about, e.g. "Gaming restored" (§18, §40).</summary>
public sealed record ServiceEventDto
{
    public required long Id { get; init; }
    public required DateTimeOffset At { get; init; }
    public required ServiceEventKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
}

public sealed record ServiceStatusDto
{
    public required int ProtocolVersion { get; init; }
    public required bool SetupCompleted { get; init; }

    /// <summary>Filters are installed and being maintained.</summary>
    public required bool EnforcementActive { get; init; }

    public required bool EnforcementPaused { get; init; }

    /// <summary>
    /// The bind-redirect driver is loaded. Without it NetRoute can block but not move
    /// traffic; the GUI must say so rather than imply otherwise.
    /// </summary>
    public required bool RedirectionAvailable { get; init; }

    public required IReadOnlyList<RoleStatusDto> Roles { get; init; }
    public required IReadOnlyList<AppStatusDto> Apps { get; init; }
    public required IReadOnlyList<LeakObservation> RecentLeaks { get; init; }
    public required IReadOnlyList<ServiceEventDto> RecentEvents { get; init; }
    public IpcError? LastError { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>Result of changing which adapter a role points at (§38).</summary>
public sealed record RoleChangeResultDto(RoleId Role, string AdapterName, int AffectedApps, string Message);

/// <summary>A live connection, for Advanced Diagnostics (§26, §54).</summary>
public sealed record ConnectionDto
{
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public string? ExecutablePath { get; init; }
    public string? PackageFamilyName { get; init; }
    public required TransportProtocol Protocol { get; init; }
    public required string LocalEndpoint { get; init; }
    public string? RemoteEndpoint { get; init; }
    public ulong? InterfaceLuid { get; init; }
    public string? InterfaceName { get; init; }
    public uint? InterfaceIndex { get; init; }
    public string? State { get; init; }
    public Guid? MatchedRuleId { get; init; }
}
