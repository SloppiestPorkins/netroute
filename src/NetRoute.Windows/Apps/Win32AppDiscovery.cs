using System.Diagnostics;
using Microsoft.Win32;
using NetRoute.Core.Policy;

namespace NetRoute.Windows.Apps;

/// <summary>
/// Finds ordinary desktop applications, from the registry's installed-programs list
/// and from what is currently running.
///
/// <para>Running processes matter as much as installed ones: a game launched through
/// Steam often has no uninstall entry of its own, so the only way a user will find it
/// is by it being on screen at the time.</para>
/// </summary>
public sealed class Win32AppDiscovery
{
    /// <summary>
    /// Executables that are launchers rather than the thing the user is playing.
    ///
    /// <para>§37 is explicit that a launcher must not inherit the game's policy — the
    /// whole point of §36 is Steam downloading on one connection while the game it
    /// launched runs on another. Categorising them separately is what makes that
    /// distinction visible in the picker instead of something the user has to know.</para>
    /// </summary>
    private static readonly Dictionary<string, string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["steam.exe"] = "Steam",
        ["epicgameslauncher.exe"] = "Epic Games Launcher",
        ["battle.net.exe"] = "Battle.net",
        ["eadesktop.exe"] = "EA app",
        ["upc.exe"] = "Ubisoft Connect",
        ["ubisoftconnect.exe"] = "Ubisoft Connect",
        ["galaxyclient.exe"] = "GOG Galaxy",
        ["rockstarservice.exe"] = "Rockstar Games Launcher",
        ["riotclientservices.exe"] = "Riot Client"
    };

    private static readonly Dictionary<string, string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome.exe"] = "Google Chrome",
        ["msedge.exe"] = "Microsoft Edge",
        ["firefox.exe"] = "Mozilla Firefox",
        ["brave.exe"] = "Brave",
        ["opera.exe"] = "Opera"
    };

    private static readonly Dictionary<string, string> Communication = new(StringComparer.OrdinalIgnoreCase)
    {
        ["discord.exe"] = "Discord",
        ["discordptb.exe"] = "Discord PTB",
        ["discordcanary.exe"] = "Discord Canary",
        ["teamspeak3.exe"] = "TeamSpeak",
        ["ts3client_win64.exe"] = "TeamSpeak 3",
        ["teamspeak.exe"] = "TeamSpeak",
        ["mumble.exe"] = "Mumble",
        ["guilded.exe"] = "Guilded",
        ["slack.exe"] = "Slack"
    };

    private static readonly string[] NotGames =
    [
        @"\steamapps\common\wallpaper_engine\",
        @"\steamapps\common\SteamVR\",
        @"\steamapps\common\Steamworks Shared\",
        @"\steamapps\common\Steam Controller Configs\"
    ];

    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public IReadOnlyList<InstalledApp> Discover()
    {
        var byPath = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in DiscoverRunning())
        {
            byPath[app.Identity.ExecutablePath!] = app;
        }

        foreach (var app in DiscoverInstalled())
        {
            // A running instance already carries better information, so it wins.
            if (!byPath.ContainsKey(app.Identity.ExecutablePath!))
            {
                byPath[app.Identity.ExecutablePath!] = app;
            }
        }

        return byPath.Values
            .OrderByDescending(a => a.IsRunning)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Applications with a live process right now.</summary>
    public IReadOnlyList<InstalledApp> DiscoverRunning()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is null || IsSystemPath(path))
                {
                    continue;
                }

                if (apps.ContainsKey(path))
                {
                    continue;
                }

                apps[path] = new InstalledApp
                {
                    Identity = AppIdentity.ForExecutable(path, FriendlyName(path, process)),
                    Category = Classify(path),
                    InstallLocation = Path.GetDirectoryName(path),
                    IsRunning = true
                };
            }
            catch (Exception)
            {
                // Reading MainModule fails for protected and cross-architecture processes.
                // Routine, and never a reason to abandon the whole enumeration.
            }
            finally
            {
                process.Dispose();
            }
        }

        return apps.Values.ToList();
    }

    /// <summary>Applications with an entry in the Windows installed-programs list.</summary>
    public IReadOnlyList<InstalledApp> DiscoverInstalled()
    {
        var apps = new List<InstalledApp>();

        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var keyPath in UninstallKeys)
            {
                using var key = root.OpenSubKey(keyPath);
                if (key is null)
                {
                    continue;
                }

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    var app = TryReadUninstallEntry(subKey);
                    if (app is not null)
                    {
                        apps.Add(app);
                    }
                }
            }
        }

        return apps;
    }

    private static InstalledApp? TryReadUninstallEntry(RegistryKey? key)
    {
        if (key is null)
        {
            return null;
        }

        var displayName = key.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        // Updates and system components are not things a user routes.
        if (key.GetValue("SystemComponent") is int and 1 || key.GetValue("ParentKeyName") is not null)
        {
            return null;
        }

        var executable = ResolveExecutable(key);
        if (executable is null || !File.Exists(executable))
        {
            return null;
        }

        return new InstalledApp
        {
            Identity = new AppIdentity
            {
                Kind = AppIdentityKind.Win32,
                DisplayName = displayName,
                ExecutablePath = executable,
                Publisher = key.GetValue("Publisher") as string
            },
            Category = Classify(executable),
            InstallLocation = Path.GetDirectoryName(executable)
        };
    }

    /// <summary>
    /// Uninstall entries do not record the main executable directly. DisplayIcon usually
    /// points at it; failing that, guess from the install directory.
    /// </summary>
    private static string? ResolveExecutable(RegistryKey key)
    {
        if (key.GetValue("DisplayIcon") is string icon && icon.Length > 0)
        {
            // DisplayIcon is often "C:\path\app.exe,0".
            var comma = icon.LastIndexOf(',');
            var path = (comma > 0 ? icon[..comma] : icon).Trim('"');

            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                return path;
            }
        }

        if (key.GetValue("InstallLocation") is string location &&
            !string.IsNullOrWhiteSpace(location) &&
            Directory.Exists(location))
        {
            try
            {
                var folderName = new DirectoryInfo(location).Name;

                // Rank rather than match exactly. Games rarely name the executable
                // exactly after the folder — DayZ ships DayZ_x64.exe alongside
                // DayZDiag_x64.exe — so an exact-match-or-give-up rule picks whichever
                // happened to be enumerated first. Preferring the shortest name that
                // still starts with the folder name selects the plain build over the
                // diagnostic, tools and helper variants that sit beside it.
                return Directory.GetFiles(location, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(e => !IsSupportExecutable(e))
                    .OrderBy(e => Rank(Path.GetFileNameWithoutExtension(e), folderName))
                    .ThenBy(e => Path.GetFileNameWithoutExtension(e).Length)
                    .FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Executables that ship alongside an application but are not the application.
    ///
    /// <para>Without this, DayZ resolves to <c>CrashReporter.exe</c> — which is the exact
    /// mistake §13 names. Routing a crash handler instead of the game would produce a
    /// rule that looks correct, verifies as "configured", and protects nothing, because
    /// the process actually carrying game traffic was never covered.</para>
    /// </summary>
    private static readonly string[] SupportExecutableMarkers =
    [
        "crashreport", "crashhandler", "crashpad", "unins", "setup", "installer",
        "updater", "update", "launcher_helper", "vcredist", "dxsetup", "dotnetfx",
        "redist", "diagnostic", "reporter", "cleanup", "helper", "service"
    ];

    /// <summary>Lower is better: exact folder-name match, then a prefix match, then anything.</summary>
    private static int Rank(string executableName, string folderName)
    {
        if (executableName.Equals(folderName, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return executableName.StartsWith(folderName, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static bool IsSupportExecutable(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return SupportExecutableMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Launcher, browser, chat, game or other, judged from the program's path.</summary>
    public static AppCategory CategoryOf(string executablePath) => Classify(executablePath);

    private static AppCategory Classify(string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);

        if (Launchers.ContainsKey(fileName))
        {
            return AppCategory.Launcher;
        }
        if (Browsers.ContainsKey(fileName))
        {
            return AppCategory.Browser;
        }
        if (Communication.ContainsKey(fileName))
        {
            return AppCategory.Communication;
        }

        // Things that live in Steam's games folder but aren't games.
        if (NotGames.Any(n => executablePath.Contains(n, StringComparison.OrdinalIgnoreCase)))
        {
            return AppCategory.Other;
        }

        // Steam games live under steamapps\common and are otherwise indistinguishable
        // from any other executable, so path is the only usable signal.
        return executablePath.Contains(@"steamapps\common", StringComparison.OrdinalIgnoreCase)
               || executablePath.Contains("XboxGames", StringComparison.OrdinalIgnoreCase)
            ? AppCategory.Game
            : AppCategory.Other;
    }

    private static string FriendlyName(string path, Process process)
    {
        var fileName = Path.GetFileName(path);

        if (Launchers.TryGetValue(fileName, out var launcher))
        {
            return launcher;
        }
        if (Browsers.TryGetValue(fileName, out var browser))
        {
            return browser;
        }
        if (Communication.TryGetValue(fileName, out var comms))
        {
            return comms;
        }

        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }
        }
        catch (Exception)
        {
            // Fall through to the process name.
        }

        return process.ProcessName;
    }

    /// <summary>
    /// Excludes Windows' own components. Routing these would be at best pointless and
    /// at worst destabilising, and §45 is clear that NetRoute stays out of the way of
    /// the operating system.
    /// </summary>
    private static bool IsSystemPath(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }
}
