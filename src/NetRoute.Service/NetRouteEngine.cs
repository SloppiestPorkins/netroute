using System.Security.Cryptography;
using System.Text;
using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;
using NetRoute.Windows.Wfp;

namespace NetRoute.Service;

public interface IConnectionSource
{
    IReadOnlyList<ConnectionDto> GetConnections();
}

public sealed class EmptyConnectionSource : IConnectionSource
{
    public IReadOnlyList<ConnectionDto> GetConnections() => [];
}

public interface IPolicyPlanResolver
{
    EnforcementPlan Resolve(NetRouteConfig config);
}

public sealed class PolicyPlanResolver(IAdapterSource adapters) : IPolicyPlanResolver
{
    private readonly PolicyResolver _resolver = new(adapters);
    public EnforcementPlan Resolve(NetRouteConfig config) => _resolver.Resolve(config);
}

/// <summary>Owns service state; one gate makes mutations and reconciliation atomic.</summary>
public sealed class NetRouteEngine : IDisposable
{
    private readonly ConfigStore _store;
    private readonly IAdapterSource _adapters;
    private readonly IPolicyPlanResolver _resolver;
    private readonly IEnforcementBackend _backend;
    private readonly IAppVerifier _verifier;
    private readonly IConnectionSource _connections;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ServiceEventDto> _events = [];
    private Dictionary<RoleId, RoleHealth> _roleHealth = [];
    private Dictionary<Guid, IpcError> _ruleErrors = [];
    private EnforcementPlan? _plan;
    private string? _fingerprint;
    private DateTimeOffset _policyAppliedAt = DateTimeOffset.MinValue;
    private long _nextEventId;
    private IpcError? _lastError;
    private string? _redirectSummary;

    public NetRouteEngine(
        IAdapterSource adapters,
        IEnforcementBackend backend,
        string? configPath = null,
        IAppVerifier? verifier = null,
        IConnectionSource? connections = null,
        IPolicyPlanResolver? resolver = null)
    {
        _store = new ConfigStore(configPath);
        _adapters = adapters;
        _backend = backend;
        _verifier = verifier ?? new PlanOnlyVerifier();
        _connections = connections ?? new EmptyConnectionSource();
        _resolver = resolver ?? new PolicyPlanResolver(adapters);
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { ReconcileLocked(_store.Load()); }
        finally { _gate.Release(); }
    }

    public async Task<ServiceStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var config = _store.Load();
            _plan ??= _resolver.Resolve(config);
            var adapters = _adapters.DiscoverAll();
            var verifications = _verifier.Verify(_plan, _policyAppliedAt).ToDictionary(v => v.RuleId);
            return new ServiceStatusDto
            {
                ProtocolVersion = IpcProtocol.Version,
                SetupCompleted = config.SetupCompleted,
                EnforcementActive = _backend.IsAvailable && !config.EnforcementPaused && _ruleErrors.Count == 0,
                EnforcementPaused = config.EnforcementPaused,
                RedirectionAvailable = _backend.RedirectionAvailable,
                RedirectSummary = _redirectSummary,
                Roles = BuildRoles(config, adapters),
                Apps = _plan.Applications.Select(a => BuildApp(a, verifications.GetValueOrDefault(a.Rule.Id))).ToList(),
                RecentLeaks = verifications.Values.SelectMany(v => v.Leaks).OrderByDescending(l => l.At).ToList(),
                RecentEvents = _events.ToList(),
                LastError = _lastError,
                GeneratedAt = DateTimeOffset.UtcNow
            };
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<AdapterDto>> GetAdaptersAsync(CancellationToken ct = default)
        => LockedAsync(() => (IReadOnlyList<AdapterDto>)_adapters.DiscoverAll().Select(AdapterDto.From).ToList(), ct);

    public Task<IReadOnlyList<ConnectionDto>> GetConnectionsAsync(CancellationToken ct = default)
        => LockedAsync(_connections.GetConnections, ct);

    public async Task CompleteSetupAsync(ulong gamingLuid, ulong downloadsLuid, CancellationToken ct = default)
    {
        await MutateAsync(config =>
        {
            var adapters = _adapters.DiscoverAll();
            var gaming = RequireAdapter(adapters, gamingLuid);
            var downloads = RequireAdapter(adapters, downloadsLuid);
            return config with { SetupCompleted = true, RoleBindings = [Binding(RoleId.Gaming, gaming), Binding(RoleId.Downloads, downloads)] };
        }, ct);
    }

