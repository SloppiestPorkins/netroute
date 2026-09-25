namespace NetRoute.Core.Policy;

/// <summary>
/// Whether a rule covers a running program: the same file, one of the app's own files, or the
/// same package. One definition, used by enforcement, verification, the live views and
/// Check-up, so they can never disagree about what "Steam" means.
/// </summary>
public static class AppMatching
{
    /// <summary>
    /// Game libraries that can sit directly inside a launcher's own folder: Steam\steamapps,
    /// Ubisoft Game Launcher\games, GOG Galaxy\Games. A launcher's folder isn't its games, so
    /// putting Steam on Downloads must never pull every game in its default library with it.
    /// </summary>
    public static IReadOnlyList<string> LibraryFolders { get; } = ["steamapps", "games"];

    public static bool Covers(AppIdentity app, string? executablePath, string? packageFamilyName)
    {
        if (app.Kind == AppIdentityKind.Packaged)
        {
            return packageFamilyName is not null
                   && string.Equals(app.PackageFamilyName, packageFamilyName, StringComparison.OrdinalIgnoreCase);
        }
        if (executablePath is null)
        {
            return false;
        }
        if (app.Kind == AppIdentityKind.Folder)
        {
            // The user pointed at this folder deliberately, so a games library inside it is included.
            return app.InstallLocation is { Length: > 0 } folder
                   && executablePath.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(app.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)
               || (app.InstallLocation is { Length: > 0 } root && IsOwnFile(root, executablePath));
    }

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
        var rest = path[(root.Length + 1)..];
        var slash = rest.IndexOf('\\');
        return slash < 0 || !LibraryFolders.Contains(rest[..slash], StringComparer.OrdinalIgnoreCase);
    }
}
