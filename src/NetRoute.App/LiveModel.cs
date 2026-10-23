using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;

namespace NetRoute.App;

/// <summary>
/// What the main window shows about one assigned app, right now. Plain data with no WPF types, so
/// the decisions behind it (is it on the right line, what to say about its latency) are tested.
/// </summary>
/// <param name="Placement">Where the app really is against where it was assigned.</param>
/// <param name="Headline">One line saying so, e.g. "Using Wi-Fi 2, but assigned to Gaming (Ethernet)".</param>
/// <param name="Latency">Text for the latency column; null when there is nothing to say (idle, or not measured yet).</param>
/// <param name="LatencyTip">What that figure is and isn't, for a tooltip.</param>
/// <param name="HasTraffic">Data is moving. False for an idle row, which stays in the list.</param>
public sealed record LiveApp(
    PlacementVerdict Placement,
    string Headline,
    double Down,
    double Up,
    int Connections,
    string? Latency,
    string? LatencyTip,
    LatencyKind LatencyKind,
    bool HasTraffic)
{
    public string RateText => HasTraffic ? $"↓ {Format.Rate(Down)}   ↑ {Format.Rate(Up)}" : "no traffic";

    public string ConnectionText => Connections switch
    {
        0 => "no connections",
        1 => "1 connection",
        var n => $"{n} connections"
    };
}

/// <summary>
/// Builds <see cref="LiveApp"/> for each assigned app from the service's status and per-app rates,
/// and keeps what makes a live view readable: smoothed rates, a held latency figure, and a warning
/// that has to persist across readings before it appears or goes.
///
/// <para>One instance lives as long as the window; call <see cref="Update"/> once per poll. It
/// remembers state per rule, so an app that is idle between two polls keeps its row and its place
/// rather than vanishing.</para>
/// </summary>
public sealed class LiveTracker
{
    /// <summary>How long a latency figure is shown after its last sample. Longer than the service's own cache interval, shorter than a number that has gone stale.</summary>
    public static readonly TimeSpan LatencyHold = TimeSpan.FromSeconds(10);

    private readonly RateSmoother _rates = new();
    private readonly ValueHold<(RttSummary Rtt, LatencyKind Kind)> _latency = new(LatencyHold);
    private readonly Debounce _problem = new(2);
    private readonly Dictionary<(string Id, bool Problem), PlacementVerdict> _lastVerdict = [];

    /// <param name="rates">Null when the service couldn't supply them; rows then fall back to connection data alone.</param>
    public IReadOnlyDictionary<Guid, LiveApp> Update(IReadOnlyList<AppStatusDto> apps, AppRatesDto? rates, DateTimeOffset now)
    {
        var measured = rates is { Available: true };
        var result = new Dictionary<Guid, LiveApp>();
        foreach (var app in apps)
        {
            var id = app.Rule.Id.ToString();
            var mine = measured
                ? rates!.Rates.Where(r => AppMatching.Covers(app.Rule.App, r.ExecutablePath, r.PackageFamilyName)).ToList()
                : [];

            var (down, up) = _rates.Update(id, mine.Sum(r => r.DownBytesPerSecond), mine.Sum(r => r.UpBytesPerSecond), now);
            var expected = app.Action == EnforcementAction.PinToAdapter && !app.Rule.Paused ? app.ResolvedAdapterName : null;

            // Bytes decide when there are any. Placement is judged on the raw rows rather than the
            // smoothed total, because smoothing's tail would keep accusing a network the app has left.
            var verdict = PlacementCheck.Evaluate(expected,
                mine.Select(r => new AdapterTraffic(r.InterfaceName, r.DownBytesPerSecond + r.UpBytesPerSecond)).ToList(),
                app.ObservedAdapterName, app.ActiveConnections);
            verdict = Believed(id, verdict);

            var (latency, tip, kind) = DescribeLatency(id, mine, now);
            var hasTraffic = down + up >= RateSmoother.Floor;
            result[app.Rule.Id] = new LiveApp(verdict, Headline(app, verdict, expected, hasTraffic), down, up,
                app.ActiveConnections, latency, tip, kind, hasTraffic);
        }

        var ids = apps.Select(a => a.Rule.Id.ToString()).ToList();
        _rates.Keep(ids);
        _latency.Keep(ids);
        _problem.Keep(ids);
        foreach (var gone in _lastVerdict.Keys.Where(k => !ids.Contains(k.Id)).ToList())
        {
            _lastVerdict.Remove(gone);
        }
        return result;
    }

