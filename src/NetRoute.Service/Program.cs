using NetRoute.Core.Adapters;
using NetRoute.Service;
using NetRoute.Windows.Wfp;

var console = args.Contains("--console", StringComparer.OrdinalIgnoreCase);
var builder = Host.CreateDefaultBuilder(args.Where(a => !string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase)).ToArray());
if (!console) builder.UseWindowsService(options => options.ServiceName = "NetRoute");

builder.ConfigureServices(services =>
{
    services.AddSingleton<IAdapterSource, AdapterDiscovery>();
    services.AddSingleton<IEnforcementBackend>(_ =>
    {
        if (!WfpSession.CanOpen(out var reason)) return new NullEnforcementBackend(reason);
        try { return new WfpEnforcementBackend(); }
        catch (Exception ex) { return new NullEnforcementBackend(ex.Message); }
    });
    services.AddSingleton<NetRouteEngine>();
    services.AddSingleton<NamedPipeServer>();
    services.AddHostedService<ServiceWorker>();
});

await builder.Build().RunAsync();
