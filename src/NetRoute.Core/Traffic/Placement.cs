namespace NetRoute.Core.Traffic;

public enum PlacementState
{
    /// <summary>The app has no assigned network to compare against (Default role, or paused), so there is nothing to get wrong.</summary>
    Unmanaged,

    /// <summary>No traffic and no connections right now.</summary>
    Idle,

    /// <summary>Connected or moving data, and only on the assigned network.</summary>
    OnAssigned,

    /// <summary>Using some network other than the assigned one, and nothing on the assigned one.</summary>
    Mismatch,

    /// <summary>Using the assigned network and another one at the same time.</summary>
    Split
}

/// <param name="Adapter">Null when the sample could not be tied to an adapter.</param>
public readonly record struct AdapterTraffic(string? Adapter, double BytesPerSecond);

/// <param name="Using">The network(s) the app is actually on, for display. Null when idle or unknown.</param>
/// <param name="Wrong">The networks it is on that are not the assigned one.</param>
/// <param name="FromTraffic">True when this rests on bytes moving; false when only on open connections.</param>
public sealed record PlacementVerdict(PlacementState State, string? Using, IReadOnlyList<string> Wrong, bool FromTraffic)
{
    public bool IsProblem => State is PlacementState.Mismatch or PlacementState.Split;
}

/// <summary>
/// Decides whether an app is on the network it was assigned. This is the question the whole
/// product exists to answer, so it works from what the app is really doing rather than from what
/// was configured: bytes per network when there are any, open connections otherwise.
/// </summary>
public static class PlacementCheck
{
    /// <summary>Below this combined rate an app counts as quiet and its connections speak instead of its bytes.</summary>
    public const double QuietBytesPerSecond = 1024;

    /// <summary>A second network only counts as a split if it carries at least this share of the traffic...</summary>
    public const double SplitShare = 0.10;

    /// <summary>...and at least this much in absolute terms, so a stray packet on the other network is not a split.</summary>
    public const double MinSplitBytesPerSecond = 512;

    /// <param name="expected">The assigned network's name, or null when none applies.</param>
    /// <param name="traffic">Per-network rates for this app, raw or smoothed.</param>
    /// <param name="connectionAdapter">The network the app's open connections are on, when one was found.</param>
    /// <param name="connections">How many open connections the app has.</param>
    public static PlacementVerdict Evaluate(string? expected, IReadOnlyList<AdapterTraffic> traffic, string? connectionAdapter, int connections)
    {
        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        var byAdapter = traffic
            .Where(t => t.Adapter is not null && t.BytesPerSecond > 0)
            .GroupBy(t => t.Adapter!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Adapter: g.Key, Rate: g.Sum(t => t.BytesPerSecond)))
            .OrderByDescending(g => g.Rate)
            .ToList();
        var total = byAdapter.Sum(g => g.Rate);

        if (total >= QuietBytesPerSecond)
        {
            var onUse = byAdapter.Where(g => g.Rate >= Math.Max(total * SplitShare, MinSplitBytesPerSecond)).ToList();
            var names = string.Join(" and ", onUse.Select(g => g.Adapter));
            if (expected is null)
            {
                return new(PlacementState.Unmanaged, names, [], true);
            }
            var wrong = onUse.Where(g => !Same(g.Adapter, expected)).Select(g => g.Adapter).ToList();
            if (wrong.Count == 0)
            {
                return new(PlacementState.OnAssigned, expected, [], true);
            }
            return new(onUse.Any(g => Same(g.Adapter, expected)) ? PlacementState.Split : PlacementState.Mismatch, names, wrong, true);
        }

        if (connections > 0 && connectionAdapter is not null)
        {
            if (expected is null)
            {
                return new(PlacementState.Unmanaged, connectionAdapter, [], false);
            }
            return Same(connectionAdapter, expected)
                ? new(PlacementState.OnAssigned, expected, [], false)
                : new(PlacementState.Mismatch, connectionAdapter, [connectionAdapter], false);
        }

        return new(expected is null ? PlacementState.Unmanaged : PlacementState.Idle, null, [], false);
    }
}
