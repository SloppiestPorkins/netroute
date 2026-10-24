using System.Net;
using System.Text.Json;
using NetRoute.Core.Adapters;

namespace NetRoute.Windows.Split;

/// <summary>
/// Makes one adapter win Windows' IPv4 default route over another, and undoes that exactly.
/// See <see cref="NetRoute.Core.Policy.RedirectPlan"/> for why the Downloads adapter has to
/// be the default.
///
/// <para>The original metrics are written to disk BEFORE anything is changed, so a crash
/// between the two changes can still be undone by <see cref="Restore"/>. Emergency Disable,
/// service stop and uninstall all call it.</para>
///
/// <para>Every value in that file records where it came from. A value NetRoute read off the
/// machine before touching anything is put back as it was; a value NetRoute itself wrote --
/// which is what breaking a default-route tie leaves behind -- is put back as Windows'
/// automatic metric, because replaying our own number would "restore" the machine to our
/// settings and call it done. The owner's own file had reached exactly that state (review of
/// 23 October 2026, finding 1).</para>
///
/// <para>Metrics are read and written through IP Helper rather than PowerShell's NetTCPIP
/// cmdlets. Five <c>powershell.exe</c> starts, each with a 60-second timeout, ran on every
/// adapter change and held a lock that Emergency Disable needed (findings 2 and 18).</para>
/// </summary>
public sealed class DefaultRouteManager
{
    /// <summary>Written into the state file so a later version can tell what it is reading.</summary>
    private const int StateVersion = 2;

    private readonly IAdapterSource _adapters;
    private readonly IMetricApi _api;
    private readonly string _statePath;
    private bool _unreadable;

    public DefaultRouteManager(IAdapterSource? adapters = null, string? statePath = null)
        : this(adapters ?? new AdapterDiscovery(), statePath, null)
    {
    }

    internal DefaultRouteManager(IAdapterSource adapters, string? statePath, IMetricApi? api)
    {
        _adapters = adapters;
        _api = api ?? new WindowsMetricApi();
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRoute", "route-state.json");
    }

    public bool IsManaging => File.Exists(_statePath);

    /// <summary>Makes <paramref name="preferred"/> the default connection. Idempotent. Returns a sentence for the user.</summary>
    public string EnsurePreferred(NetworkAdapter preferred, NetworkAdapter other)
    {
        var state = Load();
        if (state is not null && (state.PreferredLuid != preferred.Luid || state.OtherLuid != other.Luid))
        {
            Restore();
            state = null;
        }
        if (state is null)
        {
            // Keep originals still waiting to be restored on adapters that are away right now
            // (see Restore). Overwriting them would lose those settings for good.
            var originals = Load()?.Originals.ToList() ?? [];
            foreach (var adapter in new[] { preferred, other })
            {
                if (originals.All(o => o.Luid != adapter.Luid))
                {
                    originals.Add(Read(adapter));
                }
            }
            Save(NewState(preferred.Luid, other.Luid, originals));
        }

        // Effective metric = route metric + interface metric. Leave a clear margin so a small
        // automatic adjustment by Windows can't flip the order back.
        long preferredRoute = DefaultRouteMetric(preferred);
        long otherRoute = DefaultRouteMetric(other);
        const long preferredMetric = 1;
        var otherMetric = Math.Clamp(preferredRoute + preferredMetric + 20 - otherRoute, 1, 9999);

        SetMetric(preferred, preferredMetric);
        SetMetric(other, otherMetric);

        var winner = Winner();
        return winner == preferred.InterfaceIndex
            ? $"{preferred.Name} is Windows' default connection."
            : $"NetRoute made {preferred.Name} the default connection, but Windows is still routing through interface {winner}.";
    }

