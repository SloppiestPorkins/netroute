using System.Text.RegularExpressions;

namespace NetRoute.Core.Policy;

/// <summary>
/// Keeps rules working for apps that install every update into a new folder.
///
/// <para>Discord, Slack and other Squirrel-installed apps live at
/// <c>%LocalAppData%\Discord\app-1.0.9251\Discord.exe</c>, and the next update runs from
/// <c>app-1.0.9252</c>. A rule that stored the exact path would keep "protecting" a file
/// that no longer runs, while the real Discord quietly used the default network. It would
/// look fine and do nothing, which is the failure §24 exists to prevent.</para>
///
/// <para>Resolution happens whenever a plan is built. The service rebuilds every 10 seconds,
/// so an update is picked up without the user doing anything.</para>
/// </summary>
public static partial class VersionedPaths
{
    [GeneratedRegex(@"^(?<root>.+)\\app-(?<version>\d+(?:\.\d+){1,3})\\(?<rest>[^\\].*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SquirrelLayout();

    /// <summary>
    /// Points <paramref name="app"/> at the newest installed version of its program, and at the
    /// app's root folder, so every version's files are covered. Anything else is returned as-is.
    /// </summary>
    public static AppIdentity Resolve(AppIdentity app)
    {
        if (app.Kind != AppIdentityKind.Win32 || app.ExecutablePath is not { } path)
        {
            return app;
        }

        var match = SquirrelLayout().Match(path);
        if (!match.Success)
        {
            return app;
        }

        var root = match.Groups["root"].Value;
        var rest = match.Groups["rest"].Value;
        var newest = Newest(root, rest) ?? path;

        return app with { ExecutablePath = newest, InstallLocation = root };
    }

    private static string? Newest(string root, string relativeFile)
    {
        string? best = null;
        Version? bestVersion = null;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "app-*"))
            {
                if (!Version.TryParse(Path.GetFileName(dir)[4..], out var version))
                {
                    continue;
                }
                var candidate = Path.Combine(dir, relativeFile);
                if ((bestVersion is null || version > bestVersion) && File.Exists(candidate))
                {
                    best = candidate;
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
