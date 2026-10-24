using NetRoute.Core.Policy;
using NetRoute.Service;
using NetRoute.Tests.Service;
using NetRoute.Windows.Wfp;
using Xunit;

namespace NetRoute.Tests.Windows;

/// <summary>
/// A backend that could not open WFP (not ready at boot, another copy running) used to be a no-op for
/// the life of the process. These pin that it now opens when it can, and that the engine's own apply
/// backoff is what retries it.
/// </summary>
public sealed class RetryingBackendTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"NetRoute.RetryBackend.{Guid.NewGuid():N}");
    private readonly ManualTime _time = new();

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

    private sealed class Inner : IEnforcementBackend
    {
        public int Applies { get; private set; }
        public bool Disposed { get; private set; }
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool RedirectionAvailable => true;
        public BackendApplyResult Apply(EnforcementPlan plan) { Applies++; return BackendApplyResult.Success; }
        public void EmergencyDisable() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class NoDownloads : IDownloadSource
    {
        public IReadOnlyList<NetRoute.Ipc.DownloadActivityDto> InProgress() => [];
    }

    private static EnforcementPlan EmptyPlan() => new() { Applications = [], DegradedRoles = [] };

    [Fact]
    public void Opens_immediately_when_it_can()
    {
        var inner = new Inner();
        using var backend = new RetryingEnforcementBackend(() => inner);

        Assert.True(backend.IsAvailable);
        Assert.Null(backend.UnavailableReason);
        Assert.True(backend.RedirectionAvailable);
        backend.Apply(EmptyPlan());
        Assert.Equal(1, inner.Applies);
    }

    [Fact]
    public void Reports_the_reason_and_opens_on_a_later_apply()
    {
        var ready = false;
        var inner = new Inner();
        using var backend = new RetryingEnforcementBackend(() => ready
            ? inner
            : throw new AnotherInstanceException(["NetRoute.Service, process 7"]));

        Assert.False(backend.IsAvailable);
        Assert.Contains("Another copy of NetRoute is already running", backend.UnavailableReason);
        Assert.False(backend.RedirectionAvailable);
        backend.EmergencyDisable();   // nothing to disable, and must not throw

        backend.Apply(EmptyPlan());
        Assert.False(backend.IsAvailable);
        Assert.Equal(0, inner.Applies);

        ready = true;
        backend.Apply(EmptyPlan());
        Assert.True(backend.IsAvailable);
        Assert.Equal(1, inner.Applies);
        Assert.Null(backend.UnavailableReason);
    }

    [Fact]
    public void Disposing_releases_the_inner_backend()
    {
        var inner = new Inner();
        var backend = new RetryingEnforcementBackend(() => inner);
        backend.Dispose();
        Assert.True(inner.Disposed);
        Assert.False(backend.IsAvailable);
    }

    [Fact]
    public async Task The_engine_retries_an_unavailable_backend_on_its_backoff_and_then_enforces()
    {
        var ready = false;
        var inner = new Inner();
        var backend = new RetryingEnforcementBackend(() => ready ? inner : throw new WfpException("FwpmEngineOpen0", WfpException.RPC_S_SERVER_UNAVAILABLE));
        var adapters = new NetRouteEngineTests.MutableAdapterSource(NetRouteEngineTests.Ethernet(), NetRouteEngineTests.Wifi());
        using var engine = new NetRouteEngine(adapters, backend, Path.Combine(_directory, "config.json"), downloads: new NoDownloads(), time: _time);
        await engine.CompleteSetupAsync(11, 22);

        var status = await engine.GetStatusAsync();
        Assert.False(status.EnforcementActive);
        Assert.Contains("Base Filtering Engine", status.LastError?.FriendlyMessage);

        await engine.ReconcileAsync();
        Assert.Equal(0, inner.Applies);          // too soon: backoff, not a hot loop

        ready = true;
        _time.Advance(TimeSpan.FromSeconds(11));
        await engine.ReconcileAsync();

        Assert.Equal(1, inner.Applies);
        status = await engine.GetStatusAsync();
        Assert.True(status.EnforcementActive);
        Assert.Null(status.LastError);

        _time.Advance(TimeSpan.FromMinutes(10));
        await engine.ReconcileAsync();
        Assert.Equal(1, inner.Applies);          // settled: no further retries once it works
    }
}
