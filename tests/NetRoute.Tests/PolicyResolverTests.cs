using System.Net;
using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using Xunit;

namespace NetRoute.Tests;

public class PolicyResolverTests
{
    private const ulong EthernetLuid = 0x0006008001000000;
    private const ulong WifiLuid = 0x0047008000000000;

    /// <summary>
    /// §33. Repointing a role must move the apps assigned to that role and leave every
    /// other app alone. This is the behaviour the whole role indirection exists for, so
    /// it is asserted directly rather than assumed from the model's shape.
    /// </summary>
    [Fact]
    public void ChangingRoleAdapterMovesOnlyThatRolesApps()
    {
        var game = AppIdentity.ForExecutable(@"C:\Games\Halo.exe", "Halo Infinite");
        var steam = AppIdentity.ForExecutable(@"C:\Steam\steam.exe", "Steam");

        var config = new NetRouteConfig
        {
            RoleBindings =
            [
                new RoleBinding { Role = RoleId.Gaming, AdapterLuid = EthernetLuid },
                new RoleBinding { Role = RoleId.Downloads, AdapterLuid = WifiLuid }
            ],
            AppRules = [AppRule.Create(game, RoleId.Gaming), AppRule.Create(steam, RoleId.Downloads)]
        };

        var before = Resolve(config, Ethernet(), Wifi());
        Assert.Equal(EthernetLuid, Find(before, "Halo Infinite").ResolvedAdapter!.Luid);
        Assert.Equal(WifiLuid, Find(before, "Steam").ResolvedAdapter!.Luid);

        // The user repoints Gaming at Wi-Fi. Nothing about the app rules changes.
        var repointed = config with
        {
            RoleBindings =
            [
                new RoleBinding { Role = RoleId.Gaming, AdapterLuid = WifiLuid },
                new RoleBinding { Role = RoleId.Downloads, AdapterLuid = WifiLuid }
            ]
        };

        var after = Resolve(repointed, Ethernet(), Wifi());

        Assert.Equal(WifiLuid, Find(after, "Halo Infinite").ResolvedAdapter!.Luid);

        // Steam was never assigned to Gaming and must not have moved or changed mode.
        Assert.Equal(RoleId.Downloads, Find(after, "Steam").Rule.Role);
        Assert.Equal(WifiLuid, Find(after, "Steam").ResolvedAdapter!.Luid);
    }

    /// <summary>§15, §18, §39: a downed strict role blocks rather than falling back.</summary>
    [Fact]
    public void StrictRuleBlocksWhenAdapterGoesOffline()
    {
        var config = SingleApp(RoleId.Gaming, EthernetLuid, RoutingMode.Strict, killSwitch: true);

        var plan = Resolve(config, Ethernet(state: AdapterState.Disconnected), Wifi());
        var app = plan.Applications.Single();

        Assert.Equal(EnforcementAction.BlockAll, app.Action);
        Assert.Contains(app.Reasons, r => !r.Good && r.Text.Contains("Kill Switch"));

        // The failure mode this guards against is quietly resolving to the other
        // connected adapter, which would be a leak presented as success.
        Assert.DoesNotContain(plan.Applications, a => a.ResolvedAdapter?.Luid == WifiLuid);
    }

    /// <summary>§17: Preferred is allowed to fall back where Strict is not.</summary>
    [Fact]
    public void PreferredRuleFallsBackWhenAdapterGoesOffline()
    {
        var config = SingleApp(RoleId.Gaming, EthernetLuid, RoutingMode.Preferred, killSwitch: false);

        var plan = Resolve(config, Ethernet(state: AdapterState.Disconnected), Wifi());

        Assert.Equal(EnforcementAction.FallBackToWindows, plan.Applications.Single().Action);
    }

    /// <summary>
    /// §23. An IPv4-only role must actively block IPv6, because the alternative is
    /// IPv6 silently egressing via whichever adapter does have a v6 default route.
    /// </summary>
    [Fact]
    public void Ipv4OnlyRoleBlocksIpv6ToPreventBypass()
    {
        var config = SingleApp(RoleId.Downloads, WifiLuid, RoutingMode.Strict, killSwitch: true);

        var plan = Resolve(config, Ethernet(), Wifi());
        var app = plan.Applications.Single();

        Assert.Equal(EnforcementAction.PinToAdapter, app.Action);
        Assert.True(app.BlockIpv6);
        Assert.Contains(app.Reasons, r => r.Text.Contains("IPv6"));
    }

    /// <summary>A dual-stack role should enforce IPv6 rather than block it.</summary>
    [Fact]
    public void DualStackRoleEnforcesIpv6()
    {
        var config = SingleApp(RoleId.Gaming, EthernetLuid, RoutingMode.Strict, killSwitch: true);

        var app = Resolve(config, Ethernet(), Wifi()).Applications.Single();

        Assert.Equal(EnforcementAction.PinToAdapter, app.Action);
        Assert.False(app.BlockIpv6);
    }

