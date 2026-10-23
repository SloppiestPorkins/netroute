using NetRoute.Service;

namespace NetRoute.Tests.Service;

/// <summary>
/// The service loop's wait for a network change. The failure it replaces: after the first idle
/// timeout, later changes were handed to an abandoned waiter and woke nobody.
/// </summary>
public class WakeSignalTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_wait_that_times_out_reports_no_change()
        => Assert.False(await new WakeSignal().WaitAsync(Short, CancellationToken.None));

    [Fact]
    public async Task A_change_is_still_noticed_after_many_idle_timeouts()
    {
        var signal = new WakeSignal();
        for (var i = 0; i < 50; i++)
        {
            Assert.False(await signal.WaitAsync(Short, CancellationToken.None));
        }

        signal.Raise();

        // The long timeout means a lost wake-up would time this test out rather than pass.
        var woke = await signal.WaitAsync(Long, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(woke);
    }

    [Fact]
    public async Task A_change_that_arrives_during_a_wait_wakes_it()
    {
        var signal = new WakeSignal();
        var waiting = signal.WaitAsync(Long, CancellationToken.None);
        signal.Raise();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_burst_of_changes_is_one_wake_up()
    {
        var signal = new WakeSignal();
        signal.Raise();
        signal.Raise();
        signal.Raise();

        Assert.True(await signal.WaitAsync(Long, CancellationToken.None));
        Assert.False(await signal.WaitAsync(Short, CancellationToken.None));
        Assert.True(signal.LastRaised > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task Cancelling_stops_the_wait()
    {
        using var cts = new CancellationTokenSource();
        var waiting = new WakeSignal().WaitAsync(Long, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
