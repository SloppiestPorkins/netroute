using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using NetRoute.Tests.Service;

namespace NetRoute.Tests.Policy;

public sealed class LocalHostingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"NetRoute.Store.{Guid.NewGuid():N}");

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.4297127D64EC6_2.6.2.0_x64__8wekyb3d8bbwe\Minecraft.exe", true)]
    [InlineData(@"C:\Users\Jack\AppData\Local\Programs\CurseForge Windows\CurseForge.exe", true)]
    [InlineData(@"G:\WpSystem\...\runtime\java-runtime-epsilon\windows-x64\javaw.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", false)]
    [InlineData(@"G:\SteamLibrary\steamapps\common\Battlefield 6\bf6.exe", false)]
    public void KnowsWhichAppsHostOnTheLocalNetwork(string path, bool local)
        => Assert.Equal(local, LocalHostingApps.Includes(AppIdentity.ForExecutable(path)));

    [Fact]
    public void MinecraftPackagesAreRecognisedByPackageFamily()
        => Assert.True(LocalHostingApps.Includes(AppIdentity.ForPackage("Microsoft.MinecraftUWP_8wekyb3d8bbwe", "Minecraft")));

    [Fact]
    public void AnAppThatHostsLanGamesIsNeverMoved()
    {
        // Assigning Minecraft to Gaming must not enforce anything: moving it breaks LAN worlds
        // and can stop it starting (24 Sept 2026). It has to say so, not fail quietly.
        var source = new NetRouteEngineTests.MutableAdapterSource(NetRouteEngineTests.Ethernet(), NetRouteEngineTests.Wifi());
        var minecraft = AppIdentity.ForExecutable(@"C:\Program Files\WindowsApps\Microsoft.4297127D64EC6_2.6.2.0_x64__8wekyb3d8bbwe\Minecraft.exe", "Minecraft");
        var config = new NetRouteConfig
        {
            SetupCompleted = true,
            RoleBindings =
            [
                Bind(RoleId.Gaming, NetRouteEngineTests.Ethernet()),
                Bind(RoleId.Downloads, NetRouteEngineTests.Wifi())
            ],
            AppRules = [AppRule.Create(minecraft, RoleId.Gaming)]
        };

        var app = new PolicyResolver(source).Resolve(config).Applications.Single();

        Assert.Equal(EnforcementAction.None, app.Action);
        Assert.Contains(app.Reasons, r => !r.Good && r.Text.Contains("local network", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AStoreAppsRuleFollowsItToTheNextVersion()
    {
        // Store apps install each update beside the last: Name_2.6.2.0_x64__hash, then _2.7.0.0_.
        var store = Path.Combine(_root, "WindowsApps");
        var old = Path.Combine(store, "Microsoft.4297127D64EC6_2.6.2.0_x64__8wekyb3d8bbwe");
        var current = Path.Combine(store, "Microsoft.4297127D64EC6_2.7.0.0_x64__8wekyb3d8bbwe");
        var other = Path.Combine(store, "Microsoft.SomethingElse_9.9.9.9_x64__8wekyb3d8bbwe");
        foreach (var dir in new[] { old, current, other })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Minecraft.exe"), "");
        }

        var resolved = VersionedPaths.Resolve(AppIdentity.ForExecutable(Path.Combine(old, "Minecraft.exe"), "Minecraft"));

        Assert.Equal(Path.Combine(current, "Minecraft.exe"), resolved.ExecutablePath);
        // The package folder, never the whole WindowsApps folder, which holds every app on the PC.
        Assert.Equal(current, resolved.InstallLocation);
    }

    private static RoleBinding Bind(RoleId role, NetworkAdapter adapter)
        => new() { Role = role, AdapterLuid = adapter.Luid, AdapterGuid = adapter.Guid, LastKnownName = adapter.Name };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
