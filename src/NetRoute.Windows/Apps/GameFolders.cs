using System.Diagnostics.CodeAnalysis;

namespace NetRoute.Windows.Apps;

/// <summary>
/// Recognises a game from where its program lives. Store libraries keep each game in its own
/// folder under a known parent (steamapps\common, XboxGames, Epic Games...), so a running
/// program inside one is almost certainly a game, and that folder is the whole game.
/// </summary>
public static class GameFolders
{
    private static readonly string[] Libraries =
    [
        @"\steamapps\common\", @"\XboxGames\", @"\Epic Games\", @"\GOG Galaxy\Games\",
        @"\Ubisoft Game Launcher\games\", @"\EA Games\"
    ];

    private static readonly string[] NotGames =
    [
        @"\steamapps\common\wallpaper_engine\", @"\steamapps\common\SteamVR\", @"\steamapps\common\Steamworks Shared\",
        @"\steamapps\common\Steam Controller Configs\", @"\Epic Games\Launcher\", @"\Epic Games\DirectXRedist\"
    ];

    /// <summary>Programs that ship with a game but don't carry its traffic worth asking about.</summary>
    private static readonly string[] SupportPrograms = ["crash", "unins", "redist", "setup", "installer", "cefsubprocess", "webhelper"];

    /// <summary>
    /// The game's own folder and its name, e.g. "G:\SteamLibrary\steamapps\common\Halo Infinite"
    /// and "Halo Infinite", when <paramref name="programPath"/> is a game.
    /// </summary>
    public static bool TryGetGameRoot(string programPath, [NotNullWhen(true)] out string? root, [NotNullWhen(true)] out string? name)
    {
        root = name = null;
        if (NotGames.Any(n => programPath.Contains(n, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        var file = Path.GetFileNameWithoutExtension(programPath);
        if (SupportPrograms.Any(s => file.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        foreach (var library in Libraries)
        {
            var at = programPath.IndexOf(library, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                continue;
            }
            var start = at + library.Length;
            var end = programPath.IndexOf('\\', start);
            if (end <= start)
            {
                return false;   // a program sitting directly in the library folder isn't a game's
            }
            root = programPath[..end];
            name = programPath[start..end];
            return true;
        }
        return false;
    }
}
