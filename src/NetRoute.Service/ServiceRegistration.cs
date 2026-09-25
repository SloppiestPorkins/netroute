using Microsoft.Extensions.DependencyInjection;
using NetRoute.Core.Adapters;
using NetRoute.Windows.Wfp;

namespace NetRoute.Service;

/// <summary>
/// Everything the service is made of, in one place.
///
/// <para>This lives here rather than in Program.cs so a test can build the container and find a
/// missing registration at build time. It is worth the file: a service whose dependencies don't
/// line up starts, reports "Service started successfully", throws inside the host and stops —
/// which reads, from outside, like a machine with no protection and no explanation.</para>
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddNetRoute(this IServiceCollection services)
    {
        services.AddSingleton<IAdapterSource, AdapterDiscovery>();
        services.AddSingleton<IDefaultRouteSource, DefaultRouteTable>();
        services.AddSingleton<IEnforcementBackend>(_ =>
        {
            if (!WfpSession.CanOpen(out var reason))
            {
                return new NullEnforcementBackend(reason);
            }
            try
            {
                return new WfpEnforcementBackend();
            }
            catch (Exception ex)
            {
                return new NullEnforcementBackend(ex.Message);
            }
        });

        // Observed traffic: what makes "Verified" mean something, and feeds the live view.
        services.AddSingleton(sp => new NetRoute.Windows.Traffic.TrafficObserver(sp.GetRequiredService<IAdapterSource>()));
        services.AddSingleton<ConnectionVerifier>();
        services.AddSingleton<NetRoute.Core.Traffic.IAppVerifier>(sp => sp.GetRequiredService<ConnectionVerifier>());
        services.AddSingleton<IConnectionSource>(sp => sp.GetRequiredService<ConnectionVerifier>());

        // Measured ping/loss per network, and per-app speeds (ETW).
        services.AddSingleton<LinkQualityMonitor>();
        services.AddSingleton<ILinkQualitySource>(sp => sp.GetRequiredService<LinkQualityMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<LinkQualityMonitor>());
        services.AddSingleton<AppRateMonitor>();
        services.AddSingleton<IAppRateSource>(sp => sp.GetRequiredService<AppRateMonitor>());
        services.AddSingleton<IAppTotalsSource>(sp => sp.GetRequiredService<AppRateMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<AppRateMonitor>());

        // The on-demand proof, and the usage history that feeds "what used my gaming line last night".
        services.AddSingleton<SelfTestRunner>();
        services.AddSingleton<UsageHistory>();
        services.AddSingleton<IUsageHistory>(sp => sp.GetRequiredService<UsageHistory>());
        services.AddHostedService(sp => sp.GetRequiredService<UsageHistory>());
        services.AddSingleton<ConnectionHistory>();
        services.AddSingleton<IConnectionHistory>(sp => sp.GetRequiredService<ConnectionHistory>());
        services.AddHostedService(sp => sp.GetRequiredService<ConnectionHistory>());

        // Updates: checked and fetched here, installed only when the user says so (see Updater).
        services.AddSingleton<Updater>();
        services.AddSingleton<IUpdateSource>(sp => sp.GetRequiredService<Updater>());
        services.AddHostedService(sp => sp.GetRequiredService<Updater>());

        services.AddSingleton<NetRouteEngine>();
        services.AddSingleton<NamedPipeServer>();
        services.AddHostedService<ServiceWorker>();
        return services;
    }
}
