using NetRoute.Service;

var console = args.Contains("--console", StringComparer.OrdinalIgnoreCase);
var builder = Host.CreateDefaultBuilder(args.Where(a => !string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase)).ToArray());
if (!console) builder.UseWindowsService(options => options.ServiceName = "NetRoute");

// Everything is registered in ServiceRegistration, where a test can build the same container.
builder.ConfigureServices(services => services.AddNetRoute());

// Fail loudly here rather than inside the host: a dependency that doesn't line up would
// otherwise log "Service started successfully" and then stop the service seconds later.
await builder.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
}).Build().RunAsync();