    /// <summary>
    /// A change between "fine" and "problem" is shown only once it has held for two readings. In the
    /// meantime the last verdict of the believed kind stays up, so one odd sample neither raises a
    /// false alarm nor clears a real one.
    /// </summary>
    private PlacementVerdict Believed(string id, PlacementVerdict raw)
    {
        var believedProblem = _problem.Update(id, raw.IsProblem);
        if (believedProblem == raw.IsProblem)
        {
            _lastVerdict[(id, believedProblem)] = raw;
            return raw;
        }
        return _lastVerdict.TryGetValue((id, believedProblem), out var earlier) ? earlier : raw;
    }

    private (string? Text, string? Tip, LatencyKind Kind) DescribeLatency(string id, List<AppRateDto> mine, DateTimeOffset now)
        => LatencyView.Describe(_latency.Get(id, LatencyView.Fresh(mine), now), LatencyView.TriedAndFailed(mine));

    private static string Headline(AppStatusDto app, PlacementVerdict verdict, string? expected, bool hasTraffic)
    {
        var role = app.Rule.Role.DisplayName();
        return verdict.State switch
        {
            PlacementState.Mismatch => $"Using {verdict.Using}, but assigned to {role} ({expected})",
            PlacementState.Split => $"Using {verdict.Using}: part of it is off {role} ({expected})",
            PlacementState.OnAssigned => $"On {verdict.Using}",
            PlacementState.Idle => hasTraffic ? $"On {expected}" : $"Idle. Assigned to {role} ({expected})",
            _ => verdict.Using is { } on ? $"On {on}. Windows decides" : app.Rule.Paused ? "Paused. Windows decides" : "Windows decides"
        };
    }
}

/// <summary>
/// Turns the service's latency fields into the words shown for them. Shared by the main list and
/// the Live overlay so both say the same thing about the same figure.
/// </summary>
public static class LatencyView
{
    /// <summary>
    /// The latency figure for a set of rate rows. The app's own TCP connections are the better
    /// measurement, so they win when both kinds are present.
    /// </summary>
    public static (RttSummary Rtt, LatencyKind Kind)? Fresh(IReadOnlyCollection<AppRateDto> rows)
    {
        RttSummary? From(LatencyKind kind) => RttSummary.Combine(rows
            .Where(r => r.LatencyKind == kind && r.MedianRttMs is not null)
            .Select(r => new RttSummary(r.MedianRttMs!.Value, r.WorstRttMs ?? r.MedianRttMs.Value, Math.Max(1, r.RttConnections ?? 1))));

        if (From(LatencyKind.Tcp) is { } tcp)
        {
            return (tcp, LatencyKind.Tcp);
        }
        return From(LatencyKind.PeerProbe) is { } peer ? (peer, LatencyKind.PeerProbe) : null;
    }

    /// <summary>Peers were tried and none answered. Different from silence: it is a statement the UI makes.</summary>
    public static bool TriedAndFailed(IReadOnlyCollection<AppRateDto> rows) => rows.Any(r => r.LatencyKind == LatencyKind.NotMeasurable);

    public static (string? Text, string? Tip, LatencyKind Kind) Describe((RttSummary Rtt, LatencyKind Kind)? figure, bool triedAndFailed)
    {
        if (figure is { } f)
        {
            return f.Kind == LatencyKind.Tcp
                ? (f.Rtt.Describe(), Tcp, LatencyKind.Tcp)
                : ($"{f.Rtt.Describe()} · server ping", Peer(f.Rtt.Connections), LatencyKind.PeerProbe);
        }
        return triedAndFailed ? ("latency: not measurable", NotMeasurable, LatencyKind.NotMeasurable) : (null, null, LatencyKind.None);
    }

    public const string Tcp = "Round-trip time of this app's own internet connections, measured by Windows on the connections themselves.";

    public static string Peer(int peers) =>
        $"This app talks UDP, which Windows doesn't time. This is a ping to the {(peers == 1 ? "server" : $"{peers} servers")} it is exchanging traffic with, "
        + "sent from the same network the app uses. It is not the game's own packets, so treat it as a close guide rather than the exact figure.";

    public const string NotMeasurable = "This app's traffic is UDP and the servers it talks to didn't answer a ping. Many game servers ignore ping, so there is no honest number to show.";
}
