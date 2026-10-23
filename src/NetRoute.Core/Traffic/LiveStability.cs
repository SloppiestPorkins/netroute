namespace NetRoute.Core.Traffic;

/// <summary>
/// Turns one-second byte-rate samples into something readable.
///
/// <para>A rate read every two seconds from a one-second window swings wildly: a game sends in
/// bursts, a download stalls and resumes. Shown raw, the numbers jump on every tick. This is an
/// exponential moving average over elapsed time rather than over ticks, so the result is the same
/// whether the window is polled every 2 s or every 10 s, and a value that has fallen to nothing
/// decays away over a few seconds instead of snapping to zero and back.</para>
///
/// <para>Below <see cref="Floor"/> the figure is shown as zero, so the exponential tail never
/// leaves a row reading "0 B/s" for ever.</para>
/// </summary>
public sealed class RateSmoother
{
    /// <summary>Time constant: after this long a step change has closed about 63% of the gap.</summary>
    public static readonly TimeSpan Tau = TimeSpan.FromSeconds(3);

    /// <summary>Rates under this are shown as zero, in bytes per second.</summary>
    public const double Floor = 256;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public double Down;
        public double Up;
        public DateTimeOffset At;
    }

    /// <summary>Feeds one raw sample and returns the smoothed pair.</summary>
    public (double Down, double Up) Update(string key, double down, double up, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(key, out var e))
        {
            _entries[key] = e = new Entry { Down = down, Up = up, At = now };
            return Shown(e);
        }

        var dt = (now - e.At).TotalSeconds;
        if (dt > 0)
        {
            var alpha = 1 - Math.Exp(-dt / Tau.TotalSeconds);
            e.Down += alpha * (down - e.Down);
            e.Up += alpha * (up - e.Up);
            e.At = now;
        }
        return Shown(e);
    }

    /// <summary>Forgets everything not named, so closed apps don't accumulate.</summary>
    public void Keep(IEnumerable<string> keys)
    {
        var keep = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _entries.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _entries.Remove(gone);
        }
    }

    private static (double, double) Shown(Entry e) => (e.Down < Floor ? 0 : e.Down, e.Up < Floor ? 0 : e.Up);
}

/// <summary>
/// Holds the last good value for a short while after it disappears, then lets it go.
///
/// <para>A latency figure comes and goes with the probe's cadence and the app's traffic. Dropping
/// it for one tick makes a column blink; holding it for ever would show a number that is no
/// longer true. This holds for <see cref="MaxAge"/>: long enough to bridge a missed sample, short
/// enough that what is on screen is still current.</para>
/// </summary>
public sealed class ValueHold<T>(TimeSpan maxAge) where T : struct
{
    private readonly Dictionary<string, (T Value, DateTimeOffset At)> _last = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan MaxAge { get; } = maxAge;

    /// <summary>The fresh value if there is one, else the last one if it is young enough, else null.</summary>
    public T? Get(string key, T? fresh, DateTimeOffset now)
    {
        if (fresh is { } value)
        {
            _last[key] = (value, now);
            return value;
        }
        if (_last.TryGetValue(key, out var held))
        {
            if (now - held.At <= MaxAge)
            {
                return held.Value;
            }
            _last.Remove(key);
        }
        return null;
    }

    public void Keep(IEnumerable<string> keys)
    {
        var keep = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _last.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _last.Remove(gone);
        }
    }
}

/// <summary>
/// Keeps a list in a steady order while its figures change underneath.
///
/// <para>Sorting a live list by speed every tick makes rows swap places constantly, which makes it
/// unreadable. This sorts by activity only every <see cref="Resort"/>; in between, rows stay where
/// they are, a row that has gone is dropped in place and a new row waits at the end. Which row is
/// busiest is still answered, a few seconds late, and nobody has to chase a moving target.</para>
/// </summary>
public sealed class StickyOrder
{
    public static readonly TimeSpan Resort = TimeSpan.FromSeconds(10);

    private List<string> _order = [];
    private DateTimeOffset _sortedAt;

    public IReadOnlyList<string> Order(IReadOnlyCollection<(string Key, double Activity)> items, DateTimeOffset now)
    {
        if (_order.Count == 0 || now - _sortedAt >= Resort)
        {
            _order = items.OrderByDescending(i => i.Activity).ThenBy(i => i.Key, StringComparer.OrdinalIgnoreCase).Select(i => i.Key).ToList();
            _sortedAt = now;
            return _order;
        }

        var present = items.Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = _order.Where(present.Contains).ToList();
        var seen = kept.ToHashSet(StringComparer.OrdinalIgnoreCase);
        kept.AddRange(present.Where(k => !seen.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        _order = kept;
        return _order;
    }
}

/// <summary>
/// A state that has to hold for a few readings before it is believed, in either direction. A
/// rate row that wobbles across the "wrong network" line for one sample must not flash a warning,
/// and a real problem must not clear because one sample looked fine.
/// </summary>
public sealed class Debounce(int needed = 2)
{
    private readonly int _needed = Math.Max(1, needed);
    private readonly Dictionary<string, (bool Shown, int Streak)> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Feeds the raw reading; returns the believed one.</summary>
    public bool Update(string key, bool raw)
    {
        var (shown, streak) = _state.GetValueOrDefault(key);
        if (raw == shown)
        {
            _state[key] = (shown, 0);
            return shown;
        }
        streak++;
        if (streak >= _needed)
        {
            _state[key] = (raw, 0);
            return raw;
        }
        _state[key] = (shown, streak);
        return shown;
    }

    public void Keep(IEnumerable<string> keys)
    {
        var keep = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _state.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _state.Remove(gone);
        }
    }
}
