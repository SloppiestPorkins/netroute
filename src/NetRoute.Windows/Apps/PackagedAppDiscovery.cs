using NetRoute.Core.Policy;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace NetRoute.Windows.Apps;

/// <summary>
/// Finds packaged applications — Microsoft Store and Xbox / Game Pass titles.
///
/// <para>§10 requires the user to add a Game Pass game by clicking its name, never by
/// locating an executable, and §45 forbids touching <c>C:\Program Files\WindowsApps</c>.
/// Both are satisfied by going through package identity, which is the supported route
/// and also the only one that survives the game being updated.</para>
/// </summary>
public sealed class PackagedAppDiscovery
{
    private static readonly HashSet<string> LauncherFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.GamingApp",       // Xbox app
        "Microsoft.XboxApp",
        "Microsoft.GamingServices"
    };

    public async Task<IReadOnlyList<InstalledApp>> DiscoverAsync()
    {
        var manager = new PackageManager();
        var gamePass = GamePassCatalog.Load();
        var apps = new List<InstalledApp>();

        // Empty user string scopes the query to the current user, which needs no
        // elevation. Querying across users would, and NetRoute has no reason to.
        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            // Framework, resource and bundle packages are never things a user routes.
            if (SafeGet(() => package.IsFramework) || SafeGet(() => package.IsResourcePackage))
            {
                continue;
            }

            var discovered = await TryDescribe(package, gamePass);
            apps.AddRange(discovered);
        }

        return apps
            .GroupBy(a => a.Identity.StableKey)
            .Select(g => g.First())
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<InstalledApp>> TryDescribe(
        Package package, GamePassCatalog gamePass)
    {
        string familyName;
        try
        {
            familyName = package.Id.FamilyName;
        }
        catch (Exception)
        {
            // A package whose identity cannot be read cannot be routed either.
            return [];
        }

        var installLocation = SafeGet(() => package.InstalledLocation?.Path);
        var isGamePass = gamePass.Contains(familyName);

        // One package can contain several applications, each with its own AUMID (§11).
        // Enumerating the app list rather than the package keeps them distinguishable.
        var entries = await SafeGetAsync(async () => await package.GetAppListEntriesAsync());

        if (entries is null || entries.Count == 0)
        {
            // Nothing launchable. For Game Pass entries this is how DLC, skin packs and
            // art collections present themselves — they are registered as packages and
            // sit in the Gaming Services repository, but there is no application to run
            // and therefore nothing to route. Offering them would pad the picker with
            // items that can never carry traffic.
            return [];
        }

        var apps = new List<InstalledApp>();
        foreach (var entry in entries)
        {
            // Manifest display names arrive XML-escaped, so a title containing a pipe
            // or ampersand would otherwise be shown to the user as "&#124;" / "&amp;".
            var displayName = System.Net.WebUtility.HtmlDecode(
                SafeGet(() => entry.DisplayInfo.DisplayName)
                ?? SafeGet(() => package.DisplayName)
                ?? familyName);

            var aumid = SafeGet(() => entry.AppUserModelId);
            apps.Add(Build(familyName, displayName, aumid, installLocation, isGamePass));
        }

        return apps;
    }

    private static InstalledApp Build(
        string familyName, string displayName, string? aumid, string? installLocation, bool isGamePass)
    {
        var category = isGamePass
            ? AppCategory.Game
            : LauncherFamilies.Contains(TrimFamily(familyName))
                ? AppCategory.Launcher
                : AppCategory.Other;

        return new InstalledApp
        {
            Identity = AppIdentity.ForPackage(familyName, displayName, aumid),
            Category = category,
            InstallLocation = installLocation,
            IsGamePass = isGamePass
        };
    }

    /// <summary>Strips the publisher hash, turning "Foo.Bar_8wekyb3d8bbwe" into "Foo.Bar".</summary>
    private static string TrimFamily(string familyName)
    {
        var underscore = familyName.LastIndexOf('_');
        return underscore > 0 ? familyName[..underscore] : familyName;
    }

    // The packaged-app APIs throw for packages that are staged, partially removed, or
    // otherwise inaccessible. That is routine on a real machine, so failure to read one
    // package must never abort the enumeration.

    private static T? SafeGet<T>(Func<T> get)
    {
        try
        {
            return get();
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static async Task<T?> SafeGetAsync<T>(Func<Task<T>> get)
    {
        try
        {
            return await get();
        }
        catch (Exception)
        {
            return default;
        }
    }
}
