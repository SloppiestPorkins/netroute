namespace NetRoute.Core.Traffic;

/// <summary>
/// One program's TCP round-trip times boiled down to the two numbers worth showing.
///
/// <para>The median says how the program is doing; the worst says what the user is chasing.
/// A mean would let one stalled connection make a healthy app look broken, and showing only
/// the median would hide that connection. For an even count the median is the midpoint of the
/// two middle values: taking the upper one would make two connections at 10 ms and 300 ms
/// report a "median" equal to the worst, which reads as the whole app being at 300.</para>
/// </summary>
public readonly record struct RttSummary(int MedianMs, int WorstMs, int Connections)
{
    public static RttSummary? Of(IEnumerable<uint> samples)
    {
        var sorted = samples.Select(s => (int)Math.Min(s, int.MaxValue)).Order().ToList();
        return sorted.Count == 0 ? null : new RttSummary(Middle(sorted), sorted[^1], sorted.Count);
    }

    /// <summary>
    /// Merges summaries of the same program, for a program that has several processes. The raw
    /// samples aren't to hand by then, so each process's median stands in for its connections,
    /// once per connection. That is exact when each process's connections agree and close
    /// enough otherwise; the worst is exact either way.
    /// </summary>
    public static RttSummary? Combine(IEnumerable<RttSummary> parts)
    {
        var all = parts.Where(p => p.Connections > 0).ToList();
        if (all.Count == 0)
        {
            return null;
        }
        var expanded = all.SelectMany(p => Enumerable.Repeat(p.MedianMs, p.Connections)).Order().ToList();
        return new RttSummary(Middle(expanded), all.Max(p => p.WorstMs), all.Sum(p => p.Connections));
    }

    /// <summary>"14 ms", or "14 ms (worst 300 ms)" when several connections disagree. The worst is left out when it adds nothing.</summary>
    public string Describe()
        => Connections > 1 && WorstMs > MedianMs ? $"{MedianMs} ms (worst {WorstMs} ms)" : $"{MedianMs} ms";

    private static int Middle(List<int> sorted)
        => sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (int)(((long)sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2] + 1) / 2);
}
