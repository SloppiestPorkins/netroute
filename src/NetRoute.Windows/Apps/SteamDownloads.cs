using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NetRoute.Windows.Apps;

/// <summary>What Steam is downloading, read from its own files.</summary>
public sealed record SteamDownload(string Game, long BytesRemaining);

/// <summary>
/// Steam writes what it is doing into <c>appmanifest_*.acf</c> files beside the games, and every
/// library it uses into <c>libraryfolders.vdf</c>. Reading them turns "Steam is busy on Wi-Fi 2"
/// into "Steam is downloading Halo Infinite, 12.4 GB to go", which is the difference between a
/// number and an explanation. Nothing is asked of Steam itself, and nothing is written.
/// </summary>
public static partial class SteamDownloads
{
    [GeneratedRegex(@"""(?<key>name|BytesDownloaded|BytesToDownload)""\s+""(?<value>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Field();

    [GeneratedRegex(@"""path""\s+""(?<path>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();

    /// <summary>Everything Steam has part-downloaded right now, biggest first.</summary>
    public static IReadOnlyList<SteamDownload> InProgress() => InProgress(Libraries());

    internal static IReadOnlyList<SteamDownload> InProgress(IEnumerable<string> libraries)
    {
        var downloads = new List<SteamDownload>();
        foreach (var library in libraries)
        {
            try
            {
                foreach (var manifest in Directory.EnumerateFiles(library, "appmanifest_*.acf"))
                {
                    if (Read(manifest) is { } download)
                    {
                        downloads.Add(download);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A library on a drive that isn't there right now.
            }
        }
        return downloads.OrderByDescending(d => d.BytesRemaining).ToList();
    }

    private static SteamDownload? Read(string manifest)
    {
        try
        {
            string? name = null;
            long downloaded = 0, total = 0;
            foreach (Match match in Field().Matches(File.ReadAllText(manifest)))
            {
                var value = match.Groups["value"].Value;
                switch (match.Groups["key"].Value.ToLowerInvariant())
                {
                    case "name": name ??= value; break;
                    case "bytesdownloaded": long.TryParse(value, out downloaded); break;
                    case "bytestodownload": long.TryParse(value, out total); break;
                }
            }
            // Both are zero when nothing is queued, and equal when the game is complete.
            return total > 0 && downloaded < total ? new SteamDownload(name ?? "a game", total - downloaded) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Steam's own library folders, including ones on other drives.</summary>
    private static IEnumerable<string> Libraries()
    {
        var root = InstallPath();
        if (root is null)
        {
            yield break;
        }
        var main = Path.Combine(root, "steamapps");
        if (Directory.Exists(main))
        {
            yield return main;
        }

        var vdf = Path.Combine(main, "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            yield break;
        }
        string text;
        try
        {
            text = File.ReadAllText(vdf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (Match match in LibraryPath().Matches(text))
        {
            var apps = Path.Combine(match.Groups["path"].Value.Replace(@"\\", @"\"), "steamapps");
            if (Directory.Exists(apps) && !string.Equals(apps, main, StringComparison.OrdinalIgnoreCase))
            {
                yield return apps;
            }
        }
    }

    /// <summary>From the machine hive, because the service runs as LocalSystem and has no user hive.</summary>
    private static string? InstallPath()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Valve\Steam");
                if (key?.GetValue("InstallPath") is string path && Directory.Exists(path))
                {
                    return path;
                }
            }
            catch (Exception)
            {
                // No Steam, or no permission to look.
            }
        }
        return null;
    }
}
