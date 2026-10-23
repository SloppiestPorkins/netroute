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

    /// <summary>Edge updated and deleted its old version folder; the rule must follow to the new one.</summary>
    [Fact]
    public void ChromiumRuleFollowsAnUpdateWhenTheOldVersionFolderIsGone()
    {
        var top = TempTop();
        try
        {
            var app = Path.Combine(top, "Microsoft", "EdgeWebView", "Application");
            var stale = Path.Combine(app, "153.0.4234.48", "msedgewebview2.exe");
            var fresh = Make(app, "155.0.4283.45", "msedgewebview2.exe");

            var resolved = VersionedPaths.Resolve(AppIdentity.ForExecutable(stale, "WebView2"));

            Assert.Equal(fresh, resolved.ExecutablePath, ignoreCase: true);
            Assert.Equal(app, resolved.InstallLocation, ignoreCase: true);
        }
        finally { Directory.Delete(top, recursive: true); }
    }

    [Fact]
    public void ChromiumPicksTheNewestVersionThatHasTheFile()
    {
        var top = TempTop();
        try
        {
            var app = Path.Combine(top, "Chrome", "Application");
            Make(app, "9.0.1.1", "chrome.exe");               // older, though it sorts later as text
            var newest = Make(app, "126.0.6478.55", "chrome.exe");
            Make(app, "127.0.6533.0", "chrome_proxy.exe");    // newer folder, but no chrome.exe in it
            Directory.CreateDirectory(Path.Combine(app, "Installer"));

            var resolved = VersionedPaths.Resolve(AppIdentity.ForExecutable(Path.Combine(app, "100.0.1.1", "chrome.exe")));

            Assert.Equal(newest, resolved.ExecutablePath, ignoreCase: true);
        }
        finally { Directory.Delete(top, recursive: true); }
    }

    /// <summary>Edge's stable binary has no version folder, so a vanished versioned msedge.exe falls back to it.</summary>
    [Fact]
    public void ChromiumFallsBackToTheLauncherInTheApplicationFolder()
    {
        var top = TempTop();
        try
        {
            var app = Path.Combine(top, "Microsoft", "Edge", "Application");
            Directory.CreateDirectory(app);
            var launcher = Path.Combine(app, "msedge.exe");
            File.WriteAllText(launcher, "");
            Make(app, "155.0.4283.45", "msedge.dll");          // a version folder, but not holding msedge.exe

            var resolved = VersionedPaths.Resolve(AppIdentity.ForExecutable(Path.Combine(app, "153.0.4234.48", "msedge.exe")));

            Assert.Equal(launcher, resolved.ExecutablePath, ignoreCase: true);
            Assert.Equal(app, resolved.InstallLocation, ignoreCase: true);
        }
        finally { Directory.Delete(top, recursive: true); }
    }

    /// <summary>A program that moved for good must come back untouched, as it did before this layout was handled.</summary>
    [Fact]
    public void ChromiumLeavesAPathThatCannotBeHealed()
    {
        var top = TempTop();
        try
        {
            var app = Path.Combine(top, "Game", "Application");
            Make(app, "2.0.0", "other.exe");
            var gone = AppIdentity.ForExecutable(Path.Combine(app, "1.0.0.0", "game.exe"));
            Assert.Same(gone, VersionedPaths.Resolve(gone));

            var missingRoot = AppIdentity.ForExecutable(Path.Combine(top, "Nowhere", "Application", "1.2.3.4", "x.exe"));
            Assert.Same(missingRoot, VersionedPaths.Resolve(missingRoot));
        }
        finally { Directory.Delete(top, recursive: true); }
    }

    [Fact]
    public void ChromiumNeverResolvesOutsideTheApplicationFolder()
    {
        var top = TempTop();
        try
        {
            var app = Path.Combine(top, "Edge", "Application");
            Make(app, "155.0.1.1", "x.exe");
            File.WriteAllText(Path.Combine(top, "secret.exe"), "");
            var escape = AppIdentity.ForExecutable(Path.Combine(app, "153.0.1.1") + @"\..\..\secret.exe");
            Assert.Same(escape, VersionedPaths.Resolve(escape));
        }
        finally { Directory.Delete(top, recursive: true); }
    }

    private static string TempTop() => Path.Combine(Path.GetTempPath(), "nr-" + Guid.NewGuid().ToString("N"));

    private static string Make(string root, string version, string file)
    {
        var dir = Path.Combine(root, version);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, "");
        return path;
    }
}
