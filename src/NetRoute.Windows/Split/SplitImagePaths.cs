using NetRoute.Core.Policy;

namespace NetRoute.Windows.Split;

/// <summary>
/// The program files the split-tunnel driver should move for one app.
///
/// <para>The driver matches on the image path of each process. A game rarely runs as a
/// single .exe: DayZ's launcher runs DayZ_BE.exe, which starts DayZ_x64.exe, and Game Pass
/// titles live in a content folder full of binaries. Child processes are covered by the
/// driver's inheritance, but a game started directly by Steam isn't a child of the .exe the
/// user picked. So for store-installed games the whole game folder is covered.</para>
///
/// <para>Folders are expanded only when they are clearly one game's own folder. Expanding
/// C:\Windows or Program Files would move system components onto the Gaming network.</para>
/// </summary>
public static partial class SplitImagePaths
{
    [System.Text.RegularExpressions.GeneratedRegex(
        @"^[A-Za-z]:\\Users(\\[^\\]+(\\AppData(\\(Local|LocalLow|Roaming)(\\Programs)?)?)?)?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex UserRoot();

    private const int MaxDepth = 4;
    private const int MaxImages = 64;

    private static readonly string[] StoreMarkers = [@"\steamapps\common\", @"\XboxGames\", @"\Epic Games\"];

    /// <summary>
    /// Game libraries that can sit directly inside a launcher's own folder: Steam\steamapps,
    /// Ubisoft Game Launcher\games, GOG Galaxy\Games. A launcher's folder isn't its games.
    /// Putting Steam on Downloads must never pull every game in its default library with it.
    /// </summary>
    private static readonly string[] LibraryFolders = ["steamapps", "games"];

    /// <summary>
    /// Whether <paramref name="path"/> is one of the app's own program files under
    /// <paramref name="root"/>, rather than a game in a library folder inside it.
    /// </summary>
    public static bool IsOwnFile(string root, string path)
    {
        root = root.TrimEnd('\\');
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var first = path[(root.Length + 1)..].Split('\\')[0];
        return path.IndexOf('\\', root.Length + 1) < 0 || !LibraryFolders.Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> For(AppIdentity app)
    {
        var images = new List<string>();
        if (app.ExecutablePath is { } exe && File.Exists(exe))
        {
            images.Add(Path.GetFullPath(exe));
        }
        if (GameRoot(app) is { } root)
        {
            images.AddRange(Executables(root));
        }
        return images.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxImages).ToList();
    }

    internal static string? GameRoot(AppIdentity app)
    {
        var candidate = app.InstallLocation;
        if (candidate is null && app.ExecutablePath is { } exe)
        {
            candidate = StoreMarkers.Select(m => StoreRoot(exe, m)).FirstOrDefault(r => r is not null);
        }
        return candidate is not null && IsSafeRoot(candidate) ? Path.GetFullPath(candidate).TrimEnd('\\') : null;
    }

    /// <summary>"...\steamapps\common\DayZ\bin\x.exe" gives "...\steamapps\common\DayZ".</summary>
    private static string? StoreRoot(string exe, string marker)
    {
        var at = exe.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }
        var start = at + marker.Length;
        var end = exe.IndexOf('\\', start);
        return end < 0 ? null : exe[..end];
    }

    internal static bool IsSafeRoot(string root)
    {
        string full;
        try
        {
            full = Path.GetFullPath(root).TrimEnd('\\');
        }
        catch (Exception)
        {
            return false;
        }

        if (string.Equals(Path.GetPathRoot(full)?.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))
        {
            return false;   // a whole drive
        }

        // Any user's profile or AppData root, not just the current account's. The service runs
        // as SYSTEM, so GetFolderPath below would only ever name SYSTEM's profile.
        if (UserRoot().IsMatch(full))
        {
            return false;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (full.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var broad = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        if (broad.Any(b => b.Length > 0 && string.Equals(full, b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return Directory.Exists(full);
    }

    private static IEnumerable<string> Executables(string root)
    {
        var found = new List<string>();
        var pending = new Queue<(string Dir, int Depth)>();
        pending.Enqueue((root, 0));

        while (pending.Count > 0 && found.Count < MaxImages)
        {
            var (dir, depth) = pending.Dequeue();
            try
            {
                found.AddRange(Directory.EnumerateFiles(dir, "*.exe").Take(MaxImages - found.Count));
                if (depth < MaxDepth)
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        if (depth == 0 && LibraryFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                        {
                            continue;   // see LibraryFolders
                        }
                        pending.Enqueue((sub, depth + 1));
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Some store folders are partly locked down; take what is readable.
            }
        }

        return found;
    }
}