    public async Task<RoleChangeResultDto> SetRoleAdapterAsync(RoleId role, ulong adapterLuid, CancellationToken ct = default)
    {
        if (role == RoleId.Default) throw Friendly("Default follows Windows and cannot be assigned to an adapter.");
        RoleChangeResultDto? result = null;
        await MutateAsync(config =>
        {
            var adapter = RequireAdapter(_adapters.DiscoverAll(), adapterLuid);
            var affected = config.AppRules.Count(r => r.Role == role);
            var bindings = config.RoleBindings.Where(b => b.Role != role).Append(Binding(role, adapter)).ToList();
            result = new(role, adapter.Name, affected, $"{role.DisplayName()} is now {adapter.Name}. {affected} applications updated.");
            AddEvent(ServiceEventKind.RoleChanged, $"{role.DisplayName()} changed", result.Message);
            return config with { RoleBindings = bindings };
        }, ct);
        return result!;
    }

    public async Task<AppStatusDto> AddRuleAsync(AppIdentity app, RoleId role, CancellationToken ct = default)
    {
        AppRule? added = null;
        await MutateAsync(config =>
        {
            if (config.AppRules.Any(r => r.App.StableKey == app.StableKey))
                throw Friendly($"{app.DisplayName} is already in your apps.");
            added = AppRule.Create(app, role);
            return config with { AppRules = [.. config.AppRules, added] };
        }, ct);
        return (await GetStatusAsync(ct)).Apps.Single(a => a.Rule.Id == added!.Id);
    }

    public Task UpdateRuleAsync(AppRule rule, CancellationToken ct = default) => MutateAsync(config =>
    {
        if (!config.AppRules.Any(r => r.Id == rule.Id)) throw Friendly("That app rule no longer exists.");
        return config with { AppRules = config.AppRules.Select(r => r.Id == rule.Id ? rule : r).ToList() };
    }, ct);

    public Task RemoveRuleAsync(Guid ruleId, CancellationToken ct = default) => MutateAsync(config =>
        config with { AppRules = config.AppRules.Where(r => r.Id != ruleId).ToList() }, ct);

    public Task SetRulePausedAsync(Guid ruleId, bool paused, CancellationToken ct = default) => MutateAsync(config =>
    {
        if (!config.AppRules.Any(r => r.Id == ruleId)) throw Friendly("That app rule no longer exists.");
        return config with { AppRules = config.AppRules.Select(r => r.Id == ruleId ? r with { Paused = paused } : r).ToList() };
    }, ct);

    public Task SetEnforcementPausedAsync(bool paused, CancellationToken ct = default)
        => MutateAsync(config => config with { EnforcementPaused = paused }, ct);

