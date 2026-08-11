using Microsoft.Win32;

namespace NetRoute.Windows.Apps;

/// <summary>
/// The set of packages installed through Xbox / Game Pass, read from the Gaming
/// Services package repository.
///
/// <para>This replaces guesswork. Inferring Game Pass membership from install paths or
/// package dependencies does not work: the games' packages live under
/// <c>WindowsApps</c> like any other Store app, their content sits in a separate
/// <c>XboxGames</c> folder, and they do not declare a Gaming Services dependency. The
/// repository is where Xbox itself records what it installed, so it is both accurate
/// and stable across title updates.</para>
///
/// <para>Read-only, and no elevation needed.</para>
/// </summary>
public sealed class GamePassCatalog
{
    private const string RepositoryRoot =
        @"SOFTWARE\Microsoft\GamingServices\PackageRepository\Root";

    private readonly HashSet<string> _packageFamilies;

    private GamePassCatalog(HashSet<string> packageFamilies) => _packageFamilies = packageFamilies;

    public int Count => _packageFamilies.Count;

    public bool Contains(string packageFamilyName) => _packageFamilies.Contains(packageFamilyName);

    public static GamePassCatalog Load()
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(RepositoryRoot);
            if (root is null)
            {
                // Gaming Services is not installed. A machine with no Game Pass is a
                // normal machine, not an error.
                return new GamePassCatalog(families);
            }

            foreach (var entryName in root.GetSubKeyNames())
            {
                using var entry = root.OpenSubKey(entryName);
                if (entry is null)
                {
                    continue;
                }

                // Each entry holds one child per installed root, carrying the package full name.
                foreach (var childName in entry.GetSubKeyNames())
                {
                    using var child = entry.OpenSubKey(childName);
                    if (child?.GetValue("Package") is string fullName)
                    {
                        var family = ToFamilyName(fullName);
                        if (family is not null)
                        {
                            families.Add(family);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // A readable-but-malformed repository should degrade to "no Game Pass games
            // detected" rather than break application discovery entirely.
        }

        return new GamePassCatalog(families);
    }

    /// <summary>
    /// Converts a package full name to its family name.
    ///
    /// <para>"Microsoft.Foo_1.0.1.0_x64__8wekyb3d8bbwe" becomes
    /// "Microsoft.Foo_8wekyb3d8bbwe" — the name, then the publisher ID that follows
    /// the double underscore.</para>
    /// </summary>
    internal static string? ToFamilyName(string packageFullName)
    {
        var separator = packageFullName.LastIndexOf("__", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var publisherId = packageFullName[(separator + 2)..];
        var name = packageFullName[..separator];

        var firstUnderscore = name.IndexOf('_');
        if (firstUnderscore > 0)
        {
            name = name[..firstUnderscore];
        }

        return string.IsNullOrEmpty(publisherId) || string.IsNullOrEmpty(name)
            ? null
            : $"{name}_{publisherId}";
    }
}
