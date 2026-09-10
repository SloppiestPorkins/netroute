using NetRoute.Core.Adapters;
using NetRoute.Core.Policy;
using NetRoute.Windows.Split;
using NetRoute.Windows.Wfp;

namespace NetRoute.Service;

public sealed record RuleEnforcementFailure(Guid RuleId, Exception Error);

public sealed record BackendApplyResult(IReadOnlyList<RuleEnforcementFailure> Failures)
{
    public static BackendApplyResult Success { get; } = new([]);
}

/// <summary>Keeps policy decisions independent from privileged WFP and driver operations.</summary>
public interface IEnforcementBackend : IDisposable
{
    BackendApplyResult Apply(EnforcementPlan plan);
    void EmergencyDisable();
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    bool RedirectionAvailable { get; }

    /// <summary>
    /// Moves Gaming apps onto the Gaming adapter and makes Downloads the default route (see
    /// <see cref="RedirectPlan"/>). Returns a sentence for the status line, or null if this
    /// backend can't move apps.
    /// </summary>
    string? ApplyRedirect(RedirectPlan plan) => null;
}

/// <summary>
/// The real backend. The WFP filters are the strict-mode and IPv6 safety net: they are what
/// keep an app off the wrong network. The split-tunnel driver is what puts it on the right one.
/// </summary>
public sealed class WfpEnforcementBackend : IEnforcementBackend
{
    private readonly WfpSession _session;
    private readonly WfpEnforcer _enforcer;
    private readonly SplitTunnelController _split;

    public WfpEnforcementBackend()
    {
        var adapters = new AdapterDiscovery();
        _session = WfpSession.Open();
        _enforcer = new WfpEnforcer(_session, adapters);
        _split = new SplitTunnelController(new DefaultRouteManager(adapters));
    }

    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public bool RedirectionAvailable => _split.Available;

    public BackendApplyResult Apply(EnforcementPlan plan)
    {
        var result = _enforcer.Apply(plan);
        return new(result.Failed.Select(f => new RuleEnforcementFailure(f.Enforcement.Rule.Id, f.Error)).ToList());
    }

    public string? ApplyRedirect(RedirectPlan plan)
    {
        var outcome = _split.Apply(plan);
        if (outcome.Engaged)
        {
            var route = outcome.RouteNote is { } note && note.Contains("still", StringComparison.Ordinal) ? $" Warning: {note}" : string.Empty;
            return $"On. {outcome.SplitImages} Gaming program file(s) are moved onto {plan.Gaming!.Name}; " +
                   $"{plan.Downloads!.Name} is Windows' default connection.{route}";
        }
        return $"Off. {outcome.Problem ?? plan.Reason ?? "Nothing to move."}";
    }

    public void EmergencyDisable()
    {
        // Filters first: they are the part that can cut traffic off.
        try
        {
            _enforcer.EmergencyDisable();
        }
        finally
        {
            _split.DisableAll();
        }
    }

    public void Dispose()
    {
        try
        {
            _split.Dispose();
        }
        finally
        {
            _session.Dispose();
        }
    }
}

/// <summary>Serves diagnostics safely when this process cannot open WFP.</summary>
public sealed class NullEnforcementBackend(string? reason = null) : IEnforcementBackend
{
    public bool IsAvailable => false;
    public string? UnavailableReason { get; } = reason ?? "NetRoute needs administrator rights to manage network policy.";
    public bool RedirectionAvailable => false;
    public BackendApplyResult Apply(EnforcementPlan plan) => BackendApplyResult.Success;
    public void EmergencyDisable() { }
    public void Dispose() { }
}