    /// <summary>A role pointing at hardware that no longer exists must not silently resolve elsewhere.</summary>
    [Fact]
    public void MissingAdapterBlocksRatherThanResolvingElsewhere()
    {
        var config = SingleApp(RoleId.Gaming, adapterLuid: 0xDEADBEEF, RoutingMode.Strict, killSwitch: true);

        var app = Resolve(config, Ethernet(), Wifi()).Applications.Single();

        Assert.Equal(EnforcementAction.BlockAll, app.Action);
        Assert.Null(app.ResolvedAdapter);
    }

    [Fact]
    public void GlobalPauseStopsEnforcementWithoutLosingRules()
    {
        var config = SingleApp(RoleId.Gaming, EthernetLuid, RoutingMode.Strict, killSwitch: true)
            with { EnforcementPaused = true };

        var plan = Resolve(config, Ethernet(), Wifi());

        Assert.Equal(EnforcementAction.None, plan.Applications.Single().Action);
        Assert.Single(config.AppRules);
    }

    /// <summary>§30: every decision carries an explanation, including the failure cases.</summary>
    [Fact]
    public void EveryDecisionProducesReasons()
    {
        var config = SingleApp(RoleId.Gaming, EthernetLuid, RoutingMode.Strict, killSwitch: true);

        foreach (var ethernetState in new[] { AdapterState.Connected, AdapterState.Disconnected })
        {
            var app = Resolve(config, Ethernet(state: ethernetState), Wifi()).Applications.Single();
            Assert.NotEmpty(app.Reasons);
            Assert.All(app.Reasons, r => Assert.False(string.IsNullOrWhiteSpace(r.Text)));
        }
    }

    // ---- helpers ----

    private static EnforcementPlan Resolve(NetRouteConfig config, params NetworkAdapter[] adapters)
        => new PolicyResolver(new FakeAdapterSource(adapters)).Resolve(config);

    private static AppEnforcement Find(EnforcementPlan plan, string displayName)
        => plan.Applications.Single(a => a.Rule.App.DisplayName == displayName);

    private static NetRouteConfig SingleApp(
        RoleId role, ulong adapterLuid, RoutingMode mode, bool killSwitch)
    {
        var app = AppIdentity.ForExecutable(@"C:\Games\Halo.exe", "Halo Infinite");
        return new NetRouteConfig
        {
            RoleBindings = [new RoleBinding { Role = role, AdapterLuid = adapterLuid, LastKnownName = "Ethernet" }],
            AppRules = [AppRule.Create(app, role) with { Mode = mode, KillSwitch = killSwitch }]
        };
    }

    /// <summary>Dual-stack wired uplink.</summary>
    private static NetworkAdapter Ethernet(AdapterState state = AdapterState.Connected) => new()
    {
        Luid = EthernetLuid,
        Guid = "{BCCF9527-22F7-49AA-8013-CEC28E232D11}",
        Name = "Ethernet",
        Description = "Realtek PCIe GbE Family Controller",
        Kind = AdapterKind.Ethernet,
        State = state,
        InterfaceIndex = 21,
        InterfaceIndexV6 = 21,
        Ipv4Address = IPAddress.Parse("192.168.0.51"),
        Ipv6Address = IPAddress.Parse("2001:db8::1"),
        Gateways = [IPAddress.Parse("192.168.0.1"), IPAddress.Parse("fe80::1")],
        DnsServers = [],
        LinkSpeedBps = 1_000_000_000,
        Ipv4Metric = 1,
        Ipv6Metric = 25
    };

    /// <summary>IPv4-only wireless uplink, mirroring the real test machine.</summary>
    private static NetworkAdapter Wifi(AdapterState state = AdapterState.Connected) => new()
    {
        Luid = WifiLuid,
        Guid = "{0BB6FA80-3C6C-4E3E-899B-C100DB49F363}",
        Name = "Wi-Fi",
        Description = "TP-Link Wireless USB Adapter",
        Kind = AdapterKind.WiFi,
        State = state,
        InterfaceIndex = 3,
        InterfaceIndexV6 = 3,
        Ipv4Address = IPAddress.Parse("192.168.7.6"),
        Ipv6Address = null,
        Gateways = [IPAddress.Parse("192.168.7.1")],
        DnsServers = [],
        LinkSpeedBps = 600_000_000,
        Ipv4Metric = 50,
        Ipv6Metric = 50
    };

    private sealed class FakeAdapterSource(IReadOnlyList<NetworkAdapter> adapters) : IAdapterSource
    {
        public IReadOnlyList<NetworkAdapter> DiscoverAll() => adapters;
    }
}
