using NetRoute.App;
using NetRoute.Core.Adapters;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;

namespace NetRoute.Tests.Policy;

/// <summary>
/// What the window may claim (findings 11, 12, 13, 15). Each test names the evidence a word needs:
/// "Verified" needs data seen moving, "Held" is the rule in force, "Protected" needs something seen.
/// </summary>
public class HonestStatusTests
{
    private const ulong EthernetLuid = 1, WifiLuid = 2;

    // ---- the per-app badge ----

    [Fact]
    public void A_socket_with_no_data_moving_is_not_verified()
    {
        var badge = StatusWording.ForApp(App(VerificationState.Verified), Live(PlacementState.Idle, fromTraffic: false));

        Assert.Equal("Unconfirmed", badge.Text);
        Assert.NotEqual(Tone.Good, badge.Tone);
        Assert.Contains("isn't confirmed", badge.Summary);
    }

    [Fact]
    public void With_no_live_reading_at_all_it_is_not_verified_either()
        => Assert.Equal("Unconfirmed", StatusWording.ForApp(App(VerificationState.Verified), null).Text);

    [Fact]
    public void Data_seen_moving_on_the_assigned_connection_is_verified()
    {
        var badge = StatusWording.ForApp(App(VerificationState.Verified), Live(PlacementState.OnAssigned, fromTraffic: true));

        Assert.Equal("Verified", badge.Text);
        Assert.Equal(Tone.Good, badge.Tone);
    }

    [Theory]
    [InlineData(PlacementState.Mismatch, "Off target", Tone.Bad)]
    [InlineData(PlacementState.Split, "Partly off", Tone.Warn)]
    public void Bytes_on_the_wrong_connection_overrule_a_socket_that_looked_right(PlacementState state, string text, Tone tone)
    {
        var badge = StatusWording.ForApp(App(VerificationState.Verified), Live(state, fromTraffic: true));

        Assert.Equal(text, badge.Text);
        Assert.Equal(tone, badge.Tone);
    }

    [Fact]
    public void A_state_that_comes_from_the_plan_alone_says_so()
    {
        var badge = StatusWording.ForApp(App(VerificationState.Blocked), null);

        Assert.Equal("Held", badge.Text);
        Assert.DoesNotContain("Kill Switch", badge.Summary);
        Assert.Contains("rule in force", badge.Summary);
        Assert.Contains("doesn't count refused connections", badge.Summary);
    }

    [Fact]
    public void An_unmanaged_app_is_not_called_windows_routing()
        => Assert.Equal("Not managed", StatusWording.ForApp(App(VerificationState.NotEnforced), null).Text);

    // ---- the header ----

    [Fact]
    public void Rules_with_nothing_seen_do_not_earn_a_green_protected()
    {
        var (text, tone) = StatusWording.Header(Status(App(VerificationState.Configured)));

        Assert.NotEqual("Protected", text);
        Assert.NotEqual(Tone.Good, tone);
    }

    [Fact]
    public void Something_seen_on_its_assigned_connection_earns_protected()
        => Assert.Equal(("Protected", Tone.Good), StatusWording.Header(Status(App(VerificationState.Verified))));

    [Fact]
    public void No_rules_at_all_is_not_protected()
    {
        var (text, tone) = StatusWording.Header(Status());

        Assert.NotEqual("Protected", text);
        Assert.Equal(Tone.Muted, tone);
    }

    [Fact]
    public void Gaming_apps_with_the_driver_down_are_amber_not_green()
    {
        var (text, tone) = StatusWording.Header(Status(App(VerificationState.Verified)) with { RedirectionAvailable = false });

        Assert.Equal(Tone.Warn, tone);
        Assert.NotEqual("Protected", text);
    }

    [Fact]
    public void Held_apps_are_red_and_say_held_not_blocked_by_a_switch()
    {
        var (text, tone) = StatusWording.Header(Status(App(VerificationState.Blocked, EnforcementAction.BlockAll)));

        Assert.Equal(("Apps held", Tone.Bad), (text, tone));
    }

