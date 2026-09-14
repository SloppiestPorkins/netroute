using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using NetRoute.Tests.Service;
using NetRoute.Windows.Apps;
using NetRoute.Windows.Wfp;

namespace NetRoute.Tests.Windows;

public sealed class SystemDownloadsTests
{
    [Fact]
    public void ServiceSidMatchesWindows()
        // Known value: NT SERVICE\TrustedInstaller.
        => Assert.Equal("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", ServiceSids.For("TrustedInstaller"));

    [Fact]
    public void SystemDownloadsFollowDownloadsAndBlockIpv6WhenItHasNone()
    {
        var source = new NetRouteEngineTests.MutableAdapterSource(NetRouteEngineTests.Ethernet(), NetRouteEngineTests.Wifi());
        var config = new NetRouteConfig
        {
            SetupCompleted = true,
            RoleBindings = [Bind(RoleId.Gaming, NetRouteEngineTests.Ethernet()), Bind(RoleId.Downloads, NetRouteEngineTests.Wifi())]
        };

        var plan = new PolicyResolver(source).Resolve(config).SystemDownloads;
        Assert.NotNull(plan);
        Assert.Equal("Wi-Fi", plan!.Adapter.Name);
        Assert.True(plan.BlockIpv6);
        Assert.Contains("DoSvc", plan.Services);

        Assert.Null(new PolicyResolver(source).Resolve(config with { EnforcementPaused = true }).SystemDownloads);
        Assert.Null(new PolicyResolver(source).Resolve(config with { RouteSystemDownloads = false }).SystemDownloads);

        // Never cut Windows Update off because the Downloads network dropped.
        source.Adapters = [NetRouteEngineTests.Ethernet(), NetRouteEngineTests.Wifi(AdapterState.Disconnected)];
        Assert.Null(new PolicyResolver(source).Resolve(config).SystemDownloads);
    }

    [Theory]
    [InlineData(@"G:\SteamLibrary\steamapps\common\Halo Infinite\HaloInfinite.exe", @"G:\SteamLibrary\steamapps\common\Halo Infinite", "Halo Infinite")]
    [InlineData(@"C:\XboxGames\Forza Horizon 5\Content\ForzaHorizon5.exe", @"C:\XboxGames\Forza Horizon 5", "Forza Horizon 5")]
    [InlineData(@"C:\Program Files\EA Games\Battlefield 6\bf6.exe", @"C:\Program Files\EA Games\Battlefield 6", "Battlefield 6")]
    public void GameFoldersFindTheWholeGame(string program, string root, string name)
    {
        Assert.True(GameFolders.TryGetGameRoot(program, out var foundRoot, out var foundName));
        Assert.Equal(root, foundRoot);
        Assert.Equal(name, foundName);
    }

    [Theory]
    [InlineData(@"G:\SteamLibrary\steamapps\common\wallpaper_engine\wallpaper64.exe")]
    [InlineData(@"G:\SteamLibrary\steamapps\common\Halo Infinite\UnityCrashHandler64.exe")]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe")]
    [InlineData(@"C:\Program Files\Microsoft OneDrive\OneDrive.exe")]
    public void NotEverythingInALibraryIsAGame(string program)
        => Assert.False(GameFolders.TryGetGameRoot(program, out _, out _));

    private static RoleBinding Bind(RoleId role, NetworkAdapter adapter)
        => new() { Role = role, AdapterLuid = adapter.Luid, AdapterGuid = adapter.Guid, LastKnownName = adapter.Name };
}
