using NetRoute.Core.Adapters;
using NetRoute.Service;
using NetRoute.Windows.Wfp;

var console = args.Contains("--console", StringComparer.OrdinalIgnoreCase);
var builder = Host.CreateDefaultBuilder(args.Where(a => !string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase)).ToArray());
if (!console) builder.UseWindowsService(options => options.ServiceName = "NetRoute");

builder.ConfigureServices(services =>
{
    services.AddSingleton<IAdapterSource, AdapterDiscovery>();
    services.AddSingleton<IDefaultRouteSource, DefaultRouteTable>();
    services.AddSingleton<IEnforcementBackend>(_ =>
    {
        if (!WfpSession.CanOpen(out var reason)) return new NullEnforcementBackend(reason);
        try { return new WfpEnforcementBackend(); }
        catch (Exception ex) { return new NullEnforcementBackend(ex.Message); }
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
    services.AddHostedService(sp => sp.GetRequiredService<AppRateMonitor>());
    // The on-demand proof, and the usage history that feeds "what used my gaming line last night".
    services.AddSingleton<SelfTestRunner>();
    services.AddSingleton<UsageHistory>();
    services.AddSingleton<IUsageHistory>(sp => sp.GetRequiredService<UsageHistory>());
    services.AddHostedService(sp => sp.GetRequiredService<UsageHistory>());
    services.AddSingleton<NetRouteEngine>();
    services.AddSingleton<NamedPipeServer>();
    services.AddHostedService<ServiceWorker>();
});

await builder.Build().RunAsync();
