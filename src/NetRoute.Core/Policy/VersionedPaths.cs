using System.Text.RegularExpressions;

namespace NetRoute.Core.Policy;

/// <summary>
/// Keeps rules working for apps that install every update into a new folder.
///
/// <para>Two layouts do this. Squirrel apps (Discord, Slack) live at
/// <c>%LocalAppData%\Discord\app-1.0.9251\Discord.exe</c>, and the next update runs from
/// <c>app-1.0.9252</c>. Store and Game Pass apps live at
/// <c>C:\Program Files\WindowsApps\Microsoft.MinecraftUWP_1.26.4501.0_x64__8wekyb3d8bbwe</c>,
/// and an update installs beside it under a new version. A rule that stored the exact path
/// would keep "protecting" a file that no longer runs, while the real app quietly used the
/// default network. It would look fine and do nothing, which is the failure §24 exists to
/// prevent.</para>
///
/// <para>Chromium browsers and WebView2 are the third. Edge keeps its helpers at
/// <c>Microsoft\Edge\Application\155.0.4283.45\msedge.exe</c>, every update creates a new
/// version folder, and the old one is deleted later. A rule pointing into the deleted folder
/// cannot be installed at all (Windows reports the path as not found), so the browser would
/// carry on, unfiltered, after its first update. Edge's launcher sits one level up, at
/// <c>Application\msedge.exe</c>, so that is the fallback when no version folder has the file.</para>
///
/// <para>Resolution happens whenever a plan is built. The service rebuilds every 10 seconds,
/// so an update is picked up without the user doing anything.</para>
/// </summary>
public static partial class VersionedPaths
{
    [GeneratedRegex(@"^(?<root>.+)\\app-(?<version>\d+(?:\.\d+){1,3})\\(?<rest>[^\\].*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SquirrelLayout();

    [GeneratedRegex(@"^(?<root>.+\\WindowsApps)\\(?<name>[^\\]+?)_(?<version>\d+(?:\.\d+){1,3})_(?<arch>[^_\\]+)__(?<publisher>[^\\]+)\\(?<rest>[^\\].*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PackageLayout();

    // Three or four parts, so a folder named "1.0" or a date is not mistaken for a browser version.
    [GeneratedRegex(@"^(?<root>.+\\Application)\\(?<version>\d+(?:\.\d+){2,3})\\(?<rest>[^\\].*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChromiumLayout();

    /// <summary>
    /// Points <paramref name="app"/> at the newest installed version of its program, and at the
    /// folder that covers that version's files. Anything else is returned as-is.
    /// </summary>
    public static AppIdentity Resolve(AppIdentity app)
    {
        if (app.Kind != AppIdentityKind.Win32 || app.ExecutablePath is not { } path)
        {
            return app;
        }

        var squirrel = SquirrelLayout().Match(path);
        if (squirrel.Success)
        {
            var root = squirrel.Groups["root"].Value;
            var rest = squirrel.Groups["rest"].Value;
            var folder = NewestFolder(root, "app-*", "app-".Length, rest);
            // The app's own root, so program files an update adds are covered too.
            return app with { ExecutablePath = folder is null ? path : Path.Combine(folder, rest), InstallLocation = root };
        }

        var package = PackageLayout().Match(path);
        if (package.Success)
        {
            var root = package.Groups["root"].Value;
            var name = package.Groups["name"].Value;
            var rest = package.Groups["rest"].Value;
            var pattern = $"{name}_*_{package.Groups["arch"].Value}__{package.Groups["publisher"].Value}";
            var folder = NewestFolder(root, pattern, name.Length + 1, rest);
            // One package folder, never the whole WindowsApps folder, which holds every app on the PC.
            return folder is null
                ? app
                : app with { ExecutablePath = Path.Combine(folder, rest), InstallLocation = folder };
        }

        var chromium = ChromiumLayout().Match(path);
        if (chromium.Success)
        {
            var root = chromium.Groups["root"].Value;
            var resolved = ResolveChromium(root, chromium.Groups["rest"].Value);
            // The Application folder holds the launcher and every version, so a later update
            // stays covered. It is the browser's own folder, never the vendor's parent.
            return resolved is null ? app : app with { ExecutablePath = resolved, InstallLocation = root };
        }

        return app;
    }

    /// <summary>
    /// The newest numbered folder under <paramref name="root"/> that holds <paramref name="relativeFile"/>,
    /// else the same file directly in <paramref name="root"/>. Null when neither exists, so the caller
    /// keeps the original path.
    /// </summary>
    private static string? ResolveChromium(string root, string relativeFile)
    {
        // The path comes from a saved rule; never let it climb out of the application folder.
        if (relativeFile.Split('\\', '/').Contains(".."))
        {
            return null;
        }

        string? best = null;
        Version? bestVersion = null;
        try
        {
            // One listing of a handful of entries, not a tree walk.
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (!Version.TryParse(Path.GetFileName(dir), out var version) || version.Build < 0)
                {
                    continue;
                }
                if ((bestVersion is null || version > bestVersion) && File.Exists(Path.Combine(dir, relativeFile)))
                {
                    best = Path.Combine(dir, relativeFile);
                    bestVersion = version;
                }
            }

            if (best is null && File.Exists(Path.Combine(root, relativeFile)))
            {
                best = Path.Combine(root, relativeFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable or gone: the rule keeps what it had.
        }
        return best;
    }

    /// <summary>The newest folder matching <paramref name="pattern"/> that still contains <paramref name="relativeFile"/>.</summary>
    private static string? NewestFolder(string root, string pattern, int versionAt, string relativeFile)
    {
        string? best = null;
        Version? bestVersion = null;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, pattern))
            {
                var name = Path.GetFileName(dir);
                if (name.Length <= versionAt || !Version.TryParse(name[versionAt..].Split('_')[0], out var version))
                {
                    continue;
                }
                if ((bestVersion is null || version > bestVersion) && File.Exists(Path.Combine(dir, relativeFile)))
                {
                    best = dir;
                    bestVersion = version;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder is unreadable or gone: keep whatever the rule already had.
        }
        return best;
    }
}
