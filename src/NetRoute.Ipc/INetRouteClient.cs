using NetRoute.Core.Policy;

namespace NetRoute.Ipc;

/// <summary>
/// Everything the GUI and CLI can ask of the service.
///
/// <para>Application discovery is deliberately absent. The service runs as LocalSystem,
/// and packaged-app enumeration there returns SYSTEM's packages rather than the user's
/// Store and Game Pass titles. Discovery runs in the GUI, in the user's session, and
/// only the chosen <see cref="AppIdentity"/> crosses this boundary.</para>
///
/// <para>Calls throw <see cref="ServiceUnavailableException"/> when the service cannot be
/// reached and <see cref="NetRouteServiceException"/> when it answered with a failure.</para>
/// </summary>
public interface INetRouteClient
{
    Task PingAsync(CancellationToken ct = default);

    Task<ServiceStatusDto> GetStatusAsync(CancellationToken ct = default);

    Task<IReadOnlyList<AdapterDto>> GetAdaptersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ConnectionDto>> GetConnectionsAsync(CancellationToken ct = default);

    /// <summary>First-run: bind Gaming and Downloads in one step (§5, §52).</summary>
    Task CompleteSetupAsync(ulong gamingLuid, ulong downloadsLuid, CancellationToken ct = default);

    /// <summary>Repoint a role. Rules follow the role; no rule is reassigned (§33, §38).</summary>
    Task<RoleChangeResultDto> SetRoleAdapterAsync(RoleId role, ulong adapterLuid, CancellationToken ct = default);

    Task<AppStatusDto> AddRuleAsync(AppIdentity app, RoleId role, CancellationToken ct = default);

    Task UpdateRuleAsync(AppRule rule, CancellationToken ct = default);

    Task RemoveRuleAsync(Guid ruleId, CancellationToken ct = default);

    Task SetRulePausedAsync(Guid ruleId, bool paused, CancellationToken ct = default);

    /// <summary>Pause or resume everything. With <paramref name="minutes"/>, the pause ends by itself.</summary>
    Task SetEnforcementPausedAsync(bool paused, int? minutes = null, CancellationToken ct = default);

    /// <summary>Remove every NetRoute filter and keep enforcement off until resumed (§43).</summary>
    Task EmergencyDisableAsync(CancellationToken ct = default);

    /// <summary>Give Windows one default connection when two are tied (see <see cref="RouteTieDto"/>).</summary>
    Task<RouteFixResultDto> FixRouteTieAsync(CancellationToken ct = default);

    /// <summary>Per-program speeds on each network right now.</summary>
    Task<AppRatesDto> GetAppRatesAsync(CancellationToken ct = default);

    /// <summary>Keep Windows Update, Store and Xbox downloads on Downloads (see SystemDownloadsPlan).</summary>
    Task SetSystemDownloadsAsync(bool enabled, CancellationToken ct = default);
}
