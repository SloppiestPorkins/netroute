using System.Net;
using System.Net.Sockets;
using NetRoute.Core.Adapters;
using NetRoute.Ipc;
using NetRoute.Windows.Traffic;

namespace NetRoute.Service;

/// <summary>What the test needs to know, gathered by the engine before the test starts.</summary>
public sealed record SelfTestInput(
    NetworkAdapter? Gaming,
    NetworkAdapter? Downloads,
    bool RedirectionAvailable,
    bool EnforcementPaused,
    IReadOnlyList<string> VerifiedApps);

/// <summary>
/// "Prove it": the test every split-tunnelling guide says to run and almost no tool runs for you.
///
/// <para>It answers the only questions that matter, with evidence rather than configuration:
/// does each connection reach the internet, do they come out on different lines, are apps really
/// being moved, and — the point of the whole product — does a download on Downloads leave the
/// gaming line's latency alone. That last one is bufferbloat, the actual mechanism behind "my
/// ping spikes when Steam downloads", measured here rather than asserted.</para>
///
/// <para>It makes real internet requests (api.ipify.org to see each line's public address, and
/// Cloudflare's speed-test endpoint for load), so it only ever runs when the user asks.</para>
/// </summary>
public sealed class SelfTestRunner(ILogger<SelfTestRunner> logger)
{
    private const string EgressUrl = "https://api.ipify.org";
    /// <summary>
    /// Public speed-test files, tried in order until one serves: 100 MB a time, repeated, which
    /// is big enough to fill a line and small enough that the servers hand it over. Several,
    /// because any one of them can be down, rate-limited or blocked from a given country.
    /// </summary>
    private static readonly string[] LoadUrls =
    [
        "https://speed.cloudflare.com/__down?bytes=104857600",
        "https://proof.ovh.net/files/100Mb.dat",
        "https://speed.hetzner.de/100MB.bin"
    ];
    private static readonly IPAddress PingTarget = IPAddress.Parse("1.1.1.1");

    private readonly object _gate = new();
    private SelfTestDto _state = new(false, null, [], "Not run yet.");
    private Task _run = Task.CompletedTask;

    public SelfTestDto State
    {
        get { lock (_gate) { return _state; } }
    }

    public SelfTestDto Start(SelfTestInput input)
    {
        lock (_gate)
        {
            if (!_run.IsCompleted)
            {
                return _state;
            }
            _state = new SelfTestDto(true, null,
            [
                new SelfTestStepDto("Each connection reaches the internet", SelfTestState.Pending, null),
                new SelfTestStepDto("They are separate lines", SelfTestState.Pending, null),
                new SelfTestStepDto("Apps are being moved", SelfTestState.Pending, null),
                new SelfTestStepDto("A download doesn't slow your gaming line", SelfTestState.Pending, null)
            ], "Testing. This takes about half a minute.");
            _run = Task.Run(() => RunAsync(input));
            return _state;
        }
    }

    private async Task RunAsync(SelfTestInput input)
    {
        try
        {
            var (gaming, downloads) = await Reachable(input);
            Separate(input, gaming, downloads);
            Moving(2, input);
            await UnderLoad(3, input);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Self-test failed.");
            Update(0, SelfTestState.Fail, "The test itself hit a problem: " + ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                var steps = _state.Steps;
                var failed = steps.Count(s => s.State == SelfTestState.Fail);
                var warned = steps.Count(s => s.State == SelfTestState.Warn);
                _state = _state with
                {
                    Running = false,
                    FinishedAt = DateTimeOffset.Now,
                    Summary = failed > 0 ? "Something isn't working. The failed steps say what."
                        : warned > 0 ? "Working, with notes. The amber steps explain."
                        : "Everything checks out."
                };
            }
        }
    }

