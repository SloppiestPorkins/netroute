using NetRoute.Core.Policy;
using NetRoute.Windows.Apps;

namespace NetRoute.Poc;

/// <summary>
/// Runs application discovery against the real machine and prints what it found.
///
/// <para>Discovery is heuristic by nature — package metadata is inconsistent, uninstall
/// entries are unreliable, and launchers hide games behind themselves. The only way to
/// know whether the heuristics are any good is to look at the output on a real system.</para>
/// </summary>
internal static class AppDiscoveryProof
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== PACKAGED APPS (Store / Game Pass) ===");
        Console.WriteLine();

        var packaged = await new PackagedAppDiscovery().DiscoverAsync();

        var gamePass = packaged.Where(a => a.IsGamePass).ToList();
        Console.WriteLine($"Game Pass / Xbox titles: {gamePass.Count}");
        foreach (var app in gamePass)
        {
            Console.WriteLine($"  {app.DisplayName}");
            Console.WriteLine($"      package  {app.Identity.PackageFamilyName}");
            Console.WriteLine($"      aumid    {app.Identity.Aumid ?? "(none)"}");
        }

        Console.WriteLine();
        Console.WriteLine($"Other packaged apps: {packaged.Count - gamePass.Count} (showing first 15)");
        foreach (var app in packaged.Where(a => !a.IsGamePass).Take(15))
        {
            Console.WriteLine($"  {Truncate(app.DisplayName, 40),-40} {app.Identity.PackageFamilyName}");
        }

        Console.WriteLine();
        Console.WriteLine("=== DESKTOP APPS ===");
        Console.WriteLine();

        var win32 = new Win32AppDiscovery().Discover();

        foreach (var category in new[]
                 {
                     AppCategory.Launcher, AppCategory.Game,
                     AppCategory.Browser, AppCategory.Communication
                 })
        {
            var matches = win32.Where(a => a.Category == category).ToList();
            Console.WriteLine($"{category} ({matches.Count}):");

            foreach (var app in matches.Take(12))
            {
                var running = app.IsRunning ? "[running] " : "          ";
                Console.WriteLine($"  {running}{Truncate(app.DisplayName, 34),-34} {Truncate(app.Identity.ExecutablePath!, 60)}");
            }
            Console.WriteLine();
        }

        var other = win32.Count(a => a.Category == AppCategory.Other);
        Console.WriteLine($"Uncategorised desktop apps: {other}");
        Console.WriteLine($"Running right now: {win32.Count(a => a.IsRunning)}");

        Console.WriteLine();
        Console.WriteLine("Sanity checks:");
        Report("found at least one packaged app", packaged.Count > 0);
        Report("every packaged app has a package family name",
            packaged.All(a => !string.IsNullOrWhiteSpace(a.Identity.PackageFamilyName)));
        Report("every desktop app has an existing executable",
            win32.All(a => File.Exists(a.Identity.ExecutablePath!)));
        Report("identities are unique",
            win32.Concat(packaged).Select(a => a.Identity.StableKey).Distinct().Count()
            == win32.Count + packaged.Count);

        return 0;
    }

    private static void Report(string check, bool passed)
        => Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}  {check}");

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : "..." + value[^(max - 3)..];
}
