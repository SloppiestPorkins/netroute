using System.Net;
using System.Text.Json;
using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using NetRoute.Ipc;
using NetRoute.Service;

namespace NetRoute.Tests.Policy;

/// <summary>Findings 12 and 13: one setting where there were two, and a default route only moved when something needs it.</summary>
public class StrictCollapseAndRoutingTests
{
    private const ulong EthernetLuid = 1, WifiLuid = 2;

    [Theory]
    [InlineData(RoutingMode.Strict, EnforcementAction.BlockAll)]
    [InlineData(RoutingMode.Preferred, EnforcementAction.FallBackToWindows)]
    public void The_mode_alone_decides_whether_a_down_role_blocks_or_falls_back(RoutingMode mode, EnforcementAction expected)
    {
        var config = Config(RoleId.Gaming, mode);
        var plan = new PolicyResolver(new Fake([Ethernet(AdapterState.Disconnected), Wifi()])).Resolve(config);

        Assert.Equal(expected, plan.Applications.Single().Action);
    }

    [Fact]
    public void The_blocking_reason_names_strict_mode_and_nothing_that_can_be_off()
    {
        var plan = new PolicyResolver(new Fake([Ethernet(AdapterState.Disconnected), Wifi()])).Resolve(Config(RoleId.Gaming, RoutingMode.Strict));
        var reasons = string.Join(" ", plan.Applications.Single().Reasons.Select(r => r.Text));

        Assert.Contains("Strict mode", reasons);
        Assert.DoesNotContain("Kill Switch", reasons);
        Assert.DoesNotContain("while the network is down", reasons);   // the old, contradictory line
    }

