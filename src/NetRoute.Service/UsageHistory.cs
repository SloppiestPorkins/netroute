using System.Globalization;
using NetRoute.Ipc;

namespace NetRoute.Service;

public interface IUsageHistory
{
    UsageHistoryDto Summarise(int days);
}

public sealed class NoUsageHistory : IUsageHistory
{
    public UsageHistoryDto Summarise(int days) => new([], [], "This build doesn't record usage.", "");
}

/// <summary>
/// How much each app moved through each connection, day by day.
///
/// <para>"What used my gaming line last night" is the most asked-for thing in every tool of this
/// kind, and NetRoute already measures per-app rates — it just used to throw them away. This adds
/// them up and writes one small CSV per day, so the data is plain, portable and easy to delete.
/// Thirty days are kept; anything older is removed.</para>
///
/// <para>Totals come from the meter's own running counters rather than from the rate readings, so
/// the GUI asking for live speeds can't disturb the accounting.</para>
/// </summary>
public sealed class UsageHistory(IAppTotalsSource rates, ILogger<UsageHistory> logger, string? folder = null)
    : BackgroundService, IUsageHistory
{
    private const int KeepDays = 30;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly string _folder = folder ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetRoute", "history");

    private readonly object _gate = new();
    private readonly Dictionary<(string App, string Adapter), (long Down, long Up)> _previous = [];
    private readonly Dictionary<(int Hour, string Adapter, string App), (long Down, long Up)> _today = [];
    private DateTime _day = DateTime.Today;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_folder);
        Load(DateTime.Today);
        Prune();

        var flushed = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Sample();
                if (DateTimeOffset.UtcNow - flushed > TimeSpan.FromMinutes(1))
                {
                    Flush();
                    flushed = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Recording usage failed.");
            }
            await Task.Delay(Interval, stoppingToken);
        }
        Flush();
    }

    /// <summary>One reading. The loop calls this every 15 seconds; tests call it directly.</summary>
    public void Sample()
    {
        var totals = rates.GetTotals();
        lock (_gate)
        {
            if (_day != DateTime.Today)
            {
                Flush();
                _today.Clear();
                _day = DateTime.Today;
                Prune();
            }
            var hour = DateTime.Now.Hour;
            foreach (var total in totals)
            {
                var key = (total.App, total.Adapter ?? "");
                var seen = _previous.GetValueOrDefault(key);
                var down = total.DownBytes - seen.Down;
                var up = total.UpBytes - seen.Up;
                _previous[key] = (total.DownBytes, total.UpBytes);
                if (down <= 0 && up <= 0)
                {
                    continue;   // idle, or the service restarted and counters began again
                }
                var bucket = (hour, key.Item2, key.App);
                var current = _today.GetValueOrDefault(bucket);
                _today[bucket] = (current.Down + Math.Max(0, down), current.Up + Math.Max(0, up));
            }
        }
    }

    public void Flush()
    {
        try
        {
            List<string> lines;
            DateTime day;
            lock (_gate)
            {
                day = _day;
                lines = _today.OrderBy(e => e.Key.Hour).ThenBy(e => e.Key.Adapter)
                    .Select(e => string.Join(',', e.Key.Hour, Escape(e.Key.Adapter), Escape(e.Key.App), e.Value.Down, e.Value.Up))
                    .ToList();
            }
            if (lines.Count == 0)
            {
                return;
            }
            Directory.CreateDirectory(_folder);
            File.WriteAllLines(Path.Combine(_folder, FileName(day)), lines.Prepend("hour,adapter,app,downBytes,upBytes"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Writing the usage history failed.");
        }
    }

    private void Load(DateTime day)
    {
        var path = Path.Combine(_folder, FileName(day));
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            lock (_gate)
            {
                foreach (var entry in Read(path))
                {
                    _today[(entry.Hour, entry.Adapter, entry.App)] = (entry.Down, entry.Up);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading today's usage history failed.");
        }
    }

    private void Prune()
    {
        try
        {
            var oldest = DateTime.Today.AddDays(-KeepDays);
            foreach (var file in Directory.EnumerateFiles(_folder, "*.csv"))
            {
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < oldest)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pruning the usage history failed.");
        }
    }

    public UsageHistoryDto Summarise(int days)
    {
        Flush();
        var byDay = new Dictionary<(string Day, string Adapter), (long Down, long Up)>();
        var byApp = new Dictionary<(string App, string Adapter), (long Down, long Up)>();
        try
        {
            for (var back = 0; back < Math.Clamp(days, 1, KeepDays); back++)
            {
                var day = DateTime.Today.AddDays(-back);
                var path = Path.Combine(_folder, FileName(day));
                if (!File.Exists(path))
                {
                    continue;
                }
                foreach (var entry in Read(path))
                {
                    var dayKey = (FileName(day)[..10], entry.Adapter);
                    var current = byDay.GetValueOrDefault(dayKey);
                    byDay[dayKey] = (current.Down + entry.Down, current.Up + entry.Up);

                    var appKey = (entry.App, entry.Adapter);
                    var app = byApp.GetValueOrDefault(appKey);
                    byApp[appKey] = (app.Down + entry.Down, app.Up + entry.Up);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading the usage history failed.");
            return new([], [], "The usage history couldn't be read: " + ex.Message, _folder);
        }

        return new UsageHistoryDto(
            byDay.OrderBy(e => e.Key.Day).ThenBy(e => e.Key.Adapter)
                .Select(e => new UsageDayDto(e.Key.Day, e.Key.Adapter, e.Value.Down, e.Value.Up)).ToList(),
            byApp.OrderByDescending(e => e.Value.Down + e.Value.Up).Take(25)
                .Select(e => new UsageAppDto(e.Key.App, e.Key.Adapter, e.Value.Down, e.Value.Up)).ToList(),
            null,
            _folder);
    }

    private static IEnumerable<(int Hour, string Adapter, string App, long Down, long Up)> Read(string path)
    {
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = Split(line);
            if (parts.Count == 5 && int.TryParse(parts[0], out var hour)
                && long.TryParse(parts[3], out var down) && long.TryParse(parts[4], out var up))
            {
                yield return (hour, parts[1], parts[2], down, up);
            }
        }
    }

    private static string FileName(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";

    // App names can contain a comma; quote them the usual way rather than inventing a format.
    private static string Escape(string value) => value.Contains(',') || value.Contains('"')
        ? '"' + value.Replace("\"", "\"\"") + '"'
        : value;

    private static List<string> Split(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') { quoted = false; }
                else { current.Append(c); }
            }
            else if (c == '"') { quoted = true; }
            else if (c == ',') { parts.Add(current.ToString()); current.Clear(); }
            else { current.Append(c); }
        }
        parts.Add(current.ToString());
        return parts;
    }
}
