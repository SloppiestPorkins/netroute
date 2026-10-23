using NetRoute.Core.Policy;
using NetRoute.Ipc;

namespace NetRoute.Tests.Ipc;

public sealed class AppQueryTests
{
    private static AppIdentity Exe(string name, string path, string? install = null)
        => AppIdentity.ForExecutable(path, name) with { InstallLocation = install };

    private static AppIdentity Pkg(string name, string family) => AppIdentity.ForPackage(family, name);

    private static AppQueryResult<AppIdentity> Resolve(string query, params AppIdentity[] apps)
        => AppQuery.Resolve(apps, a => a, query);

    [Fact]
    public void A_path_query_that_matches_no_name_finds_the_rule()
    {
        // The shipped 1.1.3 bug: "remove <path>" said no app matched.
        var halloween = Exe("Halloween Game", @"G:\SteamLibrary\steamapps\common\Halloween\PrereqsBundle.exe");
        var other = Exe("Other", @"C:\Other\other.exe");

        var byFolder = Resolve(@"G:\SteamLibrary\steamapps\common\Halloween", halloween, other);
        var byFile = Resolve("PrereqsBundle.exe", halloween, other);

        Assert.Equal(AppQueryStage.Path, byFolder.Stage);
        Assert.Same(halloween, byFolder.Single);
        Assert.Same(halloween, byFile.Single);
    }

    [Fact]
    public void An_exact_name_beats_a_longer_name_that_contains_it()
    {
        var minecraft = Exe("Minecraft", @"C:\mc\mc.exe");
        var launcher = Exe("Minecraft Launcher", @"C:\mc\launcher.exe");

        var result = Resolve("minecraft", launcher, minecraft);

        Assert.Equal(AppQueryStage.ExactName, result.Stage);
        Assert.Same(minecraft, result.Single);
    }

    [Fact]
    public void A_name_fragment_finds_a_single_app()
    {
        var result = Resolve("steam", Exe("Steam Client", @"C:\s\steam.exe"), Exe("Chrome", @"C:\c\chrome.exe"));

        Assert.Equal(AppQueryStage.Name, result.Stage);
        Assert.NotNull(result.Single);
    }

    [Fact]
    public void Several_name_matches_are_reported_not_picked()
    {
        var result = Resolve("xbox", Exe("Xbox App", @"C:\x\XboxPcApp.exe"), Exe("Xbox Game Bar", @"C:\x\bar.exe"));

        Assert.Equal(AppQueryStage.Name, result.Stage);
        Assert.Equal(2, result.Matches.Count);
        Assert.Null(result.Single);
    }

    [Fact]
    public void Several_path_matches_are_reported_not_picked()
    {
        var result = Resolve(@"C:\Games", Exe("One", @"C:\Games\a\one.exe"), Exe("Two", @"C:\Games\b\two.exe"));

        Assert.Equal(AppQueryStage.Path, result.Stage);
        Assert.Equal(2, result.Matches.Count);
        Assert.Null(result.Single);
    }

    [Fact]
    public void A_path_tells_apart_rules_that_share_a_name()
    {
        var app = Exe("Xbox App", @"C:\x\XboxPcApp.exe");
        var ft = Exe("Xbox App", @"C:\x\XboxPcAppFT.exe");

        Assert.Equal(AppQueryStage.ExactName, Resolve("Xbox App", app, ft).Stage);
        Assert.Same(ft, Resolve("XboxPcAppFT", app, ft).Single);
    }

    [Fact]
    public void Nothing_matching_returns_no_matches()
    {
        var result = Resolve("zzz", Exe("Steam", @"C:\s\steam.exe"));

        Assert.Equal(AppQueryStage.None, result.Stage);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void A_blank_query_matches_nothing()
    {
        Assert.Empty(Resolve("  ", Exe("Steam", @"C:\s\steam.exe")).Matches);
    }

    [Fact]
    public void Matching_ignores_case_and_directory_separator_style()
    {
        var app = Exe("Halloween Game", @"G:\SteamLibrary\Halloween\game.exe");

        Assert.Same(app, Resolve("HALLOWEEN GAME", app).Single);
        Assert.Same(app, Resolve("g:/steamlibrary/halloween/", app).Single);
        Assert.Same(app, Resolve(@"g:\steamlibrary\halloween\", app).Single);
    }

    [Fact]
    public void A_folder_rule_is_found_by_its_install_location()
    {
        var folder = AppIdentity.ForFolder(@"G:\SteamLibrary\steamapps\common", "Steam games");

        Assert.Same(folder, Resolve(@"steamapps/common", folder).Single);
    }

    [Fact]
    public void The_package_family_name_is_the_last_resort()
    {
        var halo = Pkg("Halo Infinite", "Microsoft.HaloInfinite_8wekyb3d8bbwe");

        var result = Resolve("8wekyb3d8bbwe", halo, Exe("Steam", @"C:\s\steam.exe"));

        Assert.Equal(AppQueryStage.Package, result.Stage);
        Assert.Same(halo, result.Single);
    }

    [Fact]
    public void A_name_match_is_not_overridden_by_a_later_stage()
    {
        // "steam" is in one name and in another app's path; the name stage decides.
        var named = Exe("Steam", @"C:\x\a.exe");
        var pathOnly = Exe("Other", @"C:\steam\b.exe");

        Assert.Same(named, Resolve("steam", named, pathOnly).Single);
    }
}