    // ---- Default ----

    [Fact]
    public void Default_says_downloads_while_a_gaming_app_exists()
        => Assert.Equal("Downloads line, unless you add it", StatusWording.DefaultSubtitle(Status(App(VerificationState.Verified))));

    [Fact]
    public void Default_says_windows_decides_when_nothing_is_on_gaming()
        => Assert.Equal("Windows decides", StatusWording.DefaultSubtitle(Status()));

    [Fact]
    public void Default_says_windows_decides_while_paused()
        => Assert.Equal("Windows decides", StatusWording.DefaultSubtitle(Status(App(VerificationState.Verified)) with { EnforcementPaused = true }));

    [Fact]
    public void The_ui_wording_agrees_with_the_planner_about_when_the_default_route_is_managed()
    {
        // The same scenario through both: they must not drift apart.
        foreach (var gamingRule in new[] { true, false })
        {
            var rules = gamingRule ? new[] { App(VerificationState.Verified) } : [];
            var status = Status(rules);
            var planner = RedirectPlanner.Build(Config(gamingRule), PlanFor(gamingRule), [Ethernet(), Wifi()]);

            Assert.Equal(planner.ManageDefaultRoute, StatusWording.ManagesDefaultRoute(status));
        }
    }

    // ---- messages that point at nothing ----

