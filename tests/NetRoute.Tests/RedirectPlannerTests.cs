using System.Net;
using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using NetRoute.Windows.Split;
using Xunit;

namespace NetRoute.Tests;

public class RedirectPlannerTests
{
    private const ulong EthernetLuid = 1, WifiLuid = 2;

    /// <summary>
    /// The inverted mapping is the whole point. Gaming apps are split and moved onto Gaming,
    /// and Downloads becomes the default route. Launchers are NOT split: if they were, the
    /// games they start would inherit the split and follow them onto Downloads (§36/§37).
    /// </summary>
    [Fact]
    public void SplitsOnlyGamingAppsAndMakesDownloadsTheDefault()
    {
        var (config, plan, adapters) = Scenario();
        var redirect = RedirectPlanner.Build(config, plan, adapters);

        Assert.True(redirect.ManageDefaultRoute);
        Assert.True(redirect.Split);
        Assert.Equal(WifiLuid, redirect.Downloads!.Luid);
        Assert.Equal(EthernetLuid, redirect.Gaming!.Luid);
        Assert.Equal("Halo Infinite", Assert.Single(redirect.SplitRules).App.DisplayName);
    }

    [Fact]
    public void DoesNothingUntilSetupIsFinished()
    {
        var (config, plan, adapters) = Scenario();
        var redirect = RedirectPlanner.Build(config with { SetupCompleted = false }, plan, adapters);
        Assert.False(redirect.ManageDefaultRoute);
        Assert.False(redirect.Split);
    }

    /// <summary>Paused means Windows' normal routing, so the default route must be handed back too.</summary>
    [Fact]
    public void PauseRestoresTheDefaultRoute()
    {
        var (config, plan, adapters) = Scenario();
        var redirect = RedirectPlanner.Build(config with { EnforcementPaused = true }, plan, adapters);
        Assert.False(redirect.ManageDefaultRoute);
        Assert.False(redirect.Split);
    }

    /// <summary>A cable pulled for a moment must not flip Windows' default connection back and forth.</summary>
    [Fact]
    public void GamingOfflineStopsSplittingButKeepsTheRoutePreference()
    {
        var (config, _, _) = Scenario();
        var adapters = new[] { Ethernet(AdapterState.Disconnected), Wifi() };
        var plan = new PolicyResolver(new Fake(adapters)).Resolve(config);

        var redirect = RedirectPlanner.Build(config, plan, adapters);
        Assert.True(redirect.ManageDefaultRoute);
        Assert.False(redirect.Split);
        Assert.Contains("Ethernet", redirect.Reason);
    }

    [Fact]
    public void NoGamingAppsMeansNothingToSplit()
    {
        var (config, _, adapters) = Scenario();
        config = config with { AppRules = config.AppRules.Where(r => r.Role != RoleId.Gaming).ToList() };
        var plan = new PolicyResolver(new Fake(adapters)).Resolve(config);

        var redirect = RedirectPlanner.Build(config, plan, adapters);
        Assert.True(redirect.ManageDefaultRoute);
        Assert.False(redirect.Split);
    }

    [Fact]
    public void FingerprintChangesWhenAnAdapterAddressChanges()
    {
        var (config, plan, adapters) = Scenario();
        var before = RedirectPlanner.Build(config, plan, adapters).Fingerprint;
        var moved = new[] { Ethernet() with { Ipv4Address = IPAddress.Parse("192.168.0.99") }, Wifi() };
        var after = RedirectPlanner.Build(config, new PolicyResolver(new Fake(moved)).Resolve(config), moved).Fingerprint;
        Assert.NotEqual(before, after);
    }

    // ---- split image paths ----

