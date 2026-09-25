using NetRoute.Windows.Apps;

namespace NetRoute.Tests.Windows;

public sealed class SteamDownloadsTests : IDisposable
{
    private readonly string _library = Path.Combine(Path.GetTempPath(), $"NetRoute.Steamapps.{Guid.NewGuid():N}");

    public SteamDownloadsTests() => Directory.CreateDirectory(_library);

    [Fact]
    public void ReadsWhatSteamIsStillFetchingAndIgnoresFinishedGames()
    {
        // The shape Steam writes, including a finished game with no byte counters at all,
        // which is what an installed game looks like on this machine.
        Write("appmanifest_1.acf", """
            "AppState"
            {
                "name"		"Halo Infinite"
                "StateFlags"		"1026"
                "BytesDownloaded"		"1000000000"
                "BytesToDownload"		"13400000000"
            }
            """);
        Write("appmanifest_2.acf", """
            "AppState"
            {
                "name"		"Steamworks Common Redistributables"
                "StateFlags"		"4"
            }
            """);
        Write("appmanifest_3.acf", """
            "AppState"
            {
                "name"		"DayZ"
                "StateFlags"		"4"
                "BytesDownloaded"		"52000000"
                "BytesToDownload"		"52000000"
            }
            """);

        var downloading = SteamDownloads.InProgress([_library]);

        var halo = Assert.Single(downloading);
        Assert.Equal("Halo Infinite", halo.Game);
        Assert.Equal(12_400_000_000, halo.BytesRemaining);
    }

    [Fact]
    public void NoLibrariesMeansNothingToSay() => Assert.Empty(SteamDownloads.InProgress([]));

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_library, name), content);

    public void Dispose()
    {
        if (Directory.Exists(_library)) Directory.Delete(_library, true);
    }
}
