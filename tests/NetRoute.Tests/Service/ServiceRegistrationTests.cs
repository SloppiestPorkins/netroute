using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRoute.Service;

namespace NetRoute.Tests.Service;

/// <summary>
/// The service can actually be built.
///
/// <para>This exists because of a real failure: IAppTotalsSource was never registered, so every
/// start logged "Service started successfully", threw while the host was building its hosted
/// services, and stopped. Nothing else in the suite noticed, because every other test constructs
/// what it needs by hand.</para>
/// </summary>
public class ServiceRegistrationTests
{
    private static ServiceProvider Build()
        => new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.None))
            .AddNetRoute()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    [Fact]
    public void Every_registration_can_be_resolved()
    {
        using var provider = Build();

        Assert.NotNull(provider.GetRequiredService<NetRouteEngine>());
        Assert.NotNull(provider.GetRequiredService<NamedPipeServer>());
    }

    [Fact]
    public void Every_background_job_the_service_runs_can_be_created()
    {
        using var provider = Build();

        // The list the host itself would build at startup, which is where the missing one showed up.
        var hosted = provider.GetServices<IHostedService>().ToList();

        Assert.Contains(hosted, h => h is UsageHistory);
        Assert.Contains(hosted, h => h is Updater);
        Assert.Contains(hosted, h => h is ConnectionHistory);
        Assert.Contains(hosted, h => h is ServiceWorker);
    }

    [Fact]
    public void The_rate_monitor_is_the_one_object_behind_both_of_its_jobs()
    {
        using var provider = Build();

        // Two interfaces, one ETW session: resolving them separately would start two.
        Assert.Same(provider.GetRequiredService<IAppRateSource>(), provider.GetRequiredService<IAppTotalsSource>());
    }
}
