using NetRoute.App;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;
using Xunit;

namespace NetRoute.Tests.Traffic;

public class LiveTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private const string GamePath = @"G:\Games\Halo\halo.exe";

    private static AppStatusDto Game(string expectedAdapter = "Ethernet", string? observed = "Ethernet", int connections = 6, bool paused = false,
        EnforcementAction action = EnforcementAction.PinToAdapter, RoleId role = RoleId.Gaming, string path = GamePath)
        => new()
        {
            Rule = AppRule.Create(AppIdentity.ForExecutable(path, "Halo"), role) with { Paused = paused },
            Action = action,
            Verification = VerificationState.Verified,
            ResolvedAdapterName = expectedAdapter,
            ObservedAdapterName = observed,
            ActiveConnections = connections,
            PreexistingConnections = 0,
            Reasons = [],
            VerificationSummary = ""
        };

    private static AppRateDto Rate(string adapter, double down, double up = 0, string path = GamePath,
        LatencyKind kind = LatencyKind.None, int? rtt = null, int? worst = null, int connections = 1)
        => new()
        {
            ProcessId = 1, ProcessName = "halo", ExecutablePath = path, InterfaceName = adapter,
            DownBytesPerSecond = down, UpBytesPerSecond = up,
            LatencyKind = kind, MedianRttMs = rtt, WorstRttMs = worst ?? rtt, RttConnections = rtt is null ? null : connections
        };

    private static AppRatesDto Rates(params AppRateDto[] rows) => new(true, null, rows);

    private static LiveApp One(LiveTracker t, AppStatusDto app, AppRatesDto? rates, double seconds)
        => t.Update([app], rates, T0.AddSeconds(seconds))[app.Rule.Id];

    [Fact]
    public void AnAppOnItsAssignedNetworkIsOnIt()
    {
        var app = Game();
        var live = One(new LiveTracker(), app, Rates(Rate("Ethernet", 100_000, 20_000)), 0);

        Assert.Equal(PlacementState.OnAssigned, live.Placement.State);
        Assert.Equal("On Ethernet", live.Headline);
        Assert.True(live.HasTraffic);
    }

    [Fact]
    public void AnAppOnTheWrongNetworkSaysSoInWordsThatNameBothNetworksAndTheRole()
    {
        var app = Game();
        var live = One(new LiveTracker(), app, Rates(Rate("Wi-Fi 2", 300_000)), 0);

        Assert.True(live.Placement.IsProblem);
        Assert.Equal("Using Wi-Fi 2, but assigned to Gaming (Ethernet)", live.Headline);
    }

    [Fact]
    public void ASingleOddReadingDoesNotFlashAWarningOnAnAppThatWasFine()
    {
        var app = Game();
        var t = new LiveTracker();
        One(t, app, Rates(Rate("Ethernet", 100_000)), 0);
        One(t, app, Rates(Rate("Ethernet", 100_000)), 2);

        var odd = One(t, app, Rates(Rate("Wi-Fi 2", 100_000)), 4);
        Assert.False(odd.Placement.IsProblem);

        var confirmed = One(t, app, Rates(Rate("Wi-Fi 2", 100_000)), 6);
        Assert.True(confirmed.Placement.IsProblem);
    }

    [Fact]
    public void AWarningDoesNotClearOnOneGoodReading()
    {
        var app = Game();
        var t = new LiveTracker();
        One(t, app, Rates(Rate("Wi-Fi 2", 100_000)), 0);
        One(t, app, Rates(Rate("Wi-Fi 2", 100_000)), 2);

        Assert.True(One(t, app, Rates(Rate("Ethernet", 100_000)), 4).Placement.IsProblem);
        Assert.False(One(t, app, Rates(Rate("Ethernet", 100_000)), 6).Placement.IsProblem);
    }

    [Fact]
    public void AnAppWithNoTrafficKeepsItsRowAsIdleRatherThanVanishing()
    {
        var app = Game(observed: null, connections: 0);
        var live = One(new LiveTracker(), app, Rates(), 0);

        Assert.Equal(PlacementState.Idle, live.Placement.State);
        Assert.False(live.HasTraffic);
        Assert.Equal("no traffic", live.RateText);
        Assert.Equal("no connections", live.ConnectionText);
        Assert.StartsWith("Idle", live.Headline);
    }

    [Fact]
    public void RatesAreSmoothedNotJumpedBetweenPolls()
    {
        var app = Game();
        var t = new LiveTracker();
        One(t, app, Rates(Rate("Ethernet", 1_000_000)), 0);
        var dip = One(t, app, Rates(Rate("Ethernet", 0)), 2);

        Assert.InRange(dip.Down, 200_000, 800_000);   // neither the raw 0 nor the old 1,000,000
    }

    [Fact]
    public void ARateThatStopsFadesAndTheAppGoesIdle()
    {
        var app = Game(observed: null, connections: 0);
        var t = new LiveTracker();
        One(t, app, Rates(Rate("Ethernet", 1_000_000)), 0);

        var later = One(t, app, Rates(), 120);
        Assert.False(later.HasTraffic);
        Assert.Equal(0, later.Down);
    }

    [Fact]
    public void AppsAreMatchedToTheirRulesByProgramNotByName()
    {
        var app = Game();
        var other = Rate("Wi-Fi 2", 5_000_000, path: @"C:\Other\halo.exe");   // same name, different program
        var live = One(new LiveTracker(), app, Rates(Rate("Ethernet", 50_000), other), 0);

        Assert.Equal(PlacementState.OnAssigned, live.Placement.State);
        Assert.InRange(live.Down, 49_000, 51_000);
    }

    [Fact]
    public void PausedAppsAndDefaultRoleAppsHaveNothingToGetWrong()
    {
        var paused = One(new LiveTracker(), Game(paused: true), Rates(Rate("Wi-Fi 2", 300_000)), 0);
        Assert.Equal(PlacementState.Unmanaged, paused.Placement.State);
        Assert.False(paused.Placement.IsProblem);

        var notEnforced = One(new LiveTracker(), Game(action: EnforcementAction.None, role: RoleId.Default), Rates(Rate("Wi-Fi 2", 300_000)), 0);
        Assert.False(notEnforced.Placement.IsProblem);
        Assert.Contains("Not placed by NetRoute", notEnforced.Headline);
    }

    [Fact]
    public void WithoutRatesTheRowFallsBackToWhatTheConnectionsSay()
    {
        var wrong = One(new LiveTracker(), Game(observed: "Wi-Fi 2"), null, 0);
        Assert.True(wrong.Placement.IsProblem);

        var fine = One(new LiveTracker(), Game(), new AppRatesDto(false, "ETW unavailable", []), 0);
        Assert.Equal(PlacementState.OnAssigned, fine.Placement.State);
    }

    // ---- latency ----

    [Fact]
    public void ATcpFigureIsShownPlainlyWithoutAQualifier()
    {
        var live = One(new LiveTracker(), Game(), Rates(Rate("Ethernet", 100_000, kind: LatencyKind.Tcp, rtt: 14, worst: 14)), 0);

        Assert.Equal("14 ms", live.Latency);
        Assert.Equal(LatencyKind.Tcp, live.LatencyKind);
    }

    [Fact]
    public void APeerPingIsLabelledAsAServerPingAndExplainedInTheTooltip()
    {
        var live = One(new LiveTracker(), Game(), Rates(Rate("Ethernet", 100_000, kind: LatencyKind.PeerProbe, rtt: 22, connections: 2)), 0);

        Assert.Equal("22 ms · server ping", live.Latency);
        Assert.Equal(LatencyKind.PeerProbe, live.LatencyKind);
        Assert.Contains("not the game's own packets", live.LatencyTip);
        Assert.Contains("2 servers", live.LatencyTip);
    }

    [Fact]
    public void WhenNothingAnswersTheRowSaysNotMeasurableAndShowsNoNumber()
    {
        var live = One(new LiveTracker(), Game(), Rates(Rate("Ethernet", 100_000, kind: LatencyKind.NotMeasurable)), 0);

        Assert.Equal("latency: not measurable", live.Latency);
        Assert.DoesNotContain("ms", live.Latency);
        Assert.Equal(LatencyKind.NotMeasurable, live.LatencyKind);
    }

    [Fact]
    public void NoMeasurementYetIsSilenceNotAZero()
    {
        var live = One(new LiveTracker(), Game(), Rates(Rate("Ethernet", 100_000)), 0);
        Assert.Null(live.Latency);
    }

    [Fact]
    public void AFigureIsHeldForABriefGapThenDropsSoItIsNeverStale()
    {
        var app = Game();
        var t = new LiveTracker();
        One(t, app, Rates(Rate("Ethernet", 100_000, kind: LatencyKind.Tcp, rtt: 14)), 0);

        Assert.Equal("14 ms", One(t, app, Rates(Rate("Ethernet", 100_000)), 6).Latency);     // bridged
        Assert.Null(One(t, app, Rates(Rate("Ethernet", 100_000)), 20).Latency);               // gone, not shown as current
    }

    [Fact]
    public void TcpOutranksAPeerPingWhenAProgramHasBoth()
    {
        var rows = Rates(
            Rate("Ethernet", 100_000, kind: LatencyKind.PeerProbe, rtt: 9),
            Rate("Ethernet", 100_000, kind: LatencyKind.Tcp, rtt: 30));
        var live = One(new LiveTracker(), Game(), rows, 0);

        Assert.Equal(LatencyKind.Tcp, live.LatencyKind);
        Assert.Equal("30 ms", live.Latency);
    }
}
