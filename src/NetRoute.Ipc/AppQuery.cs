using NetRoute.Core.Policy;

namespace NetRoute.Ipc;

/// <summary>Which kind of text a typed app query was matched against.</summary>
public enum AppQueryStage
{
    None,
    ExactName,
    Name,
    Path,
    Package
}

/// <summary>The first stage of <see cref="AppQuery"/> that found anything, and what it found.</summary>
public sealed record AppQueryResult<T>(AppQueryStage Stage, IReadOnlyList<T> Matches)
{
    /// <summary>The one match, or null when nothing matched or several did.</summary>
    public T? Single => Matches.Count == 1 ? Matches[0] : default;
}

/// <summary>
/// Works out which app a user meant by what they typed. Stages run in order, and the first that
/// finds anything decides: exact name, then part of the name, then part of the program's path,
/// then part of the package family name. Each stage can rescue the next, so a path or file name
/// works even when no name contains it.
/// </summary>
public static class AppQuery
{
    public static AppQueryResult<T> Resolve<T>(IEnumerable<T> items, Func<T, AppIdentity> identityOf, string query)
    {
        var list = items.ToList();
        if (string.IsNullOrWhiteSpace(query))
        {
            return new AppQueryResult<T>(AppQueryStage.None, []);
        }
        query = query.Trim();

        var stages = new (AppQueryStage Stage, Func<T, bool> Test)[]
        {
            (AppQueryStage.ExactName, a => identityOf(a).DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase)),
            (AppQueryStage.Name, a => identityOf(a).DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)),
            (AppQueryStage.Path, a => PathContains(identityOf(a), query)),
            (AppQueryStage.Package, a => identityOf(a).PackageFamilyName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true),
        };
        foreach (var (stage, test) in stages)
        {
            var matches = list.Where(test).ToList();
            if (matches.Count > 0)
            {
                return new AppQueryResult<T>(stage, matches);
            }
        }
        return new AppQueryResult<T>(AppQueryStage.None, []);
    }

    private static bool PathContains(AppIdentity app, string query)
    {
        var wanted = Normalise(query);
        return wanted.Length > 0
            && (Normalise(app.ExecutablePath).Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || Normalise(app.InstallLocation).Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Backslashes only, and no trailing separator, so "C:/Games/Foo/" finds "C:\Games\Foo\foo.exe".</summary>
    private static string Normalise(string? path)
        => (path ?? "").Replace('/', '\\').TrimEnd('\\');
}
