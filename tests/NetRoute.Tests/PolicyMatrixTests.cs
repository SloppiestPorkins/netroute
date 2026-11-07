using System.Net;
using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using Xunit;

namespace NetRoute.Tests;

/// <summary>
/// Exhaustive check of the routing decision over every combination of role, mode, adapter state,
/// and the three pauses. The expected result is written out independently of the resolver, so a
/// change to any rule shows up as a failing combination rather than slipping through.
/// </summary>
public class PolicyMatrixTests
{
    private const ulong EthernetLuid = 0x0006008001000000;
    private const ulong WifiLuid = 0x0047008000000000;

    private static readonly RoleId[] Roles = [RoleId.Gaming, RoleId.Downloads];
    private static readonly RoutingMode[] Modes = [RoutingMode.Strict, RoutingMode.Preferred, RoutingMode.Default];

    // Connected, not connected, and bound to an adapter that no longer exists (Missing).
    private static readonly AdapterState?[] States = [AdapterState.Connected, AdapterState.NoInternet, AdapterState.Disconnected, null];

    [Fact]
    public void EveryCombinationFollowsTheDocumentedRule()
    {
        var checkedCount = 0;
        foreach (var role in Roles)
        foreach (var mode in Modes)
        foreach (var state in States)
        foreach (var globalPaused in new[] { false, true })
        foreach (var rulePaused in new[] { false, true })
        foreach (var downloadsPaused in new[] { false, true })
        {
            var label = $"role={role} mode={mode} adapter={state?.ToString() ?? "missing"} " +
                        $"globalPause={globalPaused} rulePause={rulePaused} downloadsPause={downloadsPaused}";

            var adapters = state is { } s
                ? new[] { Adapter(role, s) }
                : Array.Empty<NetworkAdapter>();
            var config = Config(role, mode, rulePaused, globalPaused);
            var because = downloadsPaused && role == RoleId.Downloads ? "a game is running" : null;

            var plan = new PolicyResolver(new FakeAdapterSource(adapters)).Resolve(config, because);
            var app = plan.Applications.Single();
            var expected = Expected(role, mode, state, globalPaused, rulePaused, because is not null);

            Assert.True(expected == app.Action, $"{label}: expected {expected}, got {app.Action}");

            if (app.Action == EnforcementAction.PinToAdapter)
            {
                Assert.Equal(WifiLuidFor(role), app.ResolvedAdapter!.Luid);
            }
            else
            {
                // The bound adapter is still reported when it is present but not connected, so the
                // UI can say which network is down. It is only absent when the adapter is missing.
                if (state is null) Assert.Null(app.ResolvedAdapter);
                // A blocked app also has IPv6 blocked, so nothing leaks out over it. Otherwise it is not touched.
                Assert.Equal(app.Action == EnforcementAction.BlockAll, app.BlockIpv6);
            }

            Assert.NotEmpty(app.Reasons);
            checkedCount++;
        }

        Assert.Equal(2 * 3 * 4 * 2 * 2 * 2, checkedCount);
    }

    /// <summary>The rule the matrix checks against, written separately from the resolver.</summary>
    private static EnforcementAction Expected(
        RoleId role, RoutingMode mode, AdapterState? state,
        bool globalPaused, bool rulePaused, bool downloadsPaused)
    {
        if (globalPaused || rulePaused) return EnforcementAction.None;
        if (mode == RoutingMode.Default) return EnforcementAction.None;
        if (role == RoleId.Downloads && downloadsPaused) return EnforcementAction.BlockAll;
        if (state == AdapterState.Connected) return EnforcementAction.PinToAdapter;
        return mode == RoutingMode.Strict ? EnforcementAction.BlockAll : EnforcementAction.FallBackToWindows;
    }

    private static ulong WifiLuidFor(RoleId role) => role == RoleId.Downloads ? WifiLuid : EthernetLuid;

    private static NetRouteConfig Config(RoleId role, RoutingMode mode, bool rulePaused, bool globalPaused)
    {
        var app = AppIdentity.ForExecutable(@"C:\Games\Halo.exe", "Halo Infinite");
        return new NetRouteConfig
        {
            SetupCompleted = true,
            EnforcementPaused = globalPaused,
            RoleBindings =
            [
                new RoleBinding { Role = RoleId.Gaming, AdapterLuid = EthernetLuid, LastKnownName = "Ethernet" },
                new RoleBinding { Role = RoleId.Downloads, AdapterLuid = WifiLuid, LastKnownName = "Wi-Fi" }
            ],
            AppRules = [AppRule.Create(app, role) with { Mode = mode, Paused = rulePaused }]
        };
    }

    /// <summary>Adapter for the role's binding. Missing is modelled by returning no adapter at all.</summary>
    private static NetworkAdapter Adapter(RoleId role, AdapterState state)
    {
        var isWifi = role == RoleId.Downloads;
        return new NetworkAdapter
        {
            Luid = isWifi ? WifiLuid : EthernetLuid,
            Guid = isWifi ? "{0BB6FA80-3C6C-4E3E-899B-C100DB49F363}" : "{BCCF9527-22F7-49AA-8013-CEC28E232D11}",
            Name = isWifi ? "Wi-Fi" : "Ethernet",
            Description = "test adapter",
            Kind = isWifi ? AdapterKind.WiFi : AdapterKind.Ethernet,
            State = state,
            InterfaceIndex = 3,
            InterfaceIndexV6 = 3,
            Ipv4Address = IPAddress.Parse("192.168.0.51"),
            Ipv6Address = IPAddress.Parse("2001:db8::1"),
            Gateways = [IPAddress.Parse("192.168.0.1"), IPAddress.Parse("fe80::1")],
            DnsServers = [],
            LinkSpeedBps = 1_000_000_000,
            Ipv4Metric = 1,
            Ipv6Metric = 25
        };
    }

    private sealed class FakeAdapterSource(IReadOnlyList<NetworkAdapter> adapters) : IAdapterSource
    {
        public IReadOnlyList<NetworkAdapter> DiscoverAll() => adapters;
    }
}