    public async Task EmergencyDisableAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Filter removal must not depend on readable config or a working resolver.
            _backend.EmergencyDisable();
            var config = _store.Load() with { EnforcementPaused = true };
            _store.Save(config);
            _fingerprint = null;
            _redirectSummary = "Off. Emergency Disable restored normal Windows networking.";
            AddEvent(ServiceEventKind.EmergencyDisabled, "Emergency disable", "All NetRoute enforcement was disabled.");
        }
        finally { _gate.Release(); }
    }

    private async Task MutateAsync(Func<NetRouteConfig, NetRouteConfig> mutation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var config = mutation(_store.Load());
            _store.Save(config);
            ReconcileLocked(config);
        }
        finally { _gate.Release(); }
    }

    private void ReconcileLocked(NetRouteConfig config)
    {
        var plan = _resolver.Resolve(config);
        RecordHealthTransitions(config, plan);
        var fingerprint = Fingerprint(plan);
        if (fingerprint != _fingerprint)
        {
            var result = _backend.Apply(plan);
            _ruleErrors = result.Failures.ToDictionary(f => f.RuleId, f => ToError(f.Error));
            _policyAppliedAt = DateTimeOffset.UtcNow;
            _fingerprint = fingerprint;
        }
        _plan = plan;

        if (_backend.IsAvailable)
        {
            var summary = _backend.ApplyRedirect(RedirectPlanner.Build(config, plan, _adapters.DiscoverAll()));
            if (summary is not null && summary != _redirectSummary)
            {
                AddEvent(ServiceEventKind.Info, "Moving apps", summary);
            }
            _redirectSummary = summary;
        }

        _lastError = !_backend.IsAvailable
            ? new IpcError { FriendlyMessage = _backend.UnavailableReason ?? "Network policy is unavailable." }
            : _ruleErrors.Values.FirstOrDefault();
    }

    private void RecordHealthTransitions(NetRouteConfig config, EnforcementPlan plan)
    {
        var adapters = _adapters.DiscoverAll();
        foreach (var role in Role.Bindable)
        {
            var health = Health(config.BindingFor(role), adapters);
            if (_roleHealth.TryGetValue(role, out var old) && old != health)
            {
                var count = config.AppRules.Count(r => r.Role == role);
                var binding = config.BindingFor(role);
                var name = adapters.FirstOrDefault(a => a.Luid == binding?.AdapterLuid)?.Name ?? binding?.LastKnownName ?? "The selected network";
                if (health != RoleHealth.Connected)
                    AddEvent(ServiceEventKind.RoleOffline, $"{role.DisplayName()} unavailable", $"{role.DisplayName()} unavailable. {name} is offline. {count} apps are protected by Kill Switch.");
                else
                    AddEvent(ServiceEventKind.RoleRestored, $"{role.DisplayName()} restored", $"{role.DisplayName()} restored. {name} connected. Traffic restored for {count} apps.");
            }
            _roleHealth[role] = health;
        }
    }

    private IReadOnlyList<RoleStatusDto> BuildRoles(NetRouteConfig config, IReadOnlyList<NetworkAdapter> adapters)
        => Role.Bindable.Select(role =>
        {
            var binding = config.BindingFor(role);
            var adapter = binding is null ? null : adapters.FirstOrDefault(a => a.Luid == binding.AdapterLuid);
            return new RoleStatusDto
            {
                Role = role, Adapter = adapter is null ? null : AdapterDto.From(adapter), LastKnownName = binding?.LastKnownName,
                Health = Health(binding, adapters), LatencyMs = null,
                AssignedApps = config.AppRules.Count(r => r.Role == role), Ipv4Only = adapter?.IsIpv4Only ?? false
            };
        }).ToList();

    private AppStatusDto BuildApp(AppEnforcement app, AppVerification? verification)
    {
        verification ??= new PlanOnlyVerifier().Verify(new EnforcementPlan { Applications = [app], DegradedRoles = [] }, _policyAppliedAt).Single();
        return new AppStatusDto
        {
            Rule = app.Rule, Action = app.Action, Verification = verification.State,
            ResolvedAdapterName = app.ResolvedAdapter?.Name, ObservedAdapterName = verification.ObservedAdapterName,
            ActiveConnections = verification.ActiveConnections, PreexistingConnections = verification.PreexistingConnections,
            Reasons = app.Reasons, VerificationSummary = verification.Summary, Error = _ruleErrors.GetValueOrDefault(app.Rule.Id)
        };
    }

    private static RoleHealth Health(RoleBinding? binding, IReadOnlyList<NetworkAdapter> adapters)
    {
        if (binding is null) return RoleHealth.Unassigned;
        var adapter = adapters.FirstOrDefault(a => a.Luid == binding.AdapterLuid);
        return adapter?.State switch { null => RoleHealth.Missing, AdapterState.Connected => RoleHealth.Connected, AdapterState.NoInternet => RoleHealth.NoInternet, _ => RoleHealth.Offline };
    }

    private static string Fingerprint(EnforcementPlan plan)
    {
        var text = string.Join('\n', plan.Applications.OrderBy(a => a.Rule.Id).Select(a => string.Join('|',
            // The program path is included so an app updating into a new folder (Discord's
            // app-x.y.z) re-applies its filters instead of leaving them on the old file.
            a.Rule.Id, a.Rule.App.ExecutablePath, a.Action, a.ResolvedAdapter?.Luid, a.ResolvedAdapter?.Ipv4Address, a.ResolvedAdapter?.Ipv6Address,
            a.BlockIpv6, a.Rule.Mode, a.Rule.KillSwitch, a.Rule.EnforceIpv4, a.Rule.EnforceIpv6,
            a.Rule.EnforceTcp, a.Rule.EnforceUdp, a.Rule.Paused, a.Rule.IncludeRelatedProcesses)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static NetworkAdapter RequireAdapter(IReadOnlyList<NetworkAdapter> adapters, ulong luid)
        => adapters.FirstOrDefault(a => a.Luid == luid) ?? throw Friendly("That network adapter is no longer present.");
    private static RoleBinding Binding(RoleId role, NetworkAdapter adapter) => new() { Role = role, AdapterLuid = adapter.Luid, AdapterGuid = adapter.Guid, LastKnownName = adapter.Name };
    private static NetRouteServiceException Friendly(string message) => new(new IpcError { FriendlyMessage = message });
    private static IpcError ToError(Exception ex) => ex is WfpException wfp
        ? new() { FriendlyMessage = wfp.FriendlyMessage, TechnicalDetail = ex.Message }
        : new() { FriendlyMessage = "NetRoute could not apply this app's network policy.", TechnicalDetail = ex.ToString() };
    private void AddEvent(ServiceEventKind kind, string title, string message)
    {
        _events.Add(new() { Id = ++_nextEventId, At = DateTimeOffset.UtcNow, Kind = kind, Title = title, Message = message });
        if (_events.Count > 50) _events.RemoveRange(0, _events.Count - 50);
    }
    private async Task<T> LockedAsync<T>(Func<T> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct); try { return action(); } finally { _gate.Release(); }
    }
    public void Dispose() { _backend.Dispose(); _gate.Dispose(); }
}
