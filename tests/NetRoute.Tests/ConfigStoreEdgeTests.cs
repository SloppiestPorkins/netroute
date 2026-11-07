using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using Xunit;

namespace NetRoute.Tests;

/// <summary>Edge cases for reading and writing the config file.</summary>
public sealed class ConfigStoreEdgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netroute-config-edge-" + Guid.NewGuid().ToString("N"));

    public ConfigStoreEdgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PathFor(string name = "config.json") => Path.Combine(_dir, name);

    /// <summary>An empty file is not a valid config. It must not be overwritten silently.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void EmptyOrWhitespaceFileIsTreatedAsUnreadable(string contents)
    {
        var path = PathFor();
        File.WriteAllText(path, contents);
        var store = new ConfigStore(path);

        store.Load();

        Assert.True(store.IsCorrupt);
        Assert.Throws<ConfigUnreadableException>(() => store.Update(c => c with { SetupCompleted = true }));
        Assert.Equal(contents, File.ReadAllText(path));
    }

    /// <summary>A UTF-8 byte order mark, as Notepad and some tools add, must still parse.</summary>
    [Fact]
    public void FileWithUtf8ByteOrderMarkStillLoads()
    {
        var path = PathFor();
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("{\"SetupCompleted\": true}")]);
        var store = new ConfigStore(path);

        var config = store.Load();

        Assert.False(store.IsCorrupt);
        Assert.True(config.SetupCompleted);
    }

    /// <summary>A leftover temp file from a crash must not stop the next save.</summary>
    [Fact]
    public void LeftoverTempFileDoesNotBlockSave()
    {
        var path = PathFor();
        var store = new ConfigStore(path);
        store.Save(new NetRouteConfig { SetupCompleted = true });
        File.WriteAllText(path + ".tmp", "stale");

        store.Update(c => c with { SetupCompleted = false });

        Assert.False(new ConfigStore(path).Load().SetupCompleted);
        Assert.False(File.Exists(path + ".tmp"));
    }

    /// <summary>
    /// Every field must survive a save and reload. The same config written twice must produce the
    /// same bytes, so a field that is dropped or reordered shows up as a difference.
    /// </summary>
    [Fact]
    public void FullyPopulatedConfigRoundTripsUnchanged()
    {
        var path = PathFor();
        var app = AppIdentity.ForExecutable(@"C:\Games\Halo.exe", "Halo Infinite");
        var config = new NetRouteConfig
        {
            SetupCompleted = true,
            RoleBindings =
            [
                new RoleBinding { Role = RoleId.Gaming, AdapterLuid = 0x0006008001000000, AdapterGuid = "{BCCF9527-22F7-49AA-8013-CEC28E232D11}", LastKnownName = "Ethernet" },
                new RoleBinding { Role = RoleId.Downloads, AdapterLuid = 0x0047008000000000, LastKnownName = "Wi-Fi" }
            ],
            AppRules = [AppRule.Create(app, RoleId.Gaming) with { Mode = RoutingMode.Preferred, Paused = true, EnforceIpv6 = false }],
            EnforcementPaused = true,
            PausedUntil = new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero),
            RouteSystemDownloads = false,
            PauseDownloadsWhileGaming = true,
            DownloadQuietHours = new QuietHours(22, 2),
            UpdateFeedUrl = null,
            AutomaticUpdates = false,
        };

        new ConfigStore(path).Save(config);
        var reloaded = new ConfigStore(path).Load();
        var firstBytes = File.ReadAllText(path);
        new ConfigStore(path).Save(reloaded);

        Assert.Equal(firstBytes, File.ReadAllText(path));
        Assert.Equal(config.PausedUntil, reloaded.PausedUntil);
        Assert.Equal(config.DownloadQuietHours, reloaded.DownloadQuietHours);
        Assert.Equal(RoutingMode.Preferred, reloaded.AppRules.Single().Mode);
        Assert.True(reloaded.AppRules.Single().Paused);
        Assert.False(reloaded.AppRules.Single().EnforceIpv6);
        Assert.Null(reloaded.UpdateFeedUrl);
    }

    /// <summary>Quiet hours that wrap past midnight, and the degenerate equal-hours case.</summary>
    [Theory]
    [InlineData(22, 2, 23, true)]
    [InlineData(22, 2, 1, true)]
    [InlineData(22, 2, 12, false)]
    [InlineData(9, 17, 12, true)]
    [InlineData(9, 17, 17, false)]
    [InlineData(9, 9, 9, false)]
    public void QuietHoursContainsHandlesWrapAndEqualHours(int from, int to, int hour, bool expected)
    {
        var now = new DateTime(2026, 11, 7, hour, 30, 0);

        Assert.Equal(expected, new QuietHours(from, to).Contains(now));
    }
}
