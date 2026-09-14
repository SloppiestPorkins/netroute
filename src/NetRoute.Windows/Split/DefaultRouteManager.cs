using System.Diagnostics;
using System.Text;
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
/// <para>This goes through PowerShell's NetTCPIP cmdlets rather than SetIpInterfaceEntry. They
/// carry the AutomaticMetric flag through cleanly and keep a large struct layout off the risk
/// list. The changes are rare (setup, adapter changes), so a one-second process start is fine.</para>
/// </summary>
public sealed class DefaultRouteManager
{
    private readonly IAdapterSource _adapters;
    private readonly string _statePath;

    public DefaultRouteManager(IAdapterSource? adapters = null, string? statePath = null)
    {
        _adapters = adapters ?? new AdapterDiscovery();
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
            Save(new RouteState(preferred.Luid, other.Luid, originals));
        }

        // Effective metric = route metric + interface metric. Leave a clear margin so a small
        // automatic adjustment by Windows can't flip the order back.
        long preferredRoute = DefaultRouteMetric(preferred.InterfaceIndex);
        long otherRoute = DefaultRouteMetric(other.InterfaceIndex);
        const long preferredMetric = 1;
        var otherMetric = Math.Clamp(preferredRoute + preferredMetric + 20 - otherRoute, 1, 9999);

        SetMetric(preferred.InterfaceIndex, preferredMetric);
        SetMetric(other.InterfaceIndex, otherMetric);

        var winner = DefaultRouteWinner();
        return winner == preferred.InterfaceIndex
            ? $"{preferred.Name} is Windows' default connection."
            : $"NetRoute made {preferred.Name} the default connection, but Windows is still routing through interface {winner}.";
    }

    /// <summary>Puts every metric NetRoute changed back as it was. Safe to call when nothing was changed.</summary>
    public void Restore()
    {
        var state = Load();
        if (state is null)
        {
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
                Run(original.Automatic
                    ? $"Set-NetIPInterface -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4 -AutomaticMetric Enabled"
                    : $"Set-NetIPInterface -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {original.Metric}");
            }
            catch (Exception ex)
            {
                failures.Add($"{adapter.Name}: {ex.Message}");
                remaining.Add(original);
            }
        }

        // The file is only deleted once every original is back in place.
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
            Run($"Set-NetIPInterface -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4 -AutomaticMetric Enabled");
        }

        var preferredCost = Cost(preferred);
        var replaced = tied.ToDictionary(a => a.Luid, a => new SavedMetric(a.Luid, true, 0));
        var ranked = new List<string>();
        foreach (var other in tied.Where(a => a.Luid != preferred.Luid))
        {
            var otherCost = Cost(other);
            if (otherCost <= preferredCost)
            {
                var metric = Math.Clamp(InterfaceMetric(other) + preferredCost - otherCost + 10, 1, 9999);
                SetMetric(other.InterfaceIndex, metric);
                replaced[other.Luid] = new SavedMetric(other.Luid, false, (int)metric);
                ranked.Add(other.Name);
            }
        }

        // Saved originals for these connections are the settings that tied. Restoring them
        // later would bring the tie straight back, so restore the fixed settings instead.
        if (Load() is { } state)
        {
            Save(state with { Originals = state.Originals.Select(o => replaced.GetValueOrDefault(o.Luid, o)).ToList() });
        }

        var winner = DefaultRouteWinner();
        var winnerName = tied.FirstOrDefault(a => a.InterfaceIndex == winner)?.Name;
        var how = ranked.Count == 0
            ? "Windows' automatic settings are back on"
            : $"Windows' automatic settings are back on and {string.Join(", ", ranked)} now ranks below {preferred.Name}";
        return winnerName is null
            ? $"{how}, but Windows is routing through interface {winner}."
            : $"Fixed. {how}, so {winnerName} is Windows' one default connection.";
    }

    private static long Cost(NetworkAdapter adapter) => DefaultRouteMetric(adapter.InterfaceIndex) + InterfaceMetric(adapter);

    private static long InterfaceMetric(NetworkAdapter adapter)
        => long.Parse(Run($"[int](Get-NetIPInterface -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4).InterfaceMetric"));

    private static SavedMetric Read(NetworkAdapter adapter)
    {
        var json = Run($"$i = Get-NetIPInterface -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily IPv4; " +
                       "[pscustomobject]@{ auto = ($i.AutomaticMetric -eq 'Enabled'); metric = [int]$i.InterfaceMetric } | ConvertTo-Json -Compress");
        using var doc = JsonDocument.Parse(json);
        return new SavedMetric(adapter.Luid, doc.RootElement.GetProperty("auto").GetBoolean(), doc.RootElement.GetProperty("metric").GetInt32());
    }

    private static long DefaultRouteMetric(uint interfaceIndex)
        => long.Parse(Run($"$r = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -InterfaceIndex {interfaceIndex} -ErrorAction SilentlyContinue | " +
                          "Sort-Object RouteMetric | Select-Object -First 1; if ($r) { [int]$r.RouteMetric } else { 0 }"));

    private static void SetMetric(uint interfaceIndex, long metric)
        => Run($"Set-NetIPInterface -InterfaceIndex {interfaceIndex} -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {metric}");

    private static long DefaultRouteWinner()
        => long.Parse(Run("$r = Find-NetRoute -RemoteIPAddress '1.1.1.1' -ErrorAction SilentlyContinue | Select-Object -First 1; " +
                          "if ($r) { [int]$r.InterfaceIndex } else { -1 }"));

    private static string Run(string script)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'; " + script)) })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill();
            throw new TimeoutException("PowerShell did not respond.");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(error.Result.Trim());
        }
        return output.Result.Trim();
    }

    private RouteState? Load()
    {
        try
        {
            return File.Exists(_statePath) ? JsonSerializer.Deserialize<RouteState>(File.ReadAllText(_statePath)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save(RouteState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
    }

    private sealed record SavedMetric(ulong Luid, bool Automatic, int Metric);

    private sealed record RouteState(ulong PreferredLuid, ulong OtherLuid, List<SavedMetric> Originals);
}