    /// <summary>
    /// Puts every metric NetRoute changed back. Safe to call when nothing was changed. Throws when
    /// any part of it could not be done, because the caller after this one (uninstall) deletes the
    /// only program that could try again.
    /// </summary>
    public void Restore()
    {
        var state = Load();
        if (state is null)
        {
            if (_unreadable)
            {
                // The file is there but cannot be read, so what the settings were is unknown. The
                // one safe move is Windows' own default, on both of this PC's role adapters.
                throw new InvalidOperationException(
                    $"NetRoute's record of the original connection settings at {_statePath} could not be read, " +
                    "so they cannot be put back exactly. Set every connection back to Windows' automatic metric " +
                    "to undo it: Get-NetIPInterface -AddressFamily IPv4 | Set-NetIPInterface -AutomaticMetric Enabled");
            }
            return;
        }

        var adapters = _adapters.DiscoverAll();
        var failures = new List<string>();
        var remaining = new List<SavedMetric>();
        foreach (var original in state.Originals)
        {
            var adapter = adapters.FirstOrDefault(a => a.Luid == original.Luid);
            if (adapter is null)
            {
                // Unplugged, disabled or re-enumerating right now: a USB Wi-Fi adapter can vanish
                // for a moment. Its metric is still NetRoute's, so keep the original to put back
                // when it returns. Forgetting it here left a Wi-Fi adapter on NetRoute's metric
                // for good, tied with Ethernet, so Windows' default connection became a coin toss.
                remaining.Add(original);
                continue;
            }
            try
            {
                // Only a value we actually observed is replayed. Anything else -- a value NetRoute
                // wrote itself, or one from a file written before provenance was recorded -- goes
                // back to Windows' automatic metric, which is Windows' own default.
                if (original.Source == MetricSource.Observed && !original.Automatic)
                {
                    _api.SetIpv4Interface(original.Luid, automatic: false, metric: (uint)Math.Max(1, original.Metric));
                }
                else
                {
                    _api.SetIpv4Interface(original.Luid, automatic: true, metric: 0);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{adapter.Name}: {ex.Message}");
                remaining.Add(original);
            }
        }

        // The file is only deleted once every original is back in place. Uninstall reads that as
        // the proof that the restore really happened (see Setup's RestorePolicy).
        if (remaining.Count == 0)
        {
            File.Delete(_statePath);
        }
        else
        {
            Save(state with { Originals = remaining });
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Could not restore: " + string.Join("; ", failures));
        }
    }

    /// <summary>
    /// Breaks a tie for Windows' default route (see <see cref="RouteTies"/>). Every tied
    /// connection goes back to Windows' automatic metric; if they still tie after that, the
    /// others are ranked just below <paramref name="preferred"/>. Returns a sentence for the user.
    /// </summary>
    public string ResolveTie(IReadOnlyList<NetworkAdapter> tied, NetworkAdapter preferred)
    {
        foreach (var adapter in tied)
        {
            _api.SetIpv4Interface(adapter.Luid, automatic: true, metric: 0);
        }

        var preferredCost = Cost(preferred);
        var replaced = tied.ToDictionary(a => a.Luid, a => new SavedMetric(a.Luid, true, 0, MetricSource.OurFix));
        var ranked = new List<string>();
        foreach (var other in tied.Where(a => a.Luid != preferred.Luid))
        {
            var otherCost = Cost(other);
            if (otherCost <= preferredCost)
            {
                var metric = Math.Clamp(InterfaceMetric(other) + preferredCost - otherCost + 10, 1, 9999);
                SetMetric(other, metric);
                replaced[other.Luid] = new SavedMetric(other.Luid, false, (int)metric, MetricSource.OurFix);
                ranked.Add(other.Name);
            }
        }

        // Saved originals for these connections are the settings that tied. Restoring them later
        // would bring the tie straight back, so they are replaced -- and marked as ours, so a
        // restore knows these are not Windows' settings and reaches for automatic instead.
        if (Load() is { } state)
        {
            Save(state with { Originals = state.Originals.Select(o => replaced.GetValueOrDefault(o.Luid, o)).ToList() });
        }

        var winner = Winner();
        var winnerName = tied.FirstOrDefault(a => a.InterfaceIndex == winner)?.Name;
        var how = ranked.Count == 0
            ? "Windows' automatic settings are back on"
            : $"Windows' automatic settings are back on and {string.Join(", ", ranked)} now ranks below {preferred.Name}";
        return winnerName is null
            ? $"{how}, but Windows is routing through interface {winner}."
            : $"Fixed. {how}, so {winnerName} is Windows' one default connection.";
    }

    private long Cost(NetworkAdapter adapter) => DefaultRouteMetric(adapter) + InterfaceMetric(adapter);

    private long InterfaceMetric(NetworkAdapter adapter)
        => _api.GetIpv4Interface(adapter.Luid)?.Metric
           ?? throw new InvalidOperationException($"{adapter.Name} has no IPv4 configuration to read.");

    private SavedMetric Read(NetworkAdapter adapter)
    {
        var current = _api.GetIpv4Interface(adapter.Luid)
                      ?? throw new InvalidOperationException($"{adapter.Name} has no IPv4 configuration to read.");
        return new SavedMetric(adapter.Luid, current.Automatic, (int)current.Metric, MetricSource.Observed);
    }

    private long DefaultRouteMetric(NetworkAdapter adapter) => _api.LowestDefaultRouteMetric(adapter.Luid) ?? 0;

    private void SetMetric(NetworkAdapter adapter, long metric)
        => _api.SetIpv4Interface(adapter.Luid, automatic: false, metric: (uint)metric);

    /// <summary>Which interface Windows would actually use right now, or -1 if it cannot say.</summary>
    private long Winner() => _api.BestInterfaceIndex(IPAddress.Parse("1.1.1.1")) is { } index ? index : -1;

    private static RouteState NewState(ulong preferred, ulong other, List<SavedMetric> originals)
        => new(StateVersion, DateTimeOffset.UtcNow, preferred, other, originals);

    private RouteState? Load()
    {
        _unreadable = false;
        if (!File.Exists(_statePath))
        {
            return null;
        }
        try
        {
            var state = JsonSerializer.Deserialize<RouteState>(File.ReadAllText(_statePath));
            if (state is null)
            {
                _unreadable = true;
                return null;
            }
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Not treated as "nothing to restore": that would quietly cost the user the ability to
            // be put back, and tell them it had worked. The file stays where it is, and Restore
            // says what it could not do.
            _unreadable = true;
            return null;
        }
    }

    private void Save(RouteState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

        // Written to a temporary file and moved into place, because this file is the only way back:
        // a write interrupted half way through used to read as "nothing was saved".
        var temporary = _statePath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(_statePath))
        {
            File.Replace(temporary, _statePath, null);
        }
        else
        {
            File.Move(temporary, _statePath);
        }
    }

    /// <summary>Where a saved metric came from, which decides how it is put back.</summary>
    private enum MetricSource
    {
        /// <summary>Written before this was recorded. Cannot be trusted as Windows' own setting.</summary>
        Unknown = 0,

        /// <summary>Read off the machine before NetRoute changed anything.</summary>
        Observed = 1,

        /// <summary>NetRoute's own value, written to break a default-route tie.</summary>
        OurFix = 2
    }

    private sealed record SavedMetric(ulong Luid, bool Automatic, int Metric, MetricSource Source = MetricSource.Unknown);

    private sealed record RouteState(
        int Version, DateTimeOffset CapturedAt, ulong PreferredLuid, ulong OtherLuid, List<SavedMetric> Originals);
}
