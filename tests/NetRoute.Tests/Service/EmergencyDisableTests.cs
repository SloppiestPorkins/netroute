using NetRoute.Core.Policy;
using NetRoute.Ipc;
using NetRoute.Service;

namespace NetRoute.Tests.Service;

/// <summary>
/// Emergency Disable is the escape hatch: it is what someone reaches for when their network is
/// broken, so it has to act immediately and has to stay off afterwards.
///
/// <para>Both of these were wrong (review of 23 October 2026, finding 2). It took the engine's own
/// gate, which is also held across reading config, resolving policy and route-table work, so it
/// queued behind a whole reconcile. And it saved the pause only after removal succeeded, so a
/// removal that threw left enforcement un-paused and the next tick simply put the filters back.</para>
/// </summary>
public sealed class EmergencyDisableTests : IDisposable
{
    private const string Game = @"C:\Games\Halo.exe";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"NetRoute.Emergency.{Guid.NewGuid():N}");
    private readonly NetRouteEngineTests.MutableAdapterSource _adapters = new(NetRouteEngineTests.Ethernet(), NetRouteEngineTests.Wifi());

    private string ConfigPath => Path.Combine(_directory, "config.json");

    private NetRouteEngine Engine(IEnforcementBackend backend)
        => new(_adapters, backend, ConfigPath, downloads: new NoDownloads());

    private static async Task SetUp(NetRouteEngine engine)
    {
        await engine.CompleteSetupAsync(11, 22);
        await engine.AddRuleAsync(AppIdentity.ForExecutable(Game, "Halo"), RoleId.Gaming);
    }

    [Fact]
    public async Task Filters_are_removed_without_waiting_for_an_apply_that_is_already_running()
    {
        // The backend holds its apply until released, standing in for a reconcile in flight.
        var backend = new BlockingBackend();
        using var engine = Engine(backend);
        await SetUp(engine);

        // Moving a role changes the plan, so this reconciles -- and blocks holding the gate.
        backend.Arm();
        var reconcile = Task.Run(() => engine.SetRoleAdapterAsync(RoleId.Gaming, 22));
        Assert.True(backend.ApplyStarted.Wait(TimeSpan.FromSeconds(5)), "the apply never started");

        // Removal must not be waiting on that apply. It waits only for the backend's own lock,
        // which this fake does not hold, so it completes while the apply is still blocked.
        var disable = engine.EmergencyDisableAsync();
        var finished = await Task.WhenAny(disable, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(disable, finished);
        Assert.True(backend.Disabled);

        backend.ReleaseApply();
        await reconcile;
    }

    [Fact]
    public async Task Enforcement_stays_paused_even_when_removal_fails()
    {
        var backend = new ThrowingDisableBackend();
        using var engine = Engine(backend);
        await SetUp(engine);

        // The caller is told it failed...
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.EmergencyDisableAsync());

        // ...and it is still recorded as paused, so the next tick does not put the filters back.
        var status = await engine.GetStatusAsync();
        Assert.True(status.EnforcementPaused);

        // The next reconcile does apply again -- an apply carrying nothing is how the filters come
        // off -- so what matters is that it enforces nothing, not that it does not run.
        await engine.ReconcileAsync();
        Assert.All(backend.LastPlan!.Applications, a => Assert.Equal(EnforcementAction.None, a.Action));
    }

    private sealed class BlockingBackend : IEnforcementBackend
    {
        private readonly ManualResetEventSlim _release = new(false);
        private volatile bool _armed;

        public ManualResetEventSlim ApplyStarted { get; } = new(false);
        public bool Disabled { get; private set; }
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool RedirectionAvailable => false;

        /// <summary>Starts blocking, so setting the test up doesn't deadlock on its own applies.</summary>
        public void Arm() => _armed = true;

        public BackendApplyResult Apply(EnforcementPlan plan)
        {
            if (_armed)
            {
                ApplyStarted.Set();
                _release.Wait(TimeSpan.FromSeconds(30));
            }
            return BackendApplyResult.Success;
        }

        public void ReleaseApply() => _release.Set();
        public void EmergencyDisable() => Disabled = true;

        public void Dispose()
        {
            _release.Dispose();
            ApplyStarted.Dispose();
        }
    }

    private sealed class ThrowingDisableBackend : IEnforcementBackend
    {
        public EnforcementPlan? LastPlan { get; private set; }
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool RedirectionAvailable => false;

        public BackendApplyResult Apply(EnforcementPlan plan)
        {
            LastPlan = plan;
            return BackendApplyResult.Success;
        }

        public void EmergencyDisable() => throw new InvalidOperationException("the engine refused");
        public void Dispose() { }
    }

    private sealed class NoDownloads : IDownloadSource
    {
        public IReadOnlyList<DownloadActivityDto> InProgress() => [];
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
