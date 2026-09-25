using System.Net;
using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using NetRoute.Ipc;
using NetRoute.Service;

namespace NetRoute.Tests.Service;

public sealed class NetRouteEngineTests : IDisposable
{
    private const ulong EthernetLuid = 11;
    private const ulong WifiLuid = 22;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"NetRoute.Tests.{Guid.NewGuid():N}");
    private string ConfigPath => Path.Combine(_directory, "config.json");

    [Fact]
    public async Task SetupAddDuplicateAndRoleChangeKeepRuleRoles()
    {
        var source = new MutableAdapterSource(Ethernet(), Wifi());
        using var engine = new NetRouteEngine(source, new CountingBackend(), ConfigPath);
        await engine.CompleteSetupAsync(EthernetLuid, WifiLuid);
        var halo = await engine.AddRuleAsync(AppIdentity.ForExecutable(@"C:\Games\Halo.exe", "Halo Infinite"), RoleId.Gaming);
        var steam = await engine.AddRuleAsync(AppIdentity.ForExecutable(@"C:\Steam\steam.exe", "Steam"), RoleId.Downloads);

        var duplicate = await Assert.ThrowsAsync<NetRouteServiceException>(() =>
            engine.AddRuleAsync(halo.Rule.App, RoleId.Downloads));
        Assert.Equal("Halo Infinite is already in your apps.", duplicate.Error.FriendlyMessage);

        var result = await engine.SetRoleAdapterAsync(RoleId.Gaming, WifiLuid);
        var status = await engine.GetStatusAsync();
        Assert.True(status.SetupCompleted);
        Assert.Equal(1, result.AffectedApps);
        Assert.Equal(RoleId.Downloads, status.Apps.Single(a => a.Rule.Id == steam.Rule.Id).Rule.Role);
        Assert.Equal(WifiLuid, status.Apps.Single(a => a.Rule.Id == steam.Rule.Id).Rule.Role == RoleId.Downloads
            ? status.Roles.Single(r => r.Role == RoleId.Downloads).Adapter!.Luid : 0UL);
    }

    [Fact]
    public async Task OfflineAndRestoredCreateEventsAndBlockStrictApp()
    {
        var source = new MutableAdapterSource(Ethernet(), Wifi());
        using var engine = new NetRouteEngine(source, new CountingBackend(), ConfigPath);
        await engine.CompleteSetupAsync(EthernetLuid, WifiLuid);
        await engine.AddRuleAsync(AppIdentity.ForExecutable(@"C:\Games\Halo.exe", "Halo Infinite"), RoleId.Gaming);

        source.Adapters = [Ethernet(AdapterState.Disconnected), Wifi()];
        await engine.ReconcileAsync();
        var offline = await engine.GetStatusAsync();
        Assert.Equal(EnforcementAction.BlockAll, offline.Apps.Single().Action);
        Assert.Contains(offline.RecentEvents, e => e.Kind == ServiceEventKind.RoleOffline && e.Message.Contains("1 apps are protected"));

        source.Adapters = [Ethernet(), Wifi()];
        await engine.ReconcileAsync();
        Assert.Contains((await engine.GetStatusAsync()).RecentEvents, e => e.Kind == ServiceEventKind.RoleRestored);
    }

    [Fact]
    public async Task UnchangedFingerprintDoesNotApplyAgain()
    {
        var backend = new CountingBackend();
        using var engine = new NetRouteEngine(new MutableAdapterSource(Ethernet(), Wifi()), backend, ConfigPath);
        await engine.ReconcileAsync();
        await engine.ReconcileAsync();
        Assert.Equal(1, backend.ApplyCount);
    }

    [Fact]
    public async Task EmergencyDisableRemovesFiltersBeforeThrowingResolverAndPersistsPause()
    {
        var backend = new CountingBackend();
        using var engine = new NetRouteEngine(new MutableAdapterSource(Ethernet()), backend, ConfigPath, resolver: new ThrowingResolver());
        await engine.EmergencyDisableAsync();
        Assert.Equal(1, backend.DisableCount);
        Assert.True(new ConfigStore(ConfigPath).Load().EnforcementPaused);
    }

    [Fact]
    public async Task DefaultRoleAllowedForRuleButNotRoleBinding()
    {
        using var engine = new NetRouteEngine(new MutableAdapterSource(Ethernet()), new CountingBackend(), ConfigPath);
        var app = await engine.AddRuleAsync(AppIdentity.ForExecutable(@"C:\Tools\tool.exe", "Tool"), RoleId.Default);
        Assert.Equal(RoutingMode.Default, app.Rule.Mode);
        await Assert.ThrowsAsync<NetRouteServiceException>(() => engine.SetRoleAdapterAsync(RoleId.Default, EthernetLuid));
        await Assert.ThrowsAsync<NetRouteServiceException>(() => engine.CompleteSetupAsync(EthernetLuid, 999));
    }

    [Fact]
    public async Task TiedDefaultRoutesAreReportedAndFixedTowardDownloads()
    {
        var routes = new FakeRoutes(new DefaultRoute(EthernetLuid, 11, 0), new DefaultRoute(WifiLuid, 22, 0));
        var backend = new CountingBackend { OnFix = () => routes.Routes = [new DefaultRoute(EthernetLuid, 11, 20), new DefaultRoute(WifiLuid, 22, 0)] };
        using var engine = new NetRouteEngine(new MutableAdapterSource(Ethernet(), Wifi()), backend, ConfigPath, routes: routes);
        await engine.CompleteSetupAsync(EthernetLuid, WifiLuid);

        var tie = (await engine.GetStatusAsync()).RouteTie;
        Assert.NotNull(tie);
        Assert.Equal(new[] { "Ethernet", "Wi-Fi" }, tie!.AdapterNames);

        var result = await engine.FixRouteTieAsync();
        Assert.True(result.Fixed);
        Assert.Equal(WifiLuid, backend.FixedPreferred);
        Assert.Null((await engine.GetStatusAsync()).RouteTie);
        Assert.True((await engine.FixRouteTieAsync()).Fixed);
        Assert.Equal(1, backend.FixCount);
    }

    [Fact]
    public async Task TimedPauseEndsByItself()
    {
        new ConfigStore(ConfigPath).Save(new NetRouteConfig { EnforcementPaused = true, PausedUntil = DateTimeOffset.UtcNow.AddMinutes(-1) });
        using var engine = new NetRouteEngine(new MutableAdapterSource(Ethernet(), Wifi()), new CountingBackend(), ConfigPath);
        await engine.ReconcileAsync();
        var resumed = await engine.GetStatusAsync();
        Assert.False(resumed.EnforcementPaused);
        Assert.Null(resumed.PausedUntil);
        Assert.Contains(resumed.RecentEvents, e => e.Kind == ServiceEventKind.Resumed);

        await engine.SetEnforcementPausedAsync(true, 60);
        await engine.ReconcileAsync();
        var paused = await engine.GetStatusAsync();
        Assert.True(paused.EnforcementPaused);
        Assert.InRange(paused.PausedUntil!.Value, DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddMinutes(61));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    internal static NetworkAdapter Ethernet(AdapterState state = AdapterState.Connected) => Adapter(EthernetLuid, "Ethernet", AdapterKind.Ethernet, state, "192.168.1.10", "2001:db8::1");
    internal static NetworkAdapter Wifi(AdapterState state = AdapterState.Connected) => Adapter(WifiLuid, "Wi-Fi", AdapterKind.WiFi, state, "192.168.2.10", null);
    private static NetworkAdapter Adapter(ulong luid, string name, AdapterKind kind, AdapterState state, string ipv4, string? ipv6) => new()
    {
        Luid = luid, Guid = $"{{00000000-0000-0000-0000-{luid:D12}}}", Name = name, Description = name,
        Kind = kind, State = state, InterfaceIndex = (uint)luid, InterfaceIndexV6 = (uint)luid,
        Ipv4Address = IPAddress.Parse(ipv4), Ipv6Address = ipv6 is null ? null : IPAddress.Parse(ipv6),
        Gateways = ipv6 is null ? [IPAddress.Parse("192.168.2.1")] : [IPAddress.Parse("192.168.1.1"), IPAddress.Parse("fe80::1")],
        DnsServers = [], LinkSpeedBps = 1_000_000_000, Ipv4Metric = 10, Ipv6Metric = 10
    };

    internal sealed class MutableAdapterSource(params NetworkAdapter[] adapters) : IAdapterSource
    {
        public IReadOnlyList<NetworkAdapter> Adapters { get; set; } = adapters;
        public IReadOnlyList<NetworkAdapter> DiscoverAll() => Adapters;
    }

    internal sealed class CountingBackend : IEnforcementBackend
    {
        public int ApplyCount { get; private set; }
        public int DisableCount { get; private set; }
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool RedirectionAvailable => false;
        public BackendApplyResult Apply(EnforcementPlan plan) { ApplyCount++; return BackendApplyResult.Success; }
        public void EmergencyDisable() => DisableCount++;
        public Action? OnFix { get; init; }
        public ulong? FixedPreferred { get; private set; }
        public int FixCount { get; private set; }
        public string FixRouteTie(RouteTie tie, NetworkAdapter preferred) { FixCount++; FixedPreferred = preferred.Luid; OnFix?.Invoke(); return "fixed"; }
        public void Dispose() { }
    }

    internal sealed class FakeRoutes(params DefaultRoute[] routes) : IDefaultRouteSource
    {
        public IReadOnlyList<DefaultRoute> Routes { get; set; } = routes;
        public IReadOnlyList<DefaultRoute> ReadIpv4() => Routes;
    }

    private sealed class ThrowingResolver : IPolicyPlanResolver
    {
        public EnforcementPlan Resolve(NetRouteConfig config, string? pauseDownloadsBecause = null)
            => throw new InvalidOperationException("resolver failure");
    }
}