    [Fact]
    public void ASteamGameCoversItsWholeFolderWithinTheDepthLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "nr-" + Guid.NewGuid().ToString("N"), "steamapps", "common", "DayZ");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            Directory.CreateDirectory(Path.Combine(root, "a", "b", "c", "d", "e"));
            File.WriteAllText(Path.Combine(root, "DayZ_BE.exe"), "");
            File.WriteAllText(Path.Combine(root, "DayZ_x64.exe"), "");
            File.WriteAllText(Path.Combine(root, "bin", "helper.exe"), "");
            File.WriteAllText(Path.Combine(root, "a", "b", "c", "d", "e", "too-deep.exe"), "");

            var images = SplitImagePaths.For(AppIdentity.ForExecutable(Path.Combine(root, "DayZ_BE.exe"), "DayZ"));

            Assert.Contains(images, p => p.EndsWith("DayZ_x64.exe", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(images, p => p.EndsWith("helper.exe", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(images, p => p.EndsWith("too-deep.exe", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(Path.GetFullPath(Path.Combine(root, "..", "..", "..")), recursive: true);
        }
    }

    /// <summary>Expanding a system folder would move Windows components onto the Gaming network.</summary>
    [Fact]
    public void NeverExpandsSystemOrBroadFolders()
    {
        Assert.False(SplitImagePaths.IsSafeRoot(@"C:\"));
        Assert.False(SplitImagePaths.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)));
        Assert.False(SplitImagePaths.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.False(SplitImagePaths.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

        var curl = SplitImagePaths.For(AppIdentity.ForExecutable(@"C:\Windows\System32\curl.exe"));
        Assert.Equal(@"C:\Windows\System32\curl.exe", Assert.Single(curl), ignoreCase: true);
    }

    // ---- helpers ----

    private static (NetRouteConfig, EnforcementPlan, IReadOnlyList<NetworkAdapter>) Scenario()
    {
        var adapters = new[] { Ethernet(), Wifi() };
        var config = new NetRouteConfig
        {
            SetupCompleted = true,
            RoleBindings =
            [
                new RoleBinding { Role = RoleId.Gaming, AdapterLuid = EthernetLuid },
                new RoleBinding { Role = RoleId.Downloads, AdapterLuid = WifiLuid }
            ],
            AppRules =
            [
                AppRule.Create(AppIdentity.ForExecutable(@"C:\Games\HaloInfinite.exe", "Halo Infinite"), RoleId.Gaming),
                AppRule.Create(AppIdentity.ForExecutable(@"C:\Steam\steam.exe", "Steam"), RoleId.Downloads),
                AppRule.Create(AppIdentity.ForExecutable(@"C:\Tools\thing.exe", "Thing"), RoleId.Default)
            ]
        };
        return (config, new PolicyResolver(new Fake(adapters)).Resolve(config), adapters);
    }

    private static NetworkAdapter Ethernet(AdapterState state = AdapterState.Connected) => new()
    {
        Luid = EthernetLuid, Guid = "{E}", Name = "Ethernet", Description = "Realtek", Kind = AdapterKind.Ethernet,
        State = state, InterfaceIndex = 18, InterfaceIndexV6 = 18,
        Ipv4Address = IPAddress.Parse("192.168.0.51"), Ipv6Address = IPAddress.Parse("2001:db8::1"),
        Gateways = [IPAddress.Parse("192.168.0.1"), IPAddress.Parse("fe80::1")], DnsServers = [],
        LinkSpeedBps = 1_000_000_000, Ipv4Metric = 1, Ipv6Metric = 1
    };

    private static NetworkAdapter Wifi() => new()
    {
        Luid = WifiLuid, Guid = "{W}", Name = "Wi-Fi 2", Description = "Realtek 8851BU", Kind = AdapterKind.WiFi,
        State = AdapterState.Connected, InterfaceIndex = 2, InterfaceIndexV6 = 2,
        Ipv4Address = IPAddress.Parse("192.168.7.7"), Ipv6Address = null,
        Gateways = [IPAddress.Parse("192.168.7.1")], DnsServers = [],
        LinkSpeedBps = 143_000_000, Ipv4Metric = 50, Ipv6Metric = 50
    };

    private sealed class Fake(IReadOnlyList<NetworkAdapter> adapters) : IAdapterSource
    {
        public IReadOnlyList<NetworkAdapter> DiscoverAll() => adapters;
    }
}
