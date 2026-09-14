using System.Net;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Tests.Service;
using NetRoute.Windows.Split;
using NetRoute.Windows.Traffic;

namespace NetRoute.Tests.Traffic;

public sealed class LocalNetworkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"NetRoute.Steam.{Guid.NewGuid():N}");

    [Theory]
    [InlineData("192.168.0.81", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.10.10", true)]
    [InlineData("239.255.255.250", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12::5", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("162.254.196.26", false)]
    [InlineData("2a00:1450::1", false)]
    public void KnowsTheLocalNetworkFromTheInternet(string address, bool local)
        => Assert.Equal(local, LocalNetwork.Contains(IPAddress.Parse(address)));

    [Fact]
    public void ACastDeviceOnTheOtherLanIsNotALeak()
    {
        // Steam on Downloads (Wi-Fi), talking to a Chromecast on the Ethernet LAN: seen for real on 14 Sept.
        var steam = AppIdentity.ForExecutable(@"C:\Steam\steam.exe", "Steam");
        var app = new AppEnforcement
        {
            Rule = AppRule.Create(steam, RoleId.Downloads), Action = EnforcementAction.PinToAdapter,
            ResolvedAdapter = NetRouteEngineTests.Wifi(), BlockIpv6 = true, Reasons = []
        };
        var cast = new ObservedConnection(TransportProtocol.Tcp, IPEndPoint.Parse("192.168.1.10:50000"), IPEndPoint.Parse("192.168.0.81:8009"),
            42, "steam", @"C:\Steam\steam.exe", null, 11, "Ethernet", 11, false, "Established", DateTimeOffset.UtcNow);

        var verdict = TrafficVerdicts.For(app, [cast], DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.NotEqual(VerificationState.Leak, verdict.State);
        Assert.Empty(verdict.Leaks);
        Assert.Equal(0, verdict.PreexistingConnections);
    }

    [Fact]
    public void ALaunchersRuleCoversItsHelpersButNotItsGames()
    {
        Directory.CreateDirectory(Path.Combine(_root, @"bin\cef"));
        Directory.CreateDirectory(Path.Combine(_root, @"steamapps\common\Halo"));
        foreach (var file in new[] { "steam.exe", @"bin\cef\steamwebhelper.exe", @"steamapps\common\Halo\halo.exe" })
        {
            File.WriteAllText(Path.Combine(_root, file), "");
        }
        var steam = AppIdentity.ForExecutable(Path.Combine(_root, "steam.exe"), "Steam") with { InstallLocation = _root };

        var images = SplitImagePaths.For(steam);

        Assert.Contains(images, p => p.EndsWith("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(images, p => p.EndsWith("halo.exe", StringComparison.OrdinalIgnoreCase));
        Assert.True(SplitImagePaths.IsOwnFile(_root, Path.Combine(_root, @"bin\cef\steamwebhelper.exe")));
        Assert.False(SplitImagePaths.IsOwnFile(_root, Path.Combine(_root, @"steamapps\common\Halo\halo.exe")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
