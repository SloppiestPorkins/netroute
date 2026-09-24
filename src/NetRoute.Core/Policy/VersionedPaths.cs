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

        return app;
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
