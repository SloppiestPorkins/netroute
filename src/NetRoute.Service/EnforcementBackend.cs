using NetRoute.Core.Adapters;
using NetRoute.Core.Policy;
using NetRoute.Windows.Wfp;

namespace NetRoute.Service;

public sealed record RuleEnforcementFailure(Guid RuleId, Exception Error);

public sealed record BackendApplyResult(IReadOnlyList<RuleEnforcementFailure> Failures)
{
    public static BackendApplyResult Success { get; } = new([]);
}

/// <summary>Keeps policy decisions independent from privileged WFP operations.</summary>
public interface IEnforcementBackend : IDisposable
{
    BackendApplyResult Apply(EnforcementPlan plan);
    void EmergencyDisable();
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    bool RedirectionAvailable { get; }
}

public sealed class WfpEnforcementBackend : IEnforcementBackend
{
    private readonly WfpSession _session;
    private readonly WfpEnforcer _enforcer;

    public WfpEnforcementBackend()
    {
        _session = WfpSession.Open();
        _enforcer = new WfpEnforcer(_session, new AdapterDiscovery());
    }

    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public bool RedirectionAvailable => false;

    public BackendApplyResult Apply(EnforcementPlan plan)
    {
        var result = _enforcer.Apply(plan);
        return new(result.Failed.Select(f => new RuleEnforcementFailure(f.Enforcement.Rule.Id, f.Error)).ToList());
    }

    public void EmergencyDisable() => _enforcer.EmergencyDisable();
    public void Dispose() => _session.Dispose();
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
