using NetRoute.Core.Adapters;

namespace NetRoute.Core.Policy;

public enum EnforcementAction
{
    /// <summary>Leave this application alone. No filters installed.</summary>
    None,

    /// <summary>Permit only on the role's adapter; block every other interface.</summary>
    PinToAdapter,

    /// <summary>Block all traffic — the role is unavailable and the kill switch is on.</summary>
    BlockAll,

    /// <summary>Role unavailable, Preferred mode, so Windows routing is allowed to take over.</summary>
    FallBackToWindows
}

/// <summary>One line of the §30 "Why?" explanation.</summary>
public sealed record Reason(bool Good, string Text)
{
    public static Reason Ok(string text) => new(true, text);
    public static Reason Bad(string text) => new(false, text);

    public override string ToString() => $"{(Good ? "✓" : "✗")} {Text}";
}

/// <summary>What NetRoute intends to do about one application, and why.</summary>
public sealed record AppEnforcement
{
    public required AppRule Rule { get; init; }
    public required EnforcementAction Action { get; init; }

    /// <summary>The adapter the role currently resolves to. Null when unresolvable.</summary>
    public required NetworkAdapter? ResolvedAdapter { get; init; }

    /// <summary>
    /// Block IPv6 outright for this application.
    ///
    /// <para>Set when the role's adapter has no IPv6 default route. Without this the
    /// application's IPv6 traffic would leave via whichever adapter does have one,
    /// quietly defeating the whole policy — the bypass §23 explicitly forbids.</para>
    /// </summary>
    public required bool BlockIpv6 { get; init; }

    public required IReadOnlyList<Reason> Reasons { get; init; }

    /// <summary>Short status for the app list: "Protected", "Blocked", "Not enforced".</summary>
    public string StatusSummary => Action switch
    {
        EnforcementAction.PinToAdapter => "Protected",
        EnforcementAction.BlockAll => "Blocked",
        EnforcementAction.FallBackToWindows => "Unprotected (fallback)",
        _ => "Not enforced"
    };
}

public sealed record EnforcementPlan
{
    public required IReadOnlyList<AppEnforcement> Applications { get; init; }

    /// <summary>Roles that are configured but whose adapter is currently unusable.</summary>
    public required IReadOnlyList<RoleId> DegradedRoles { get; init; }
}