    /// <summary>Step 1: the public address each connection comes out of, found by binding to it.</summary>
    private async Task<(string? Gaming, string? Downloads)> Reachable(SelfTestInput input)
    {
        Update(0, SelfTestState.Running, "Asking what the internet sees for each connection…");
        var gaming = input.Gaming?.Ipv4Address is { } g ? await PublicAddressAsync(g) : null;
        var downloads = input.Downloads?.Ipv4Address is { } d ? await PublicAddressAsync(d) : null;

        var said = new List<string>();
        if (input.Gaming is { } gamingAdapter)
        {
            said.Add($"{gamingAdapter.Name} comes out as {gaming ?? "nothing — no answer"}");
        }
        if (input.Downloads is { } downloadsAdapter)
        {
            said.Add($"{downloadsAdapter.Name} comes out as {downloads ?? "nothing — no answer"}");
        }

        Update(0, gaming is not null && downloads is not null ? SelfTestState.Pass
                : gaming is not null || downloads is not null ? SelfTestState.Warn
                : SelfTestState.Fail,
            said.Count == 0 ? "Neither role has a connection yet." : string.Join(". ", said) + ".");
        return (gaming, downloads);
    }

    /// <summary>Step 2: two public addresses means two real lines; one means a shared uplink.</summary>
    private void Separate(SelfTestInput input, string? gaming, string? downloads)
    {
        if (gaming is null || downloads is null)
        {
            Update(1, SelfTestState.Warn, "Can't compare: one connection didn't answer.");
            return;
        }
        if (input.Gaming?.Luid == input.Downloads?.Luid)
        {
            Update(1, SelfTestState.Warn, "Both roles are the same connection, so there's nothing to separate.");
            return;
        }
        Update(1, gaming == downloads ? SelfTestState.Warn : SelfTestState.Pass,
            gaming == downloads
                ? $"Both come out of {gaming}, so they share one uplink. NetRoute still keeps the apps apart, but they compete for the same line."
                : $"Different public addresses ({gaming} and {downloads}), so these really are two separate lines.");
    }

    /// <summary>Step 3: is anything actually being moved right now.</summary>
    private void Moving(int step, SelfTestInput input)
    {
        if (input.EnforcementPaused)
        {
            Update(step, SelfTestState.Warn, "Protection is paused, so nothing is being moved or kept in place.");
            return;
        }
        if (!input.RedirectionAvailable)
        {
            Update(step, SelfTestState.Fail, "The split-tunnel driver isn't running, so NetRoute can't move apps onto Gaming.");
            return;
        }
        Update(step, input.VerifiedApps.Count > 0 ? SelfTestState.Pass : SelfTestState.Warn,
            input.VerifiedApps.Count > 0
                ? "Verified from real traffic: " + string.Join(", ", input.VerifiedApps.Take(6)) + "."
                : "The driver is ready, but no app has sent traffic yet, so there's nothing to verify. Start a game or a download and run this again.");
    }

    /// <summary>Step 4: the whole point — a download on Downloads must not cost the gaming line its latency.</summary>
    private async Task UnderLoad(int step, SelfTestInput input)
    {
        if (input.Gaming?.Ipv4Address is not { } gaming || input.Downloads?.Ipv4Address is not { } downloads)
        {
            Update(step, SelfTestState.Warn, "Needs both connections to have an IPv4 address.");
            return;
        }

        Update(step, SelfTestState.Running, "Measuring the gaming line while it's quiet…");
        var (quiet, _) = await PingSeries(gaming, TimeSpan.FromSeconds(3));

        Update(step, SelfTestState.Running, "Downloading on Downloads and watching the gaming line…");
        using var stop = new CancellationTokenSource();
        var load = DownloadAsync(downloads, TimeSpan.FromSeconds(12), stop.Token);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var (loaded, worst) = await PingSeries(gaming, TimeSpan.FromSeconds(9));
        stop.Cancel();
        var (speed, problem) = await load;

        var sameLine = input.Gaming.Luid == input.Downloads.Luid;
        if (speed < 0.5)
        {
            Update(step, SelfTestState.Warn, problem is null
                ? $"Couldn't pull enough traffic to test ({speed:0.0} MB/s), so this proves nothing either way."
                : $"Couldn't load the connection, so this proves nothing either way: {problem}.");
            return;
        }
        if (quiet is not { } before || loaded is not { } after)
        {
            Update(step, SelfTestState.Warn, $"The gaming line didn't answer pings, so latency couldn't be compared. Downloads reached {speed:0.0} MB/s.");
            return;
        }

        // A few ms on a 10 ms wired link matters; the same few ms on a 120 ms Wi-Fi link is that
        // link's own jitter. Judge the rise against the line's idle latency, not a fixed number.
        var allowance = Math.Max(10, before / 4);
        var delta = after - before;
        if (delta <= allowance && !sameLine)
        {
            Update(step, SelfTestState.Pass,
                $"{input.Gaming.Name} stayed at {after} ms (worst {worst} ms; {before} ms when idle) while " +
                $"{input.Downloads.Name} pulled {speed:0.0} MB/s. That's the separation working: a download on one " +
                "connection can't take the other one's latency.");
            return;
        }
        Update(step, SelfTestState.Warn, sameLine
            ? $"Both roles are the same connection, so a {speed:0.0} MB/s download pushed ping from {before} to {after} ms " +
              $"(worst {worst} ms). That's bufferbloat. With one connection, \"Pause downloads while I play\" is the fix."
            : $"Ping on {input.Gaming.Name} rose from {before} to {after} ms (worst {worst} ms) while " +
              $"{input.Downloads.Name} pulled {speed:0.0} MB/s. They may share an uplink further upstream, or the " +
              "gaming line was busy with something else.");
    }

