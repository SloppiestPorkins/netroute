namespace NetRoute.Core.Policy;

public enum RoutingMode
{
    /// <summary>NetRoute does not interfere. Windows routing applies (§16).</summary>
    Default,

    /// <summary>Only the assigned role's adapter may be used. If it is down, traffic is blocked (§15).</summary>
    Strict,

    /// <summary>Try the assigned role; fall back to Windows routing when it is unavailable (§17).</summary>
    Preferred
}

/// <summary>
/// One user decision: this application goes to this role.
///
/// <para>Note what is absent — there is no adapter here. The rule points at a role,
/// and the role points at an adapter. §33 depends on that separation.</para>
/// </summary>
public sealed record AppRule
{
    public required Guid Id { get; init; }
    public required AppIdentity App { get; init; }
    public required RoleId Role { get; init; }

    /// <summary>Strict is the correct default for games; see §15 and §17.</summary>
    public RoutingMode Mode { get; init; } = RoutingMode.Strict;

    /// <summary>Block rather than let traffic fall back to another network when the role is down (§18).</summary>
    public bool KillSwitch { get; init; } = true;

    public bool EnforceIpv4 { get; init; } = true;
    public bool EnforceIpv6 { get; init; } = true;
    public bool EnforceTcp { get; init; } = true;
    public bool EnforceUdp { get; init; } = true;

    /// <summary>Temporarily suspended by the user without deleting the rule (§29).</summary>
    public bool Paused { get; init; }

    /// <summary>Apply to child processes of this application as well (§13).</summary>
    public bool IncludeRelatedProcesses { get; init; }

    public static AppRule Create(AppIdentity app, RoleId role) => new()
    {
        Id = Guid.NewGuid(),
        App = app,
        Role = role,
        Mode = role == RoleId.Default ? RoutingMode.Default : RoutingMode.Strict
    };
}
