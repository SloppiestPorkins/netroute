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
    public async Task Only_a_few_lookups_run_at_once_and_the_rest_wait_for_a_later_pass()
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
        await Until(() => Volatile.Read(ref running) == 4);
        history.Record();   // a second pass while the first four are still out
        Assert.Equal(4, Volatile.Read(ref peak));
        Assert.Equal(4, Volatile.Read(ref total));

        release.SetResult("host.example");
        await Until(() => Volatile.Read(ref running) == 0);

        // The others get their turn on later passes (a pass can land just before a slot frees).
        await Until(() => { history.Record(); return Volatile.Read(ref total) > 4; });
        Assert.True(Volatile.Read(ref peak) <= 4);
    }

    [Fact]
    public async Task A_failed_lookup_is_not_repeated_until_its_time_is_up()
    {
        var connections = new Connections();
        connections.Add("app", "203.0.113.9");
        var time = new ManualTime();
        var calls = 0;
        var history = History(connections, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromException<string>(new InvalidOperationException("resolver down"));
        }, time);

        history.Record();
        await Until(() => Volatile.Read(ref calls) == 1);
        await Task.Delay(50);   // let the failure be recorded

        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            history.Record();
        }
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref calls));

        time.Advance(ConnectionHistory.FailureTtl);
        history.Record();
        await Until(() => Volatile.Read(ref calls) == 2);
    }

    [Fact]
    public async Task A_successful_name_is_shown_and_kept()
    {
        var connections = new Connections();
        connections.Add("app", "203.0.113.9");
        var calls = 0;
        var history = History(connections, (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult("cdn.example"); });

        history.Record();
        await Until(() => history.Recent(10).Single().ToString()!.Contains("cdn.example", StringComparison.Ordinal));
        history.Record();
        history.Record();

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
            await Task.Delay(1);
        }
        await Task.Delay(50);
        history.Record();

        Assert.True(Volatile.Read(ref calls) >= 1600, "the test never got past the cache size");
        Assert.True(history.CachedNames <= 1000, $"{history.CachedNames} names cached");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