    private void Update(int step, SelfTestState state, string? detail)
    {
        lock (_gate)
        {
            var steps = _state.Steps.ToList();
            steps[step] = steps[step] with { State = state, Detail = detail ?? steps[step].Detail };
            _state = _state with { Steps = steps };
        }
    }

    /// <summary>The address the internet sees for one connection, by binding the socket to it.</summary>
    private async Task<string?> PublicAddressAsync(IPAddress local)
    {
        try
        {
            using var http = BoundClient(local, TimeSpan.FromSeconds(10));
            var text = await http.GetStringAsync(EgressUrl);
            return IPAddress.TryParse(text.Trim(), out var parsed) ? parsed.ToString() : null;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Egress probe failed for {Local}.", local);
            return null;
        }
    }

    /// <summary>
    /// Downloads and discards for <paramref name="duration"/>, returning MB/s and, when the load
    /// couldn't be generated, why. Requests are repeated until the time is up, so the line stays
    /// busy even when one response finishes early.
    /// </summary>
    private async Task<(double Speed, string? Problem)> DownloadAsync(IPAddress local, TimeSpan duration, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var buffer = new byte[128 * 1024];
        long total = 0;
        string? problem = null;
        try
        {
            using var http = BoundClient(local, duration + TimeSpan.FromSeconds(15));
            var url = LoadUrls[0];
            var remaining = LoadUrls.Skip(1).ToList();
            while (DateTimeOffset.UtcNow - started < duration && !ct.IsCancellationRequested)
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    problem = $"{new Uri(url).Host} answered {(int)response.StatusCode} {response.ReasonPhrase}";
                    if (remaining.Count == 0)
                    {
                        break;
                    }
                    url = remaining[0];      // that server won't serve us; try the next one
                    remaining.RemoveAt(0);
                    continue;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                while (DateTimeOffset.UtcNow - started < duration && !ct.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read <= 0)
                    {
                        break;   // this chunk finished; ask for another
                    }
                    total += read;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the pings finished and the load was stopped.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            problem = ex.Message;
        }
        var seconds = Math.Max(0.001, (DateTimeOffset.UtcNow - started).TotalSeconds);
        return (total / seconds / (1024 * 1024), total > 0 ? null : problem);
    }

    /// <summary>Median and worst round trip from one connection; nulls if nothing answered.</summary>
    private static async Task<(int? Median, int? Worst)> PingSeries(IPAddress local, TimeSpan duration)
    {
        var results = new List<int>();
        var until = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < until)
        {
            var reply = await Task.Run(() =>
            {
                try
                {
                    return LinkProbe.Ping(local, PingTarget, 1000);
                }
                catch (Exception)
                {
                    return null;
                }
            });
            if (reply is { } ms)
            {
                results.Add(ms);
            }
            await Task.Delay(200);
        }
        if (results.Count == 0)
        {
            return (null, null);
        }
        results.Sort();
        return (results[results.Count / 2], results[^1]);
    }

    private static HttpClient BoundClient(IPAddress local, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(local.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.Bind(new IPEndPoint(local, 0));   // this is what picks the connection
                    await socket.ConnectAsync(context.DnsEndPoint, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        // Some test servers refuse a request with no user agent (Cloudflare answers 403).
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetRoute/1.0 (self-test)");
        return client;
    }
}
