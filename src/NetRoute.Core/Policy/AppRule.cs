using System.Text.Json.Serialization;

namespace NetRoute.Core.Policy;

public enum RoutingMode
{
    /// <summary>
    /// NetRoute does not place this app (§16). It follows Windows' default route, which NetRoute
    /// points at the Downloads connection while any app is assigned to Gaming (see RedirectPlanner).
    /// </summary>
    Default,

    /// <summary>
    /// Only the assigned role's adapter may be used. If it is down, the app's traffic is held
    /// (blocked) rather than sent anywhere else (§15, §18). This is the "kill switch": there is no
    /// separate setting for it, because "strict but allowed to fall back" is what Preferred is.
    /// </summary>
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

    public bool EnforceIpv6 { get; init; } = true;

    /// <summary>Temporarily suspended by the user without deleting the rule (§29).</summary>
    public bool Paused { get; init; }

    // ---- Retired settings -------------------------------------------------------------------
    //
    // These used to be rule settings that nothing in the UI, CLI or IPC could change, or that
    // changed nothing at all. They are no longer stored in the config or sent over the pipe
    // (JsonIgnore), and nobody can set them: they are constants that say what has always been the
    // behaviour. They remain only because NetRouteEngine's rule fingerprint and WFP FilterPlanner
    // still read them; delete these five lines when those two references are removed.

    /// <summary>Always <c>Mode == Strict</c>: the kill switch and Strict mode are one thing.</summary>

    public static AppRule Create(AppIdentity app, RoleId role) => new()
    {
        Id = Guid.NewGuid(),
        App = app,
        Role = role,
        Mode = role == RoleId.Default ? RoutingMode.Default : RoutingMode.Strict
    };
}