    [Fact]
    public void No_message_sends_anyone_to_a_script_the_installer_does_not_carry()
    {
        var dir = AppSourceFolder();
        var offenders = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml"))
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"RUN-NETROUTE|INSTALL-NETROUTE|\.cmd\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_repair_advice_names_the_installer_not_a_file_in_the_repository()
    {
        Assert.Contains("installer", StatusWording.RepairAdvice);
        Assert.Contains("Repair", StatusWording.RepairAdvice);
        Assert.Contains(StatusWording.RepairAdvice, StatusWording.DriverDown);
    }

    [Fact]
    public void The_update_settings_note_says_administrator_plainly()
        => Assert.Contains("administrator", StatusWording.UpdateSettingsNeedAdmin);

    [Fact]
    public void A_role_that_is_down_does_not_claim_a_kill_switch_acts_on_every_app()
    {
        var note = StatusWording.RoleOfflineNote(RoleId.Gaming, 3);

        Assert.DoesNotContain("Kill Switch", note);
        Assert.Contains("Strict", note);
        Assert.Contains("fall back", note);
    }

    // ---- a cut list never looks complete ----

    [Fact]
    public void A_complete_connection_list_has_no_note()
        => Assert.Null(ConnectionListNote.For([Row(null), Row(null)]));

    [Fact]
    public void A_cut_connection_list_says_how_much_and_what_was_left_out()
    {
        var note = ConnectionListNote.For([Row(1800), Row(1800)]);

        Assert.NotNull(note);
        Assert.Contains("2 of 1800", note);
        Assert.Contains("idle", note);
    }

    [Fact]
    public void An_empty_list_has_no_note()
        => Assert.Null(ConnectionListNote.For([]));

    // ---- helpers ----

    private static ConnectionDto Row(int? total) => new()
    {
        ProcessId = 1, ProcessName = "x", Protocol = TransportProtocol.Tcp, LocalEndpoint = "10.0.0.2:1", TotalBeforeCut = total
    };

    private static string AppSourceFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "NetRoute.App")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repository root not found."), "src", "NetRoute.App");
    }

    private static LiveApp Live(PlacementState state, bool fromTraffic) => new(
        new PlacementVerdict(state, state == PlacementState.OnAssigned ? "Ethernet" : "Wi-Fi 2",
            state is PlacementState.Mismatch or PlacementState.Split ? new[] { "Wi-Fi 2" } : Array.Empty<string>(), fromTraffic),
        "headline", 1000, 1000, 1, null, null, LatencyKind.None, fromTraffic);

    private static AppStatusDto App(VerificationState verification, EnforcementAction action = EnforcementAction.PinToAdapter) => new()
    {
        Rule = AppRule.Create(AppIdentity.ForExecutable(@"C:\Games\Halo.exe", "Halo Infinite"), RoleId.Gaming),
        Action = action,
        Verification = verification,
        ResolvedAdapterName = "Ethernet",
        ObservedAdapterName = "Ethernet",
        ActiveConnections = 1,
        PreexistingConnections = 0,
        Reasons = [],
        VerificationSummary = "summary from the service"
    };

    private static ServiceStatusDto Status(params AppStatusDto[] apps) => new()
    {
        ProtocolVersion = 1,
        SetupCompleted = true,
        EnforcementActive = true,
        EnforcementPaused = false,
        RedirectionAvailable = true,
        Roles =
        [
            new RoleStatusDto { Role = RoleId.Gaming, Adapter = Dto(Ethernet()), Health = RoleHealth.Connected, AssignedApps = apps.Length, Ipv4Only = false },
            new RoleStatusDto { Role = RoleId.Downloads, Adapter = Dto(Wifi()), Health = RoleHealth.Connected, AssignedApps = 0, Ipv4Only = true }
        ],
        Apps = apps,
        RecentLeaks = [],
        RecentEvents = [],
        GeneratedAt = DateTimeOffset.UnixEpoch
    };

    private static AdapterDto Dto(NetworkAdapter a) => AdapterDto.From(a);

    private static NetRoute.Core.Config.NetRouteConfig Config(bool gamingRule) => new()
    {
        SetupCompleted = true,
        RoleBindings =
        [
            new RoleBinding { Role = RoleId.Gaming, AdapterLuid = EthernetLuid },
            new RoleBinding { Role = RoleId.Downloads, AdapterLuid = WifiLuid }
        ],
        AppRules = gamingRule ? [AppRule.Create(AppIdentity.ForExecutable(@"C:\Games\Halo.exe", "Halo Infinite"), RoleId.Gaming)] : []
    };

    private static EnforcementPlan PlanFor(bool gamingRule)
        => new PolicyResolver(new Fake([Ethernet(), Wifi()])).Resolve(Config(gamingRule));

    private static NetworkAdapter Ethernet() => new()
    {
        Luid = EthernetLuid, Guid = "{E}", Name = "Ethernet", Description = "Realtek", Kind = AdapterKind.Ethernet,
        State = AdapterState.Connected, InterfaceIndex = 18, InterfaceIndexV6 = 18,
        Ipv4Address = System.Net.IPAddress.Parse("192.168.0.51"), Ipv6Address = System.Net.IPAddress.Parse("2001:db8::1"),
        Gateways = [System.Net.IPAddress.Parse("192.168.0.1"), System.Net.IPAddress.Parse("fe80::1")], DnsServers = [],
        LinkSpeedBps = 1_000_000_000, Ipv4Metric = 1, Ipv6Metric = 1
    };

    private static NetworkAdapter Wifi() => new()
    {
        Luid = WifiLuid, Guid = "{W}", Name = "Wi-Fi 2", Description = "Realtek 8851BU", Kind = AdapterKind.WiFi,
        State = AdapterState.Connected, InterfaceIndex = 2, InterfaceIndexV6 = 2,
        Ipv4Address = System.Net.IPAddress.Parse("192.168.7.7"), Ipv6Address = null,
        Gateways = [System.Net.IPAddress.Parse("192.168.7.1")], DnsServers = [],
        LinkSpeedBps = 143_000_000, Ipv4Metric = 50, Ipv6Metric = 50
    };

    private sealed class Fake(IReadOnlyList<NetworkAdapter> adapters) : IAdapterSource
    {
        public IReadOnlyList<NetworkAdapter> DiscoverAll() => adapters;
    }
}
