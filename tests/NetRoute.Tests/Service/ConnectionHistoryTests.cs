using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;
using NetRoute.Service;

namespace NetRoute.Tests.Service;

/// <summary>The name cache and reverse lookups: bounded in size, in concurrency, and in retries.</summary>
public class ConnectionHistoryTests
{
    private sealed class Connections : IConnectionSource
    {
        public List<ConnectionDto> Current { get; } = [];
        public IReadOnlyList<ConnectionDto> GetConnections() => Current;
        public void Add(string app, string address)
            => Current.Add(new ConnectionDto { ProcessId = 1, LocalEndpoint = "10.0.0.2:50000", ProcessName = app, RemoteEndpoint = $"{address}:443", Protocol = TransportProtocol.Tcp });
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static ConnectionHistory History(Connections connections, Func<IPAddress, CancellationToken, Task<string>> resolve, TimeProvider? time = null)
        => new(connections, NullLogger<ConnectionHistory>.Instance, resolve, time);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task Recording_alone_sends_no_lookups_and_asking_for_the_history_does()
    {
        var connections = new Connections();
        connections.Add("app", "203.0.113.9");
        var calls = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = History(connections, (_, _) => { Interlocked.Increment(ref calls); first.TrySetResult(); return Task.FromResult("cdn.example"); });

        history.Record();
        history.Record();
        Assert.Equal(0, Volatile.Read(ref calls));      // nobody is looking: nothing leaves the PC

        history.Recent(10);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Only_addresses_in_the_list_being_returned_are_looked_up()
    {
        var connections = new Connections();
        for (var i = 1; i <= 10; i++) connections.Add("app", $"203.0.113.{i}");
        var asked = new System.Collections.Concurrent.ConcurrentBag<string>();
        var history = History(connections, (address, _) => { asked.Add(address.ToString()); return Task.FromResult("h.example"); });

        history.Record();
        var shown = history.Recent(2);
        await Until(() => asked.Count == 2);
        await Until(() => history.Recent(2).All(r => r.Host == "h.example"));

        Assert.Equal(2, shown.Count);
        Assert.True(asked.Count == 2, $"{asked.Count} addresses were looked up for a list of 2");
    }

    [Fact]
    public async Task Addresses_on_your_own_network_are_never_looked_up()
    {
        var connections = new Connections();
        connections.Add("app", "192.168.0.5");
        var calls = 0;
        var history = History(connections, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult("x"); });

        history.Record();
        var row = history.Recent(10).Single();

        Assert.Equal("your network", row.Host);
        Assert.Equal(0, Volatile.Read(ref calls));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Only_a_few_lookups_run_at_once_and_the_rest_wait_for_a_later_request()
    {
        var connections = new Connections();
        for (var i = 1; i <= 40; i++) connections.Add("app", $"203.0.113.{i}");
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, peak = 0, total = 0;
        var history = History(connections, async (_, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Interlocked.Increment(ref total);
            try { return await release.Task; }
            finally { Interlocked.Decrement(ref running); }
        });

        history.Record();
        history.Recent(40);
        await Until(() => Volatile.Read(ref running) == 4);
        history.Recent(40);   // a second request while the first four are still out
        Assert.Equal(4, Volatile.Read(ref peak));
        Assert.Equal(4, Volatile.Read(ref total));

        release.SetResult("host.example");
        await Until(() => Volatile.Read(ref running) == 0);

        // The others get their turn on later requests.
        await Until(() => { history.Recent(40); return Volatile.Read(ref total) > 4; });
        Assert.True(Volatile.Read(ref peak) <= 4);
    }

    [Fact]
    public async Task A_failed_lookup_is_not_repeated_until_its_time_is_up()
    {
        var connections = new Connections();
        connections.Add("app", "203.0.113.9");
        var time = new ManualTime();
        var calls = 0;
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = History(connections, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            asked.TrySetResult();
            return Task.FromException<string>(new InvalidOperationException("resolver down"));
        }, time);

        history.Record();
        history.Recent(10);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Whether the failure has been written down yet or the lookup is still marked pending,
        // the address is not asked again inside the window, so no waiting is needed here.
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            history.Recent(10);
        }
        Assert.Equal(1, Volatile.Read(ref calls));

        time.Advance(ConnectionHistory.FailureTtl);
        await Until(() => { history.Recent(10); return Volatile.Read(ref calls) == 2; });
    }

    [Fact]
    public async Task A_successful_name_is_shown_and_kept()
    {
        var connections = new Connections();
        connections.Add("app", "203.0.113.9");
        var calls = 0;
        var history = History(connections, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult("cdn.example"); });

        history.Record();
        await Until(() => history.Recent(10).Single().Host == "cdn.example");
        history.Recent(10);
        history.Recent(10);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task The_name_cache_does_not_grow_past_its_cap()
    {
        var connections = new Connections();
        var calls = 0;
        var history = History(connections, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult("host.example"); });

        // Keep offering fresh addresses until well over the cache size has been looked up.
        var next = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Volatile.Read(ref calls) < 1600 && DateTime.UtcNow < deadline)
        {
            connections.Current.Clear();
            for (var i = 0; i < 20; i++, next++) connections.Add("app", $"203.0.{next / 250}.{next % 250 + 1}");
            history.Record();
            history.Recent(300);
            await Task.Yield();
        }
        history.Record();

        Assert.True(Volatile.Read(ref calls) >= 1600, "the test never got past the cache size");
        Assert.True(history.CachedNames <= 1000, $"{history.CachedNames} names cached");
    }

    [Fact]
    public void Sampling_slows_right_down_when_nobody_is_looking()
    {
        var time = new ManualTime();
        var history = History(new Connections(), (_, _) => Task.FromResult("x"), time);

        Assert.False(history.BeingRead);
        Assert.Equal(ConnectionHistory.IdleInterval, history.NextInterval());

        history.Recent(10);
        Assert.True(history.BeingRead);
        Assert.Equal(ConnectionHistory.BusyInterval, history.NextInterval());

        time.Advance(ConnectionHistory.BusyFor + TimeSpan.FromSeconds(1));
        Assert.False(history.BeingRead);
        Assert.Equal(ConnectionHistory.IdleInterval, history.NextInterval());
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
