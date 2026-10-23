using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
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

public sealed class NoDefaultRoutes : IDefaultRouteSource
{
    public IReadOnlyList<DefaultRoute> ReadIpv4() => [];
}

/// <summary>What the download apps are fetching, read from wherever they keep that.</summary>
public interface IDownloadSource
{
    IReadOnlyList<DownloadActivityDto> InProgress();
}

/// <summary>Steam's own manifests. Slow if a library drive is asleep, so never call it under the gate.</summary>
public sealed class SteamDownloadSource : IDownloadSource
{
    public IReadOnlyList<DownloadActivityDto> InProgress()
        => NetRoute.Windows.Apps.SteamDownloads.InProgress()
            .Select(d => new DownloadActivityDto("Steam", d.Game, d.BytesRemaining))
            .Take(5)
            .ToList();
}

public interface IPolicyPlanResolver
{
    EnforcementPlan Resolve(NetRouteConfig config, string? pauseDownloadsBecause = null);
}

public sealed class PolicyPlanResolver(IAdapterSource adapters) : IPolicyPlanResolver
{
    private readonly PolicyResolver _resolver = new(adapters);
    public EnforcementPlan Resolve(NetRouteConfig config, string? pauseDownloadsBecause = null)
        => _resolver.Resolve(config, pauseDownloadsBecause);
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
    private readonly IDefaultRouteSource _routes;
    private readonly ILinkQualitySource _links;
    private readonly IAppRateSource _rates;
    private readonly SelfTestRunner? _selfTest;
    private readonly IUsageHistory _history;
    private readonly IUpdateSource _updates;
    private readonly IConnectionHistory _connectionHistory;
    private readonly IDownloadSource _downloadSource;
    private readonly TimeProvider _time;
    private readonly ILogger<NetRouteEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _systemDownloadsError;
    private readonly List<ServiceEventDto> _events = [];
    private Dictionary<RoleId, RoleHealth> _roleHealth = [];
    private Dictionary<Guid, IpcError> _ruleErrors = [];
    private EnforcementPlan? _plan;
    private PlanPrint? _applied;
    private int _failedApplies;
    private DateTimeOffset _retryAt;
    private string? _lastGame;
    private DateTimeOffset _lastGameAt;
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
        IPolicyPlanResolver? resolver = null,
        IDefaultRouteSource? routes = null,
        ILinkQualitySource? links = null,
        IAppRateSource? rates = null,
        SelfTestRunner? selfTest = null,
        IUsageHistory? history = null,
        IUpdateSource? updates = null,
        IConnectionHistory? connectionHistory = null,
        IDownloadSource? downloads = null,
        TimeProvider? time = null,
        ILogger<NetRouteEngine>? logger = null)
    {
        _downloadSource = downloads ?? new SteamDownloadSource();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<NetRouteEngine>.Instance;
        _updates = updates ?? new NoUpdates();
        _connectionHistory = connectionHistory ?? new NoConnectionHistory();
        _links = links ?? new NoLinkQuality();
        _rates = rates ?? new NoAppRates();
        _selfTest = selfTest;
        _history = history ?? new NoUsageHistory();
        _store = new ConfigStore(configPath);
        _adapters = adapters;
        _backend = backend;
        _verifier = verifier ?? new PlanOnlyVerifier();
        _connections = connections ?? new EmptyConnectionSource();
        _resolver = resolver ?? new PolicyPlanResolver(adapters);
        _routes = routes ?? new NoDefaultRoutes();
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var config = _store.Load();
            if (config.EnforcementPaused && config.PausedUntil is { } until && until <= DateTimeOffset.UtcNow)
            {
                config = config with { EnforcementPaused = false, PausedUntil = null };
                _store.Save(config);
                AddEvent(ServiceEventKind.Resumed, "Protection is back on",
                    "The pause ended, so NetRoute is keeping your apps on their networks again.");
            }
            ReconcileLocked(config);
        }
        finally { _gate.Release(); }
    }

    public async Task<ServiceStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var config = _store.Load();
            var plan = _plan ??= _resolver.Resolve(config);
            var adapters = _adapters.DiscoverAll();
            var verifications = _verifier.Verify(plan, _policyAppliedAt).ToDictionary(v => v.RuleId);
            var tie = FindTie(adapters);
            return new ServiceStatusDto
            {
                ProtocolVersion = IpcProtocol.Version,
                SetupCompleted = config.SetupCompleted,
                // Rules that failed are reported per rule (Apps[].Error). One stale path must not make
                // the other rules, which are installed and working, look switched off.
                EnforcementActive = _backend.IsAvailable && !config.EnforcementPaused,
                EnforcementPaused = config.EnforcementPaused,
                PausedUntil = config.EnforcementPaused ? config.PausedUntil : null,
                SystemDownloads = DescribeSystemDownloads(config),
                DownloadsPause = new DownloadsPauseDto(config.PauseDownloadsWhileGaming, plan.DownloadsPausedFor,
                    config.DownloadQuietHours?.ToString()),
                RedirectionAvailable = _backend.RedirectionAvailable,
                RedirectSummary = _redirectSummary,
                Roles = BuildRoles(config, adapters),
                Apps = plan.Applications.Select(a => BuildApp(a, verifications.GetValueOrDefault(a.Rule.Id))).ToList(),
                RecentLeaks = verifications.Values.SelectMany(v => v.Leaks).OrderByDescending(l => l.At).ToList(),
                RecentEvents = _events.ToList(),
                Update = _updates.Available,
                Downloading = Downloading(),
                RouteTie = tie is null ? null : new RouteTieDto(tie.Adapters.Select(a => a.Name).ToList(),
                    $"{tie.Names} are tied as Windows' default connection, so Windows splits traffic between them " +
                    "and a download can use both at once."),
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

    public Task SetEnforcementPausedAsync(bool paused, int? minutes = null, CancellationToken ct = default)
        => MutateAsync(config => config with
        {
            EnforcementPaused = paused,
            PausedUntil = paused && minutes is > 0 ? DateTimeOffset.UtcNow.AddMinutes(minutes.Value) : null
        }, ct);

    public Task SetSystemDownloadsAsync(bool enabled, CancellationToken ct = default)
        => MutateAsync(config => config with { RouteSystemDownloads = enabled }, ct);

    /// <summary>Not under the gate: the meter has its own lock and nothing here touches config.</summary>
    public AppRatesDto GetAppRates() => _rates.GetRates();

    public async Task EmergencyDisableAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Filter removal must not depend on readable config or a working resolver.
            _backend.EmergencyDisable();
            var config = _store.Load() with { EnforcementPaused = true, PausedUntil = null };
            _store.Save(config);
            _applied = null;
            _failedApplies = 0;
            _redirectSummary = "Off. Emergency Disable restored normal Windows networking.";
            AddEvent(ServiceEventKind.EmergencyDisabled, "Emergency disable", "All NetRoute enforcement was disabled.");
        }
        finally { _gate.Release(); }
    }

    private SystemDownloadsDto DescribeSystemDownloads(NetRouteConfig config)
    {
        const string what = "Windows Update, Microsoft Store and Xbox app downloads";
        if (!config.RouteSystemDownloads)
        {
            return new(false, false, $"Off. {what} use whichever connection Windows picks.");
        }
        if (_plan?.SystemDownloads is not { } system)
        {
            return new(true, false, $"On, waiting: {what} are kept on Downloads while protection is on and Downloads is connected.");
        }
        if (_systemDownloadsError is { } error)
        {
            return new(true, false, $"On, but it couldn't be applied: {error}");
        }
        return new(true, true, $"On. {what} are kept on {system.Adapter.Name}" +
                               (system.BlockIpv6 ? $", with IPv6 blocked because {system.Adapter.Name} has none." : "."));
    }

    public async Task<RouteFixResultDto> FixRouteTieAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (FindTie(_adapters.DiscoverAll()) is not { } tie)
            {
                return new(true, "Windows already has one default connection. Nothing to fix.");
            }

            // Downloads is the connection NetRoute makes the default, so it wins the tie-break.
            var config = _store.Load();
            var downloads = config.BindingFor(RoleId.Downloads)?.AdapterLuid;
            var preferred = tie.Adapters.FirstOrDefault(a => a.Luid == downloads)
                            ?? tie.Adapters.OrderByDescending(a => a.LinkSpeedBps).First();
            string message;
            try
            {
                message = _backend.FixRouteTie(tie, preferred);
            }
            catch (NotSupportedException ex)
            {
                throw Friendly(ex.Message);
            }
            catch (Exception ex)
            {
                throw new NetRouteServiceException(new IpcError { FriendlyMessage = "NetRoute couldn't change the connection settings.", TechnicalDetail = ex.Message });
            }

            AddEvent(ServiceEventKind.Info, "Default connection fixed", message);
            ReconcileLocked(config);
            return FindTie(_adapters.DiscoverAll()) is { } still
                ? new(false, $"{message} Windows still ranks {still.Names} equally.")
                : new(true, message);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The name of a Gaming app with live connections, or null. Runtime state the resolver can't
    /// see, and the trigger for pausing downloads while you play.
    /// </summary>
    private string? GameInPlay(NetRouteConfig config)
    {
        try
        {
            var connections = _connections.GetConnections();
            foreach (var rule in config.AppRules.Where(r => r.Role == RoleId.Gaming && !r.Paused))
            {
                var app = VersionedPaths.Resolve(rule.App);
                if (connections.Any(c => AppMatching.Covers(app, c.ExecutablePath, c.PackageFamilyName)))
                {
                    return app.DisplayName;
                }
            }
        }
        catch (Exception)
        {
            // Never let looking for a game stop enforcement.
        }
        return null;
    }

    public Task SetPauseDownloadsAsync(bool enabled, CancellationToken ct = default)
        => MutateAsync(config => config with { PauseDownloadsWhileGaming = enabled }, ct);

    public Task SetQuietHoursAsync(int? fromHour, int? toHour, CancellationToken ct = default)
        => MutateAsync(config => config with
        {
            DownloadQuietHours = fromHour is { } from && toHour is { } to
                ? new QuietHours(Math.Clamp(from, 0, 23), Math.Clamp(to, 0, 23))
                : null
        }, ct);

    /// <summary>Why Downloads apps are blocked right now, as a phrase for the user, or null.</summary>
    private string? WhyDownloadsArePaused(NetRouteConfig config)
    {
        if (config.DownloadQuietHours is { } hours && hours.Contains(DateTime.Now))
        {
            return $"it is quiet hours ({hours})";
        }
        if (!config.PauseDownloadsWhileGaming)
        {
            _lastGame = null;
            return null;
        }
        var now = _time.GetUtcNow();
        if (GameInPlay(config) is { } game)
        {
            _lastGame = game;
            _lastGameAt = now;
            return $"{game} is running";
        }

        // A game's connections come and go (a lobby, a loading screen). Unblocking downloads in
        // each gap rebuilds every filter and kills the download connections that just opened.
        return _lastGame is { } recent && now - _lastGameAt < GameGrace ? $"{recent} is running" : null;
    }

    /// <summary>How long after its last connection a game still counts as running.</summary>
    internal static readonly TimeSpan GameGrace = TimeSpan.FromSeconds(45);

    public SelfTestDto GetSelfTest()
        => _selfTest?.State ?? new SelfTestDto(false, null, [], "This build can't run the test.");

    /// <summary>Gathers what the test needs, then starts it outside the gate: it takes ~30 seconds.</summary>
    public async Task<SelfTestDto> StartSelfTestAsync(CancellationToken ct = default)
    {
        if (_selfTest is null)
        {
            return GetSelfTest();
        }
        var status = await GetStatusAsync(ct);
        var adapters = _adapters.DiscoverAll();
        NetworkAdapter? For(RoleId role) => status.Roles.FirstOrDefault(r => r.Role == role)?.Adapter is { } dto
            ? adapters.FirstOrDefault(a => a.Luid == dto.Luid)
            : null;
        return _selfTest.Start(new SelfTestInput(
            For(RoleId.Gaming),
            For(RoleId.Downloads),
            status.RedirectionAvailable,
            status.EnforcementPaused,
            status.Apps.Where(a => a.Verification == VerificationState.Verified)
                .Select(a => $"{a.Rule.App.DisplayName} on {a.ObservedAdapterName}").ToList()));
    }

    public UsageHistoryDto GetUsageHistory(int days) => _history.Summarise(days);

    public IReadOnlyList<ConnectionHistoryDto> GetConnectionHistory(int limit) => _connectionHistory.Recent(limit);

    public Task<UpdateDto?> CheckForUpdateAsync(CancellationToken ct = default) => _updates.CheckAsync(ct);

    public Task<UpdateDto?> DownloadUpdateAsync(CancellationToken ct = default) => _updates.DownloadAsync(ct);

    public UpdateSettingsDto GetUpdateSettings() => _updates.Settings();

    /// <summary>Saves where to look for updates, then looks, so the answer is there when the user asks.</summary>
    public async Task SetUpdateSettingsAsync(string? feedUrl, bool automatic, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(feedUrl) ? null : feedUrl.Trim();
        await MutateAsync(config => config with { UpdateFeedUrl = url, AutomaticUpdates = automatic }, ct);
        if (url is not null)
        {
            await _updates.CheckAsync(ct);
        }
    }

    private volatile IReadOnlyList<DownloadActivityDto> _downloading = [];
    private long _downloadingAtTicks;
    private int _downloadRefreshing;

    internal static readonly TimeSpan DownloadsRefresh = TimeSpan.FromSeconds(10);

    /// <summary>
    /// What the download apps are fetching. Reading Steam's manifests touches every library
    /// drive, and a sleeping one can stall for seconds, so this only returns the last answer and
    /// starts a refresh in the background. Status is built under the gate, which reconcile and
    /// Emergency Disable also need.
    /// </summary>
    private IReadOnlyList<DownloadActivityDto> Downloading()
    {
        var age = _time.GetUtcNow().UtcTicks - Interlocked.Read(ref _downloadingAtTicks);
        if (age > DownloadsRefresh.Ticks && Interlocked.CompareExchange(ref _downloadRefreshing, 1, 0) == 0)
        {
            _ = Task.Run(RefreshDownloading);
        }
        return _downloading;
    }

    private void RefreshDownloading()
    {
        try
        {
            _downloading = _downloadSource.InProgress();
        }
        catch (Exception)
        {
            _downloading = [];
        }
        finally
        {
            Interlocked.Exchange(ref _downloadingAtTicks, _time.GetUtcNow().UtcTicks);
            Interlocked.Exchange(ref _downloadRefreshing, 0);
        }
    }

    private RouteTie? FindTie(IReadOnlyList<NetworkAdapter> adapters)
    {
        try
        {
            return RouteTies.Find(adapters, _routes.ReadIpv4());
        }
        catch (Exception)
        {
            // A failed routing-table read must not take status down with it.
            return null;
        }
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
        var plan = _resolver.Resolve(config, WhyDownloadsArePaused(config));
        RecordHealthTransitions(config, plan);
        var print = Fingerprint(plan);
        var now = _time.GetUtcNow();
        string? trigger = null;
        if (_applied is null || print.Hash != _applied.Hash)
        {
            trigger = Describe(_applied, print);
            _failedApplies = 0;
        }
        else if (_failedApplies > 0 && now >= _retryAt)
        {
            // Same plan, but last time something would not install. A Steam library on a drive
            // that mounts late, or WindowsApps not yet readable at boot, usually comes good, and
            // until it does that app is running unfiltered.
            trigger = $"retry {_failedApplies} after a rule failed to install";
        }

        if (trigger is not null)
        {
            var result = _backend.Apply(plan);
            _ruleErrors = result.Failures.ToDictionary(f => f.RuleId, f => ToError(f.Error));
            _systemDownloadsError = result.SystemDownloadsError;
            _policyAppliedAt = DateTimeOffset.UtcNow;
            _applied = print;
            _failedApplies = result.Failures.Count > 0 || result.SystemDownloadsError is not null ? _failedApplies + 1 : 0;
            _retryAt = now + RetryDelay(_failedApplies);
            if (_backend.IsAvailable)
            {
                LogApply(result, trigger, now);
            }
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

    /// <summary>An apply slower than this is worth a warning: it stalls the app's own traffic while filters swap.</summary>
    private static readonly TimeSpan SlowApply = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A commit slower than this is worth a warning on its own: the commit is the part where the filter
    /// engine swaps the set in, and the only part that can hold up other programs' new connections.
    /// A quarter of a second is far above what a diff of a few dozen filters should cost, and well
    /// below the seconds an app would notice.
    /// </summary>
    private static readonly TimeSpan SlowCommit = TimeSpan.FromMilliseconds(250);

    /// <summary>The routine line is capped to one a minute so a flapping adapter cannot fill the Event Log.</summary>
    private static readonly TimeSpan RoutineLogInterval = TimeSpan.FromMinutes(1);

    private DateTimeOffset _routineLoggedAt = DateTimeOffset.MinValue;
    private int _routineSuppressed;

    /// <summary>
    /// One line per apply (the counts are what actually changed in the engine, so an apply that found nothing to change says 0 and 0), so a latency spike in the user's apps can be lined up with a commit.
    /// The Event Log keeps only Warning and above unless configured otherwise, so anything a person
    /// needs to see (a failure or a slow apply) is logged at Warning and is never rate-limited;
    /// retries are already backed off. The routine case is Information and rate-limited.
    /// </summary>
    private void LogApply(BackendApplyResult result, string trigger, DateTimeOffset now)
    {
        const string Format = "Filters applied in {ElapsedMs:F0} ms (commit {CommitMs:F0} ms): {Added} filter(s) added, {Removed} removed, {Failed} rule(s) failed. Trigger: {Trigger}.";
        object[] args =
        [
            result.Elapsed.TotalMilliseconds, result.CommitTime.TotalMilliseconds,
            result.FiltersAdded, result.FiltersRemoved, result.Failures.Count, trigger
        ];

        if (result.Failures.Count > 0 || result.SystemDownloadsError is not null)
        {
            _logger.LogWarning(Format + " Some rules were not applied; see the app's Check-up for which.", args);
        }
        else if (result.CommitTime >= SlowCommit)
        {
            _logger.LogWarning(Format + " The commit was slow; new connections from other programs may have stalled while it ran.", args);
        }
        else if (result.Elapsed >= SlowApply)
        {
            _logger.LogWarning(Format + " This apply was slow.", args);
        }
        else if (now - _routineLoggedAt >= RoutineLogInterval)
        {
            _logger.LogInformation(
                _routineSuppressed == 0 ? Format : Format + " ({Skipped} earlier apply line(s) not logged.)",
                _routineSuppressed == 0 ? args : [.. args, _routineSuppressed]);
            _routineLoggedAt = now;
            _routineSuppressed = 0;
        }
        else
        {
            _routineSuppressed++;
        }
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
            var quality = adapter is null ? null : _links.For(adapter.Luid);
            return new RoleStatusDto
            {
                Role = role, Adapter = adapter is null ? null : AdapterDto.From(adapter), LastKnownName = binding?.LastKnownName,
                Health = Health(binding, adapters), LatencyMs = quality?.LatencyMs, PacketLossPercent = quality?.LossPercent,
                InternetReachable = quality?.Reachable,
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

    /// <summary>
    /// Backs off retrying a plan that would not install: 10 s, 30 s, 2 min, then every 5 min. A
    /// rule that can never work (a deleted game) must not rebuild ~1500 filters every tick, and
    /// one that is only late (a drive mounting) should not wait long.
    /// </summary>
    internal static TimeSpan RetryDelay(int failedApplies) => failedApplies switch
    {
        <= 1 => TimeSpan.FromSeconds(10),
        2 => TimeSpan.FromSeconds(30),
        3 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(5)
    };

    /// <summary>One app's share of the fingerprint, split by cause so a rebuild can say why it happened.</summary>
    private sealed record AppPrint(string Name, string Program, string Action, bool BlockIpv6, string Adapter, string Settings);

    private sealed record PlanPrint(string Hash, Dictionary<Guid, AppPrint> Apps, string System);

    /// <summary>
    /// What the filters depend on. They match an application and an interface LUID, so the
    /// adapter's addresses are deliberately absent: a DHCP renewal or an IPv6 temporary-address
    /// rotation would otherwise tear down and rebuild every filter for nothing. Whether the
    /// adapter has IPv6 at all still counts, through BlockIpv6.
    /// </summary>
    private static PlanPrint Fingerprint(EnforcementPlan plan)
    {
        var apps = plan.Applications.ToDictionary(a => a.Rule.Id, a => new AppPrint(
            a.Rule.App.DisplayName,
            // The program path is included so an app updating into a new folder (Discord's
            // app-x.y.z) re-applies its filters instead of leaving them on the old file.
            a.Rule.App.ExecutablePath ?? string.Empty,
            a.Action.ToString(),
            a.BlockIpv6,
            a.ResolvedAdapter?.Luid.ToString() ?? string.Empty,
            string.Join('|', a.Rule.Mode, a.Rule.KillSwitch, a.Rule.EnforceIpv4, a.Rule.EnforceIpv6,
                a.Rule.EnforceTcp, a.Rule.EnforceUdp, a.Rule.Paused, a.Rule.IncludeRelatedProcesses)));
        var system = plan.SystemDownloads is { } downloads
            ? $"{downloads.Adapter.Luid}|{downloads.BlockIpv6}|{string.Join(',', downloads.Services)}"
            : "off";
        var text = string.Join('\n', apps.OrderBy(a => a.Key).Select(a =>
            $"{a.Key}|{a.Value.Program}|{a.Value.Action}|{a.Value.Adapter}|{a.Value.BlockIpv6}|{a.Value.Settings}"))
            + "\nsystem|" + system;
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), apps, system);
    }

    /// <summary>Why filters are being rebuilt, in plain words for the log: which part of the plan moved.</summary>
    private static string Describe(PlanPrint? before, PlanPrint after)
    {
        if (before is null)
        {
            return "first apply since the service started or Emergency Disable";
        }
        var changes = new List<string>();
        foreach (var (id, now) in after.Apps)
        {
            if (!before.Apps.TryGetValue(id, out var was))
            {
                changes.Add($"{now.Name} added");
                continue;
            }
            if (was.Program != now.Program) changes.Add($"{now.Name} program path changed");
            if (was.Action != now.Action) changes.Add($"{now.Name} action {was.Action} to {now.Action}");
            if (was.BlockIpv6 != now.BlockIpv6) changes.Add($"{now.Name} IPv6 blocking {(now.BlockIpv6 ? "on" : "off")}");
            if (was.Adapter != now.Adapter) changes.Add($"{now.Name} moved to a different adapter");
            if (was.Settings != now.Settings) changes.Add($"{now.Name} rule settings changed");
        }
        changes.AddRange(before.Apps.Where(a => !after.Apps.ContainsKey(a.Key)).Select(a => $"{a.Value.Name} removed"));
        if (before.System != after.System) changes.Add("system downloads settings changed");

        const int shown = 4;
        return changes.Count == 0 ? "plan changed"
            : changes.Count <= shown ? string.Join("; ", changes)
            : $"{string.Join("; ", changes.Take(shown))}; and {changes.Count - shown} more";
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
