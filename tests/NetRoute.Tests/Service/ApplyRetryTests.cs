using System.Net;
using NetRoute.Core.Adapters;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;
using NetRoute.Service;

namespace NetRoute.Tests.Service;

/// <summary>
/// When the engine rebuilds filters, and when it tries again.
///
/// <para>These exist because of what the audit found: a rule whose filters failed to install was
/// never retried, so that app stayed unfiltered; and filters were rebuilt, killing live downloads,
/// for reasons that had nothing to do with them.</para>
/// </summary>
public sealed class ApplyRetryTests : IDisposable
{
    private const string Game = @"C:\Games\Halo.exe";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"NetRoute.Retry.{Guid.NewGuid():N}");
    private string ConfigPath => Path.Combine(_directory, "config.json");
    private readonly ManualTime _time = new();
    private readonly NetRouteEngineTests.MutableAdapterSource _adapters = new(NetRouteEngineTests.Ethernet(), NetRouteEngineTests.Wifi());

    private NetRouteEngine Engine(IEnforcementBackend backend, IConnectionSource? connections = null)
        => new(_adapters, backend, ConfigPath, connections: connections, downloads: new FakeDownloads(), time: _time);

    private static async Task SetUp(NetRouteEngine engine)
    {
        await engine.CompleteSetupAsync(11, 22);
        await engine.AddRuleAsync(AppIdentity.ForExecutable(Game, "Halo"), RoleId.Gaming);
    }

    [Fact]
    public async Task A_rule_that_failed_to_install_is_tried_again()
    {
        var backend = new ScriptedBackend { Fail = true };
        using var engine = Engine(backend);
        await SetUp(engine);
        var first = backend.ApplyCount;

        await engine.ReconcileAsync();
        Assert.Equal(first, backend.ApplyCount);   // too soon

        backend.Fail = false;
        _time.Advance(TimeSpan.FromSeconds(11));
        await engine.ReconcileAsync();

        Assert.Equal(first + 1, backend.ApplyCount);
        var status = await engine.GetStatusAsync();
        Assert.Null(status.LastError);
        Assert.True(status.EnforcementActive);
    }

    [Fact]
    public async Task A_rule_that_never_installs_settles_into_rare_retries_and_stays_reported()
    {
        var backend = new ScriptedBackend { Fail = true };
        using var engine = Engine(backend);
        await SetUp(engine);
        var first = backend.ApplyCount;

        // 35 minutes of 10 second ticks: retries after 10 s, 30 s, 2 min, then every 5 min.
        for (var i = 0; i < 210; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            await engine.ReconcileAsync();
        }

        Assert.InRange(backend.ApplyCount - first, 8, 10);
        var status = await engine.GetStatusAsync();
        Assert.NotNull(status.LastError);
        Assert.True(status.EnforcementActive);   // one failing rule is not the whole service being off
        Assert.Equal("1 rule couldn't be applied, so no app is being kept on its network.", status.Summary());
        Assert.NotNull(status.Apps.Single().Error);
    }

    /// <summary>The reported case: four stale browser paths made thirty working rules look switched off.</summary>
    [Fact]
    public async Task One_failed_rule_does_not_make_the_rest_look_off()
    {
        var backend = new ScriptedBackend { FailPath = @"C:\Tools\stale.exe" };
        using var engine = Engine(backend);
        await SetUp(engine);
        await engine.AddRuleAsync(AppIdentity.ForExecutable(@"C:\Tools\stale.exe", "Stale"), RoleId.Gaming);

        var status = await engine.GetStatusAsync();

        Assert.True(status.EnforcementActive);
        Assert.Equal("1 rule needs attention", status.Headline());
        Assert.Equal("1 rule couldn't be applied. The other rule is still enforced.", status.Summary());
        Assert.Equal("Stale", status.Failed().Single().Rule.App.DisplayName);
    }

    [Fact]
    public async Task A_failed_apply_is_logged_as_a_warning_and_a_routine_one_is_rate_limited()
    {
        var log = new CapturingLogger();
        var backend = new ScriptedBackend();
        using var engine = new NetRouteEngine(_adapters, backend, ConfigPath, downloads: new FakeDownloads(), time: _time, logger: log);
        await SetUp(engine);      // two applies in the same instant: setup, then the first rule
        Assert.Equal([Microsoft.Extensions.Logging.LogLevel.Information], log.Levels);

        _time.Advance(TimeSpan.FromMinutes(2));
        await engine.AddRuleAsync(AppIdentity.ForExecutable(@"C:\Tools\other.exe", "Other"), RoleId.Gaming);
        Assert.Equal(2, log.Levels.Count(l => l == Microsoft.Extensions.Logging.LogLevel.Information));

        backend.Fail = true;
        await engine.AddRuleAsync(AppIdentity.ForExecutable(@"C:\Tools\third.exe", "Third"), RoleId.Gaming);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, log.Levels[^1]);
    }

    [Fact]
    public async Task Changing_the_plan_ends_the_backoff()
    {
        var backend = new ScriptedBackend { Fail = true };
        using var engine = Engine(backend);
        await SetUp(engine);
        for (var i = 0; i < 40; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            await engine.ReconcileAsync();
        }
        var before = backend.ApplyCount;

        await engine.AddRuleAsync(AppIdentity.ForExecutable(@"C:\Tools\other.exe", "Other"), RoleId.Gaming);

        Assert.Equal(before + 1, backend.ApplyCount);
    }

    [Fact]
    public async Task A_new_address_on_the_same_adapter_does_not_rebuild_filters()
    {
        var backend = new ScriptedBackend();
        using var engine = Engine(backend);
        await SetUp(engine);
        var first = backend.ApplyCount;

        // A DHCP renewal and an IPv6 temporary-address rotation.
        _adapters.Adapters =
        [
            NetRouteEngineTests.Ethernet() with { Ipv4Address = IPAddress.Parse("192.168.1.77"), Ipv6Address = IPAddress.Parse("2001:db8::99") },
            NetRouteEngineTests.Wifi()
        ];
        await engine.ReconcileAsync();

        Assert.Equal(first, backend.ApplyCount);
    }

    [Fact]
    public async Task Losing_the_adapter_still_rebuilds_filters()
    {
        var backend = new ScriptedBackend();
        using var engine = Engine(backend);
        await SetUp(engine);
        var first = backend.ApplyCount;

        _adapters.Adapters = [NetRouteEngineTests.Ethernet(AdapterState.Disconnected), NetRouteEngineTests.Wifi()];
        await engine.ReconcileAsync();

        Assert.Equal(first + 1, backend.ApplyCount);
    }

    [Fact]
    public async Task A_game_whose_connections_come_and_go_keeps_downloads_paused_for_the_grace_period()
    {
        var connections = new FakeConnections { GameConnected = true };
        var backend = new ScriptedBackend();
        using var engine = Engine(backend, connections);
        await SetUp(engine);
        await engine.AddRuleAsync(AppIdentity.ForExecutable(@"C:\Steam\steam.exe", "Steam"), RoleId.Downloads);
        await engine.SetPauseDownloadsAsync(true);
        Assert.Equal(EnforcementAction.BlockAll, ActionFor(backend, "Steam"));
        var applies = backend.ApplyCount;

        // The game's last connection closes; a lobby, say.
        connections.GameConnected = false;
        _time.Advance(TimeSpan.FromSeconds(20));
        await engine.ReconcileAsync();
        Assert.Equal(applies, backend.ApplyCount);
        Assert.Equal(EnforcementAction.BlockAll, ActionFor(backend, "Steam"));

        // It reconnects in time, then drops again: the clock starts over from the last sighting.
        connections.GameConnected = true;
        _time.Advance(TimeSpan.FromSeconds(20));
        await engine.ReconcileAsync();
        connections.GameConnected = false;
        _time.Advance(TimeSpan.FromSeconds(40));
        await engine.ReconcileAsync();
        Assert.Equal(applies, backend.ApplyCount);

        // Gone for good.
        _time.Advance(TimeSpan.FromSeconds(10));
        await engine.ReconcileAsync();
        Assert.Equal(applies + 1, backend.ApplyCount);
        Assert.Equal(EnforcementAction.PinToAdapter, ActionFor(backend, "Steam"));
    }

    [Fact]
    public async Task Status_does_not_wait_for_steam_s_files()
    {
        var downloads = new BlockingDownloads();
        using var engine = new NetRouteEngine(_adapters, new ScriptedBackend(), ConfigPath, downloads: downloads, time: _time);
        try
        {
            var status = await engine.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(status.Downloading);

            // Emergency Disable takes the same gate and must not queue behind a stuck drive.
            await engine.EmergencyDisableAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            downloads.Release.Set();
        }
    }

    [Fact]
    public async Task Status_shows_the_background_answer_once_it_arrives()
    {
        var downloads = new BlockingDownloads();
        using var engine = new NetRouteEngine(_adapters, new ScriptedBackend(), ConfigPath, downloads: downloads, time: _time);
        await engine.GetStatusAsync();
        await downloads.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        downloads.Release.Set();

        // The refresh lands on another thread; poll briefly rather than sleep a fixed time.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IReadOnlyList<DownloadActivityDto> shown = [];
        while (shown.Count == 0 && DateTime.UtcNow < deadline)
        {
            shown = (await engine.GetStatusAsync()).Downloading;
            await Task.Delay(1);
        }
        Assert.Contains("Halo Infinite", shown.Single().ToString(), StringComparison.Ordinal);
    }

    private static EnforcementAction ActionFor(ScriptedBackend backend, string name)
        => backend.LastPlan!.Applications.Single(a => a.Rule.App.DisplayName == name).Action;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ScriptedBackend : IEnforcementBackend
    {
        public bool Fail { get; set; }

        /// <summary>Fails only the rule for this executable, leaving the others installed.</summary>
        public string? FailPath { get; set; }
        public int ApplyCount { get; private set; }
        public EnforcementPlan? LastPlan { get; private set; }
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool RedirectionAvailable => false;

        public BackendApplyResult Apply(EnforcementPlan plan)
        {
            ApplyCount++;
            LastPlan = plan;
            return Fail || FailPath is not null
                ? new(plan.Applications
                    .Where(a => FailPath is null || string.Equals(a.Rule.App.ExecutablePath, FailPath, StringComparison.OrdinalIgnoreCase))
                    .Select(a => new RuleEnforcementFailure(a.Rule.Id, new InvalidOperationException("not readable yet"))).ToList())
                : BackendApplyResult.Success;
        }

        public void EmergencyDisable() { }
        public void Dispose() { }
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<NetRouteEngine>
    {
        public List<Microsoft.Extensions.Logging.LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    private sealed class FakeConnections : IConnectionSource
    {
        public bool GameConnected { get; set; }
        public IReadOnlyList<ConnectionDto> GetConnections() => GameConnected
            ? [new ConnectionDto { ProcessId = 1, LocalEndpoint = "10.0.0.2:50000", ProcessName = "Halo", ExecutablePath = Game, RemoteEndpoint = "203.0.113.5:443", Protocol = TransportProtocol.Tcp }]
            : [];
    }

    private sealed class FakeDownloads : IDownloadSource
    {
        public IReadOnlyList<DownloadActivityDto> InProgress() => [];
    }

    private sealed class BlockingDownloads : IDownloadSource
    {
        public ManualResetEventSlim Release { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<DownloadActivityDto> InProgress()
        {
            Started.TrySetResult();
            Release.Wait();
            return [new DownloadActivityDto("Steam", "Halo Infinite", 1000)];
        }
    }
}