    [Fact]
    public void Retired_rule_settings_are_not_written_or_sent()
    {
        var rule = AppRule.Create(AppIdentity.ForExecutable(@"C:\Games\Halo.exe", "Halo"), RoleId.Gaming);

        var json = JsonSerializer.Serialize(rule, IpcProtocol.JsonOptions);

        foreach (var retired in new[] { "KillSwitch", "EnforceIpv4", "EnforceTcp", "EnforceUdp", "IncludeRelatedProcesses" })
        {
            Assert.DoesNotContain(retired, json, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("EnforceIpv6", json, StringComparison.OrdinalIgnoreCase);   // the one that still means something
    }

    [Fact]
    public void A_config_written_before_the_collapse_still_loads_and_drops_the_old_fields_on_save()
    {
        var path = Path.Combine(Path.GetTempPath(), "nr-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // Write a current config, then add the fields an older NetRoute stored on each rule.
            var store = new ConfigStore(path);
            store.Save(Config(RoleId.Gaming, RoutingMode.Strict));
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            var rule = node["AppRules"]![0]!.AsObject();
            rule["KillSwitch"] = false;
            rule["EnforceIpv4"] = false;
            rule["EnforceTcp"] = false;
            rule["EnforceUdp"] = false;
            rule["IncludeRelatedProcesses"] = true;
            File.WriteAllText(path, node.ToJsonString());

            // The retired settings are simply ignored: an old config still loads, and what it
            // said about them no longer has anywhere to go. Strict is the whole of the meaning.
            var loaded = store.Load().AppRules.Single();
            Assert.Equal(RoutingMode.Strict, loaded.Mode);
            store.Save(store.Load());
            var rewritten = File.ReadAllText(path);

            Assert.DoesNotContain("KillSwitch", rewritten);
            Assert.DoesNotContain("EnforceTcp", rewritten);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- the default route (finding 12) ----

    [Fact]
    public void The_default_route_is_left_alone_until_an_app_is_assigned_to_gaming()
    {
        var config = Config(RoleId.Downloads, RoutingMode.Strict);   // Downloads rules only
        var adapters = new[] { Ethernet(), Wifi() };

        var redirect = RedirectPlanner.Build(config, new PolicyResolver(new Fake(adapters)).Resolve(config), adapters);

        Assert.False(redirect.ManageDefaultRoute);
        Assert.False(redirect.Split);
        Assert.Contains("No apps are assigned to Gaming", redirect.Reason);
    }

    [Fact]
    public void A_paused_gaming_app_does_not_move_the_default_route()
    {
        var config = Config(RoleId.Gaming, RoutingMode.Strict);
        config = config with { AppRules = config.AppRules.Select(r => r with { Paused = true }).ToList() };
        var adapters = new[] { Ethernet(), Wifi() };

        var redirect = RedirectPlanner.Build(config, new PolicyResolver(new Fake(adapters)).Resolve(config), adapters);

        Assert.False(redirect.ManageDefaultRoute);
    }

    [Fact]
    public void A_gaming_app_makes_downloads_the_default_route()
    {
        var config = Config(RoleId.Gaming, RoutingMode.Strict);
        var adapters = new[] { Ethernet(), Wifi() };

        var redirect = RedirectPlanner.Build(config, new PolicyResolver(new Fake(adapters)).Resolve(config), adapters);

        Assert.True(redirect.ManageDefaultRoute);
        Assert.Equal(WifiLuid, redirect.Downloads!.Luid);
    }

    [Fact]
    public void A_gaming_app_keeps_the_preference_while_its_connection_is_unplugged()
    {
        var config = Config(RoleId.Gaming, RoutingMode.Strict);
        var adapters = new[] { Ethernet(AdapterState.Disconnected), Wifi() };

        var redirect = RedirectPlanner.Build(config, new PolicyResolver(new Fake(adapters)).Resolve(config), adapters);

        Assert.True(redirect.ManageDefaultRoute);   // a pulled cable must not flip the default back and forth
        Assert.False(redirect.Split);
    }

    // ---- the self-test's claims (finding 10) ----

    [Fact]
    public void A_downloads_app_seen_on_downloads_does_not_pass_the_moved_step()
    {
        var input = new SelfTestInput(null, null, RedirectionAvailable: true, EnforcementPaused: false,
            VerifiedApps: ["Steam on Wi-Fi 2"], GamingVerifiedApps: []);

        var (state, text) = SelfTestRunner.JudgeMoving(input);

        Assert.Equal(SelfTestState.Warn, state);
        Assert.Contains("No Gaming app", text);
    }

    [Fact]
    public void A_gaming_app_seen_on_gaming_passes_the_moved_step()
    {
        var input = new SelfTestInput(null, null, true, false, ["Halo on Ethernet"], ["Halo on Ethernet"]);

        var (state, text) = SelfTestRunner.JudgeMoving(input);

        Assert.Equal(SelfTestState.Pass, state);
        Assert.Contains("Halo", text);
    }

    [Fact]
    public void An_engine_that_cannot_say_which_apps_are_gaming_apps_cannot_get_a_pass()
    {
        var input = new SelfTestInput(null, null, true, false, ["Steam on Wi-Fi 2"]);   // no Gaming list supplied

        Assert.Equal(SelfTestState.Warn, SelfTestRunner.JudgeMoving(input).State);
    }

    [Fact]
    public void A_dead_driver_fails_the_moved_step_and_a_pause_does_not_pass_it()
    {
        Assert.Equal(SelfTestState.Fail, SelfTestRunner.JudgeMoving(new SelfTestInput(null, null, false, false, [], [])).State);
        Assert.Equal(SelfTestState.Warn, SelfTestRunner.JudgeMoving(new SelfTestInput(null, null, true, true, ["x"], ["x"])).State);
    }

    [Fact]
    public void An_all_green_summary_says_what_it_does_not_prove()
    {
        var steps = Enumerable.Range(0, 4).Select(i => new SelfTestStepDto("s" + i, SelfTestState.Pass, null)).ToList();

        var summary = SelfTestRunner.Summarise(steps);

        Assert.DoesNotContain("Everything checks out", summary);
        Assert.Contains("would read the same without it", summary);
    }

    // ---- helpers ----

    private static NetRouteConfig Config(RoleId role, RoutingMode mode) => new()
    {
        SetupCompleted = true,
        RoleBindings =
        [
            new RoleBinding { Role = RoleId.Gaming, AdapterLuid = EthernetLuid, LastKnownName = "Ethernet" },
            new RoleBinding { Role = RoleId.Downloads, AdapterLuid = WifiLuid, LastKnownName = "Wi-Fi 2" }
        ],
        AppRules = [AppRule.Create(AppIdentity.ForExecutable(@"C:\Games\Halo.exe", "Halo"), role) with { Mode = mode }]
    };

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
