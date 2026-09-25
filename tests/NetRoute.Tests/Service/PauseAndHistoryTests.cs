using Microsoft.Extensions.Logging.Abstractions;
using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using NetRoute.Service;
using NetRoute.Windows.Traffic;

namespace NetRoute.Tests.Service;

public sealed class PauseAndHistoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"NetRoute.History.{Guid.NewGuid():N}");

    private static NetRouteConfig Config(bool pause) => new()
    {
        SetupCompleted = true,
        PauseDownloadsWhileGaming = pause,
        RoleBindings =
        [
            Bind(RoleId.Gaming, NetRouteEngineTests.Ethernet()),
            Bind(RoleId.Downloads, NetRouteEngineTests.Wifi())
        ],
        AppRules =
        [
            AppRule.Create(AppIdentity.ForExecutable(@"C:\Games\Halo.exe", "Halo Infinite"), RoleId.Gaming),
            AppRule.Create(AppIdentity.ForExecutable(@"C:\Steam\steam.exe", "Steam"), RoleId.Downloads)
        ]
    };

    private static PolicyResolver Resolver()
        => new(new NetRouteEngineTests.MutableAdapterSource(NetRouteEngineTests.Ethernet(), NetRouteEngineTests.Wifi()));

    [Fact]
    public void DownloadsAreBlockedOnlyWhileAGameIsRunning()
    {
        var plan = Resolver().Resolve(Config(pause: true), gamingActive: true, gameName: "Halo Infinite");
        var steam = plan.Applications.Single(a => a.Rule.App.DisplayName == "Steam");

        Assert.Equal(EnforcementAction.BlockAll, steam.Action);
        Assert.Contains(steam.Reasons, r => r.Text.Contains("Halo Infinite", StringComparison.Ordinal));
        Assert.Equal("Halo Infinite", plan.DownloadsPausedFor);
        // The game itself is untouched by its own pause rule.
        Assert.Equal(EnforcementAction.PinToAdapter, plan.Applications.Single(a => a.Rule.App.DisplayName == "Halo Infinite").Action);
    }

    [Fact]
    public void NothingIsPausedWhenNoGameRunsOrTheSettingIsOff()
    {
        Assert.Equal(EnforcementAction.PinToAdapter,
            Resolver().Resolve(Config(pause: true)).Applications.Single(a => a.Rule.App.DisplayName == "Steam").Action);
        Assert.Equal(EnforcementAction.PinToAdapter,
            Resolver().Resolve(Config(pause: false), gamingActive: true, gameName: "Halo Infinite")
                .Applications.Single(a => a.Rule.App.DisplayName == "Steam").Action);
        Assert.Null(Resolver().Resolve(Config(pause: true)).DownloadsPausedFor);
    }

    [Fact]
    public void UsageIsRecordedPerAppAndConnectionAndSurvivesAReread()
    {
        var totals = new FakeTotals();
        var history = new UsageHistory(totals, NullLogger<UsageHistory>.Instance, _folder);

        totals.Set("steam", "Wi-Fi 2", 100, 10);
        history.Sample();
        totals.Set("steam", "Wi-Fi 2", 300, 30);      // counters only ever climb
        totals.Set("HaloInfinite", "Ethernet", 50, 25);
        history.Sample();
        history.Flush();

        // A second instance reads what the first wrote: a service restart doesn't lose the day.
        var summary = new UsageHistory(new FakeTotals(), NullLogger<UsageHistory>.Instance, _folder).Summarise(1);

        Assert.Null(summary.Problem);
        var steam = summary.TopApps.Single(a => a.App == "steam");
        Assert.Equal(300, steam.DownBytes);
        Assert.Equal(30, steam.UpBytes);
        Assert.Equal("Wi-Fi 2", steam.Adapter);
        Assert.Equal(50, summary.TopApps.Single(a => a.App == "HaloInfinite").DownBytes);
        Assert.Equal(350, summary.Days.Sum(d => d.DownBytes));
    }

    private static RoleBinding Bind(RoleId role, NetworkAdapter adapter)
        => new() { Role = role, AdapterLuid = adapter.Luid, AdapterGuid = adapter.Guid, LastKnownName = adapter.Name };

    private sealed class FakeTotals : IAppTotalsSource
    {
        private readonly Dictionary<(string App, string Adapter), (long Down, long Up)> _totals = [];

        public void Set(string app, string adapter, long down, long up) => _totals[(app, adapter)] = (down, up);

        public IReadOnlyList<ProcessTotal> GetTotals()
            => _totals.Select(e => new ProcessTotal(e.Key.App, e.Key.Adapter, e.Value.Down, e.Value.Up)).ToList();
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }
}
