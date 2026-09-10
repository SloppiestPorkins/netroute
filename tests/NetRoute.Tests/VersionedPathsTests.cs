using NetRoute.Core.Policy;
using NetRoute.Windows.Split;
using Xunit;

namespace NetRoute.Tests;

public class VersionedPathsTests
{
    /// <summary>
    /// Discord updates into a new app-x.y.z folder. A rule added before the update must follow
    /// it to the new program, or it goes on protecting a file that no longer runs.
    /// </summary>
    [Fact]
    public void FollowsAnUpdateIntoTheNewestVersionFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "nr-" + Guid.NewGuid().ToString("N"), "Discord");
        try
        {
            var oldExe = Make(root, "app-1.0.9251", "Discord.exe");
            var newExe = Make(root, "app-1.0.9252", "Discord.exe");
            Make(root, "app-1.0.10000", "Other.exe");   // newer, but not this program

            var resolved = VersionedPaths.Resolve(AppIdentity.ForExecutable(oldExe, "Discord"));

            Assert.Equal(newExe, resolved.ExecutablePath, ignoreCase: true);
            Assert.Equal(root, resolved.InstallLocation, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Fact]
    public void KeepsThePathWhenTheOldVersionIsTheOnlyOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "nr-" + Guid.NewGuid().ToString("N"), "Slack");
        try
        {
            var exe = Make(root, "app-4.41.1", "slack.exe");
            Assert.Equal(exe, VersionedPaths.Resolve(AppIdentity.ForExecutable(exe)).ExecutablePath, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    [Fact]
    public void LeavesOrdinaryPathsAndPackagedAppsAlone()
    {
        var normal = AppIdentity.ForExecutable(@"C:\Program Files (x86)\Steam\steam.exe", "Steam");
        Assert.Same(normal, VersionedPaths.Resolve(normal));

        var packaged = AppIdentity.ForPackage("Microsoft.198377053870B_8wekyb3d8bbwe", "Halo");
        Assert.Same(packaged, VersionedPaths.Resolve(packaged));
    }

    /// <summary>The root of a user's profile or AppData must never be treated as one app's folder.</summary>
    [Theory]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\Jack")]
    [InlineData(@"C:\Users\Jack\AppData")]
    [InlineData(@"C:\Users\Jack\AppData\Local")]
    [InlineData(@"C:\Users\Jack\AppData\Roaming")]
    [InlineData(@"C:\Users\Jack\AppData\Local\Programs")]
    public void UserProfileRootsAreNeverExpanded(string root)
    {
        Assert.False(SplitImagePaths.IsSafeRoot(root));
    }

    private static string Make(string root, string version, string file)
    {
        var dir = Path.Combine(root, version);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, "");
        return path;
    }
}
