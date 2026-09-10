using NetRoute.Core.Policy;

namespace NetRoute.Core.Traffic;

/// <summary>
/// How much NetRoute actually knows about an application's traffic (§24).
///
/// <para>The ordering of evidence matters and the names are chosen so that none of them
/// can be mistaken for a stronger claim than it is. In particular, a rule whose filters
/// were installed successfully is only <see cref="Configured"/>; it becomes
/// <see cref="Verified"/> only once traffic has been seen on the expected adapter.</para>
/// </summary>
public enum VerificationState
{
    /// <summary>NetRoute is not enforcing this application (Default role, paused, or fallback).</summary>
    NotEnforced,

    /// <summary>Enforcement is installed but the application has no live processes to observe.</summary>
    NotRunning,

    /// <summary>Enforcement is installed and the app is running, but no attributable traffic has been seen yet.</summary>
    Configured,

    /// <summary>Traffic was observed, and all of it used the expected adapter.</summary>
    Verified,

    /// <summary>The role is unavailable and the kill switch is holding the application's traffic.</summary>
    Blocked,

    /// <summary>Traffic created after the policy was applied was observed on an adapter other than the expected one.</summary>
    Leak
}

/// <summary>A flow that did not use the adapter its role resolves to (§25).</summary>
public sealed record LeakObservation
{
    public required DateTimeOffset At { get; init; }
    public int? ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string ExpectedAdapter { get; init; }
    public required string ObservedAdapter { get; init; }
    public required TransportProtocol Protocol { get; init; }
    public required string RemoteEndpoint { get; init; }

    /// <summary>
    /// True when WFP refused the flow. A blocked attempt is evidence the policy is
    /// working, not a leak, but the user is still told about it.
    /// </summary>
    public required bool Blocked { get; init; }
}

/// <summary>The verification result for one rule.</summary>
public sealed record AppVerification
{
    public required Guid RuleId { get; init; }
    public required VerificationState State { get; init; }

    /// <summary>Adapter the traffic was actually seen on, when a single one was.</summary>
    public string? ObservedAdapterName { get; init; }
    public ulong? ObservedAdapterLuid { get; init; }

    /// <summary>Live connections attributed to this rule's processes.</summary>
    public int ActiveConnections { get; init; }

    /// <summary>
    /// Connections that predate the most recent policy change and so may legitimately still
    /// be on the previous adapter. Surfaced honestly rather than moved, per §19.
    /// </summary>
    public int PreexistingConnections { get; init; }

    public IReadOnlyList<LeakObservation> Leaks { get; init; } = [];

    /// <summary>One-line human summary, e.g. "Actual traffic verified on Ethernet."</summary>
    public required string Summary { get; init; }
}

/// <summary>Produces verification results for an enforcement plan.</summary>
public interface IAppVerifier
{
    /// <param name="plan">The plan currently enforced.</param>
    /// <param name="policyAppliedAt">When the plan was last applied; flows older than this are pre-existing.</param>
    IReadOnlyList<AppVerification> Verify(EnforcementPlan plan, DateTimeOffset policyAppliedAt);
}

/// <summary>
/// A verifier that looks only at the plan and never claims to have seen traffic.
///
/// <para>The honest fallback when no traffic source is available. It can report
/// Configured, Blocked or NotEnforced, and by construction never Verified.</para>
/// </summary>
public sealed class PlanOnlyVerifier : IAppVerifier
{
    public IReadOnlyList<AppVerification> Verify(EnforcementPlan plan, DateTimeOffset policyAppliedAt)
        => plan.Applications.Select(Describe).ToList();

    private static AppVerification Describe(AppEnforcement app) => app.Action switch
    {
        EnforcementAction.PinToAdapter => new AppVerification
        {
            RuleId = app.Rule.Id,
            State = VerificationState.Configured,
            Summary = $"Policy applied for {app.ResolvedAdapter?.Name ?? "the selected network"}; traffic not yet verified."
        },
        EnforcementAction.BlockAll => new AppVerification
        {
            RuleId = app.Rule.Id,
            State = VerificationState.Blocked,
            Summary = $"{app.Rule.Role.DisplayName()} network unavailable. Traffic blocked by Kill Switch."
        },
        _ => new AppVerification
        {
            RuleId = app.Rule.Id,
            State = VerificationState.NotEnforced,
            Summary = "NetRoute is not overriding Windows routing for this app."
        }
    };
}
