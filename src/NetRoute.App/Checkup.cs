using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetRoute.Core.Policy;
using NetRoute.Ipc;
using NetRoute.Windows.Apps;

namespace NetRoute.App;

public enum HealthLevel
{
    Problem,
    Tip,
    Fine
}

/// <summary>One line of the Check-up, with the one-click fix when there is one.</summary>
public sealed record HealthItem(HealthLevel Level, string Title, string? Detail, string? FixText, Func<Task>? Fix)
{
    public static HealthItem Problem(string title, string? detail, string? fixText = null, Func<Task>? fix = null) => new(HealthLevel.Problem, title, detail, fix is null ? null : fixText, fix);
    public static HealthItem Tip(string title, string? detail, string? fixText = null, Func<Task>? fix = null) => new(HealthLevel.Tip, title, detail, fix is null ? null : fixText, fix);
    public static HealthItem Fine(string title, string? detail, string? fixText = null, Func<Task>? fix = null) => new(HealthLevel.Fine, title, detail, fix is null ? null : fixText, fix);

    public string Glyph => Level switch { HealthLevel.Problem => "", HealthLevel.Tip => "", _ => "" };
    public Brush Brush => Ui.Res(Level switch { HealthLevel.Problem => "WarnBrush", HealthLevel.Tip => "DownloadsBrush", _ => "GoodBrush" });
}

/// <summary>
/// Check-up: everything that decides where traffic actually goes, in one place, each problem
/// with its fix. It exists because the things that went wrong in practice (protection left
/// paused, two default connections, Steam never added) were each visible somewhere, but
/// nowhere put them together.
/// </summary>
public partial class HealthViewModel(MainViewModel main) : ObservableObject
{
    /// <summary>What counts as "busy" on the Gaming connection: 1 MB/s either way.</summary>
    private const double BusyBytesPerSecond = 1024 * 1024;

    public ObservableCollection<HealthItem> Items { get; } = [];

    [ObservableProperty] private string _summary = "Checking…";

    public async Task LoadAsync()
    {
        Summary = "Checking…";
        var status = main.Status;
        if (status is null)
        {
            Items.Clear();
            Summary = "The NetRoute service isn't answering, so nothing can be checked.";
            return;
        }

        var client = main.Client;
        var items = new List<HealthItem>();

        if (status.EnforcementPaused)
        {
            items.Add(HealthItem.Problem("Protection is paused",
                (status.PausedUntil is { } until ? $"It turns back on by itself at {until.ToLocalTime():HH:mm}. " : "It stays off until you turn it back on. ") +
                "Until then every app uses normal Windows routing, so downloads can land on your Gaming connection.",
                "Resume", () => main.Run(() => client.SetEnforcementPausedAsync(false), "Protection is back on.")));
        }

        if (status.RouteTie is { } tie)
        {
            items.Add(HealthItem.Problem("Windows has two default connections", tie.Message, "Fix it", () => main.FixRouteTieCommand.ExecuteAsync(null)));
        }

        if (!status.RedirectionAvailable)
        {
            items.Add(HealthItem.Problem("NetRoute can't move apps onto another connection",
                "The split-tunnel driver isn't running, so NetRoute can keep apps off the wrong network but can't move them. " +
                "Run RUN-NETROUTE-SETUP.cmd as administrator to fix this."));
        }

        foreach (var role in status.Roles.Where(r => r.Health != RoleHealth.Connected))
        {
            var name = role.Adapter?.Name ?? role.LastKnownName ?? "no network";
            var what = role.Health switch
            {
                RoleHealth.Unassigned => "hasn't been chosen",
                RoleHealth.Missing => $"is {name}, which isn't there right now",
                RoleHealth.NoInternet => $"is {name}, which has no internet",
                _ => $"is {name}, which is offline"
            };
            items.Add(HealthItem.Problem($"Your {role.Role.DisplayName()} network {what}",
                role.AssignedApps > 0 ? $"{role.AssignedApps} app(s) set to {role.Role.DisplayName()} are blocked until it's back, so they can't fall onto the other connection." : null,
                "Choose a network", () =>
                {
                    main.Overlay = new AdapterPickerViewModel(main, role.Role);
                    return Task.CompletedTask;
                }));
        }

        // Download apps: installed but not routed, or routed to the wrong place.
        IReadOnlyList<InstalledApp> installed = [];
        try
        {
            installed = await Task.Run(() => new Win32AppDiscovery().Discover());
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        foreach (var launcher in DownloadApps.Installed(installed))
        {
            var rule = status.Apps.FirstOrDefault(a => DownloadApps.SameProgram(a.Rule.App, launcher.Identity));
            if (rule is null)
            {
                items.Add(HealthItem.Problem($"{launcher.DisplayName} isn't in your apps",
                    "Its downloads follow Windows' default connection, and over IPv6 they can still reach your Gaming connection.",
                    "Put on Downloads", () => main.Run(
                        () => client.AddRuleAsync(launcher.Identity with { InstallLocation = launcher.InstallLocation }, RoleId.Downloads),
                        $"{launcher.DisplayName} now uses Downloads.")));
            }
            else if (rule.Rule.Role != RoleId.Downloads)
            {
                items.Add(HealthItem.Problem($"{launcher.DisplayName} is on {rule.Rule.Role.DisplayName()}",
                    "Its game downloads and updates use that connection.", "Move to Downloads", () => MoveToDownloads(rule)));
            }
        }

        foreach (var browser in status.Apps.Where(a => a.Rule.Role == RoleId.Gaming && a.Rule.App.ExecutablePath is { } p
                                                      && Win32AppDiscovery.CategoryOf(p) == AppCategory.Browser))
        {
            items.Add(HealthItem.Tip($"{browser.Rule.App.DisplayName} is on Gaming",
                "Anything you download in it uses your Gaming connection. Move it if you'd rather keep browsing off that line.",
                "Move to Downloads", () => MoveToDownloads(browser)));
        }

        foreach (var app in status.Apps.Where(a => a.Rule.Role != RoleId.Default && LocalHostingApps.Includes(a.Rule.App)))
        {
            items.Add(HealthItem.Tip($"{app.Rule.App.DisplayName} isn't moved", LocalHostingApps.Explain(app.Rule.App),
                "Set to Windows routing", () => main.Run(
                    () => client.UpdateRuleAsync(app.Rule with { Role = RoleId.Default, Mode = RoutingMode.Default }),
                    $"{app.Rule.App.DisplayName} now uses Windows routing.")));
        }

        // The same app added twice: once by name and once by the new-game prompt, say.
        foreach (var same in status.Apps.GroupBy(a => DuplicateKey(a.Rule.App)).Where(g => g.Count() > 1))
        {
            var extra = same.Skip(1).First();
            items.Add(HealthItem.Problem($"{same.First().Rule.App.DisplayName} is in your list twice",
                $"\"{same.First().Rule.App.DisplayName}\" and \"{extra.Rule.App.DisplayName}\" are the same program, and two rules for one app can disagree.",
                "Remove the duplicate", () => main.Run(() => client.RemoveRuleAsync(extra.Rule.Id), $"Removed the duplicate {extra.Rule.App.DisplayName}.")));
        }

        if (status.SystemDownloads is { } system)
        {
            if (!system.Enabled)
            {
                items.Add(HealthItem.Tip("Windows and Xbox downloads can use either connection", system.Summary,
                    "Keep them on Downloads", () => main.Run(() => client.SetSystemDownloadsAsync(true), "Windows Update, Store and Xbox downloads now use Downloads.")));
            }
            else if (system.Active)
            {
                items.Add(HealthItem.Fine("Windows and Xbox downloads use Downloads", system.Summary,
                    "Turn off", () => main.Run(() => client.SetSystemDownloadsAsync(false), "Windows and Xbox downloads now follow Windows' default connection.")));
            }
            else if (!status.EnforcementPaused)
            {
                items.Add(HealthItem.Problem("Windows and Xbox downloads aren't being kept on Downloads", system.Summary));
            }
        }

        // Said once, plainly, because it is the one gap a user can't see and every tool of this
        // kind has it: ForceBindIP and Mullvad's driver both hit exactly the same thing.
        if (status.Roles.Any(r => r.Adapter is { DnsServers.Count: > 0 }))
        {
            var servers = status.Roles.Where(r => r.Adapter is { DnsServers.Count: > 0 })
                .Select(r => $"{r.Role.DisplayName()} asks {string.Join(", ", r.Adapter!.DnsServers.Take(2))}");
            items.Add(HealthItem.Tip("Name lookups don't follow your app rules",
                "Windows looks up names in its own service, not inside the app, so a lookup can leave by the other " +
                "connection even when the app's traffic can't. It affects which names were asked for, never where the " +
                "traffic itself goes. " + string.Join(". ", servers) + "."));
        }

        if (status.DownloadsPause is { } pause)
        {
            var oneConnection = status.Roles.Where(r => r.Adapter is not null).Select(r => r.Adapter!.Luid).Distinct().Count() == 1;
            if (!pause.Enabled)
            {
                items.Add(HealthItem.Tip("Downloads keep running while you play",
                    oneConnection
                        ? "Both roles are the same connection, so traffic can't be separated. Pausing downloads while a game runs is the one thing that will protect it."
                        : "If a download ever ends up on your gaming line, NetRoute can block the download apps while a game is running.",
                    "Pause them while I play",
                    () => main.Run(() => client.SetPauseDownloadsAsync(true), "Downloads will pause while you play.")));
            }
            else
            {
                items.Add(HealthItem.Fine(
                    pause.PausedFor is { } reason ? $"Downloads are paused: {reason}" : "Downloads pause while you play",
                    "They start again by themselves, and anything half-downloaded carries on.", "Turn off",
                    () => main.Run(() => client.SetPauseDownloadsAsync(false), "Downloads keep running while you play.")));
            }

            if (pause.QuietHours is { } window)
            {
                items.Add(HealthItem.Fine($"Downloads are held back between {window}", "Your scheduled quiet hours.", "Stop that",
                    () => main.Run(() => client.SetQuietHoursAsync(null, null), "Quiet hours are off.")));
            }
            else
            {
                items.Add(HealthItem.Tip("Downloads can be held back at set times",
                    "If you usually play in the evening, NetRoute can block the download apps then, whatever else is happening. " +
                    "Other hours: netroute quiet-hours 20 23.",
                    "Hold them 6pm to 11pm",
                    () => main.Run(() => client.SetQuietHoursAsync(18, 23), "Downloads are held back between 18:00 and 23:00.")));
            }
        }

        await AddUpdateChecks(items);
        await AddGamingLineChecks(status, items);

        var ordered = items.OrderBy(i => i.Level).ToList();
        if (ordered.All(i => i.Level != HealthLevel.Problem))
        {
            ordered.Insert(0, HealthItem.Fine("Everything that affects your routing looks right", null));
        }

        Items.Clear();
        foreach (var item in ordered)
        {
            Items.Add(item);
        }
        var problems = ordered.Count(i => i.Level == HealthLevel.Problem);
        Summary = problems switch
        {
            0 => "No problems found. Tips below are optional.",
            1 => "1 thing needs fixing. Each fix is one click.",
            _ => $"{problems} things need fixing. Each fix is one click."
        };
    }

    /// <summary>
    /// Anything moving a lot of data on the Gaming connection that isn't a Gaming app. This is
    /// what actually hurts a game (a big download on its line), and it's measured, not guessed.
    /// </summary>
    /// <summary>
    /// Updates. Nothing is configured out of the box, so the first thing this can say is that
    /// NetRoute has nowhere to look — which is a choice the user should get to make, not a
    /// silence they never notice.
    /// </summary>
    private async Task AddUpdateChecks(List<HealthItem> items)
    {
        UpdateSettingsDto settings;
        try
        {
            settings = await main.Client.GetUpdateSettingsAsync();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return;
        }

        Task Open() => main.ShowUpdatesCommand.ExecuteAsync(null);

        if (settings.Available is { } update)
        {
            items.Add(HealthItem.Tip($"NetRoute {update.Version} is available", Upgrade.Describe(update), Upgrade.ButtonText(update), Open));
        }
        else if (settings.FeedUrl is null)
        {
            items.Add(HealthItem.Tip("NetRoute never checks for updates",
                $"You are on {settings.CurrentVersion}, and NetRoute has nowhere to look, so it doesn't call anywhere. " +
                "Give it an address and it will check daily, fetch what it finds and prove the download is genuine before you install it.",
                "Set that up", Open));
        }
        else if (!settings.Automatic)
        {
            items.Add(HealthItem.Tip("Update checks are switched off",
                $"You are on {settings.CurrentVersion}. NetRoute only looks when you ask it to.", "Updates", Open));
        }
        else
        {
            items.Add(HealthItem.Fine($"You are on the newest NetRoute ({settings.CurrentVersion})",
                settings.CheckedAt is { } at ? "Last looked " + Format.Ago(at) + "." : "It checks once a day.", "Check now", Open));
        }
    }

    private async Task AddGamingLineChecks(ServiceStatusDto status, List<HealthItem> items)
    {
        var gaming = status.Roles.FirstOrDefault(r => r.Role == RoleId.Gaming)?.Adapter?.Name;
        if (gaming is null)
        {
            return;
        }

        AppRatesDto? rates;
        try
        {
            // The first reading after the service starts has nothing to compare against yet.
            rates = await main.Client.GetAppRatesAsync();
            if (rates.Available && rates.Rates.Count == 0)
            {
                await Task.Delay(1200);
                rates = await main.Client.GetAppRatesAsync();
            }
        }
        catch (Exception ex) when (ex is NetRouteServiceException or ServiceUnavailableException)
        {
            return;
        }

        if (!rates.Available)
        {
            items.Add(HealthItem.Tip("Per-app speeds aren't available", rates.Problem ?? "NetRoute couldn't start measuring traffic per app."));
            return;
        }

        var playing = status.Apps.FirstOrDefault(a => a.Rule.Role == RoleId.Gaming && a.ActiveConnections > 0)?.Rule.App.DisplayName;
        var busy = rates.Rates
            .Where(r => string.Equals(r.InterfaceName, gaming, StringComparison.OrdinalIgnoreCase))
            .Where(r => !status.Apps.Any(a => a.Rule.Role == RoleId.Gaming && AppMatch.Covers(a.Rule.App, r.ExecutablePath, r.PackageFamilyName)))
            .GroupBy(r => r.ExecutablePath ?? r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Rate: g.First(), Speed: g.Sum(r => r.DownBytesPerSecond + r.UpBytesPerSecond)))
            .Where(x => x.Speed >= BusyBytesPerSecond)
            .OrderByDescending(x => x.Speed)
            .Take(3);

        foreach (var (rate, speed) in busy)
        {
            var detail = $"It's using {Format.Rate(speed)} of your Gaming connection" + (playing is null ? "." : $" while {playing} is running.");
            if (rate.ExecutablePath is null || IsWindowsComponent(rate.ExecutablePath))
            {
                var systemOn = status.SystemDownloads?.Enabled == true;
                items.Add(HealthItem.Problem($"Windows ({rate.ProcessName}) is busy on your Gaming connection",
                    detail + (systemOn ? "" : " Keeping Windows downloads on Downloads would move most of this."),
                    "Keep Windows downloads on Downloads",
                    systemOn ? null : () => main.Run(() => main.Client.SetSystemDownloadsAsync(true), "Windows Update, Store and Xbox downloads now use Downloads.")));
                continue;
            }

            var existing = status.Apps.FirstOrDefault(a => AppMatch.Covers(a.Rule.App, rate.ExecutablePath, rate.PackageFamilyName));
            var name = existing?.Rule.App.DisplayName ?? rate.ProcessName;
            items.Add(HealthItem.Problem($"{name} is busy on your Gaming connection", detail, "Move to Downloads",
                existing is not null
                    ? () => MoveToDownloads(existing)
                    : () => main.Run(() => main.Client.AddRuleAsync(IdentityOf(rate, name), RoleId.Downloads), $"{name} now uses Downloads.")));
        }
    }

    [RelayCommand]
    private async Task Fix(HealthItem item)
    {
        if (item.Fix is null)
        {
            return;
        }
        await item.Fix();
        if (main.Overlay == this)
        {
            await LoadAsync();
        }
    }

    [RelayCommand]
    private Task Recheck() => LoadAsync();

    private Task MoveToDownloads(AppStatusDto app) => main.Run(
        () => main.Client.UpdateRuleAsync(app.Rule with { Role = RoleId.Downloads, Mode = RoutingMode.Strict }),
        $"{app.Rule.App.DisplayName} now uses Downloads.");

    private static AppIdentity IdentityOf(AppRateDto rate, string name)
        => rate.PackageFamilyName is { } package ? AppIdentity.ForPackage(package, name) : AppIdentity.ForExecutable(rate.ExecutablePath!, name);

    /// <summary>What two rules must share to be the same app: a package, or the same program.</summary>
    private static string DuplicateKey(AppIdentity app)
    {
        if (app.PackageFamilyName is { } family)
        {
            return "pkg:" + family.Split('_')[0].ToLowerInvariant();
        }
        var path = app.ExecutablePath ?? app.DisplayName;
        const string store = @"\WindowsApps\";
        var at = path.IndexOf(store, StringComparison.OrdinalIgnoreCase);
        return at < 0
            ? "exe:" + path.ToLowerInvariant()
            : "pkg:" + path[(at + store.Length)..].Split('\\')[0].Split('_')[0].ToLowerInvariant();
    }

    private static bool IsWindowsComponent(string path)
        => path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
}

/// <summary>The shared matcher, so the GUI agrees with the service about what an app is.</summary>
public static class AppMatch
{
    public static bool Covers(AppIdentity rule, string? path, string? package) => AppMatching.Covers(rule, path, package);
}

/// <summary>Game launchers and stores: the apps whose whole job is downloading.</summary>
public static class DownloadApps
{
    public static IReadOnlyList<InstalledApp> Installed(IEnumerable<InstalledApp> apps) => apps
        .Where(a => a.Category == AppCategory.Launcher && a.Identity.ExecutablePath is not null)
        .GroupBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .Select(g => g.First())
        .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>Launchers are matched by file name, so a rule still counts after Steam moves drives.</summary>
    public static bool SameProgram(AppIdentity rule, AppIdentity app)
        => rule.ExecutablePath is { } a && app.ExecutablePath is { } b
           && string.Equals(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
}

/// <summary>"Pause protection" asks how long, so it can't be left off by accident.</summary>
public partial class PauseViewModel(MainViewModel main) : ObservableObject
{
    public IReadOnlyList<PauseChoice> Choices { get; } =
    [
        new("15 minutes", 15), new("1 hour", 60), new("3 hours", 180), new("Until I turn it back on", null)
    ];

    [RelayCommand]
    private async Task Choose(PauseChoice choice)
    {
        main.Overlay = null;
        await main.PauseFor(choice.Minutes);
    }
}

public sealed record PauseChoice(string Title, int? Minutes)
{
    public string Subtitle => Minutes is { } m ? $"Back on by itself at {DateTime.Now.AddMinutes(m):HH:mm}" : "Stays off until you press Resume";
}

/// <summary>Straight after setup: the download apps on this PC, ready to put on Downloads in one click.</summary>
public partial class SuggestDownloadsViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<SuggestItem> Items { get; } = [];

    [ObservableProperty] private bool _loading = true;

    public async Task<int> LoadAsync()
    {
        IReadOnlyList<InstalledApp> installed = [];
        try
        {
            installed = await Task.Run(() => new Win32AppDiscovery().Discover());
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        var apps = main.Status?.Apps ?? [];
        foreach (var app in DownloadApps.Installed(installed).Where(l => !apps.Any(r => DownloadApps.SameProgram(r.Rule.App, l.Identity))))
        {
            Items.Add(new SuggestItem(app));
        }
        Loading = false;
        return Items.Count;
    }

    [RelayCommand]
    private async Task AddSelected()
    {
        main.Overlay = null;
        var chosen = Items.Where(i => i.IsSelected).ToList();
        if (chosen.Count == 0)
        {
            return;
        }
        await main.Run(async () =>
        {
            foreach (var item in chosen)
            {
                await main.Client.AddRuleAsync(item.App.Identity with { InstallLocation = item.App.InstallLocation }, RoleId.Downloads);
            }
        }, $"{string.Join(", ", chosen.Select(c => c.Name))} now use{(chosen.Count == 1 ? "s" : "")} Downloads.");
    }
}

public partial class SuggestItem(InstalledApp app) : ObservableObject
{
    public InstalledApp App { get; } = app;
    public string Name => App.DisplayName;
    public string Subtitle => App.IsRunning ? "Running now" : "Installed";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckGlyph))]
    private bool _isSelected = true;

    public string CheckGlyph => IsSelected ? "" : "";

    [RelayCommand]
    private void Toggle() => IsSelected = !IsSelected;
}

/// <summary>A game that is online right now and isn't in the user's list.</summary>
public sealed record NewGamePrompt(string Name, string Root, AppIdentity Identity)
{
    public string Message => $"{Name} is running and isn't in your apps. Put it on Gaming?";
}

public static class NewGameDetector
{
    public static NewGamePrompt? Find(IEnumerable<ConnectionDto> connections, IReadOnlyList<AppStatusDto> apps, IReadOnlySet<string> ignored)
    {
        foreach (var connection in connections.Where(c => c.ExecutablePath is not null).DistinctBy(c => c.ExecutablePath!, StringComparer.OrdinalIgnoreCase))
        {
            var path = connection.ExecutablePath!;
            if (!GameFolders.TryGetGameRoot(path, out var root, out var name) || ignored.Contains(root))
            {
                continue;
            }
            var covered = apps.Any(a => AppMatch.Covers(a.Rule.App, path, connection.PackageFamilyName)
                                        || (a.Rule.App.ExecutablePath is { } rulePath
                                            && GameFolders.TryGetGameRoot(rulePath, out var ruleRoot, out _)
                                            && string.Equals(ruleRoot, root, StringComparison.OrdinalIgnoreCase)));
            if (covered)
            {
                continue;
            }
            var identity = connection.PackageFamilyName is { } package ? AppIdentity.ForPackage(package, name) : AppIdentity.ForExecutable(path, name);
            identity = identity with { InstallLocation = root };
            if (LocalHostingApps.Includes(identity))
            {
                continue;   // NetRoute wouldn't move it anyway, so don't offer to.
            }
            return new NewGamePrompt(name, root, identity);
        }
        return null;
    }
}

/// <summary>Small per-user app state: games the user said never to ask about.</summary>
public sealed class GuiState
{
    public List<string> IgnoredGames { get; set; } = [];

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetRoute", "gui-state.json");

    public static GuiState Load()
    {
        try
        {
            return System.IO.File.Exists(FilePath)
                ? JsonSerializer.Deserialize<GuiState>(System.IO.File.ReadAllText(FilePath)) ?? new GuiState()
                : new GuiState();
        }
        catch (Exception)
        {
            return new GuiState();
        }
    }

    public static void Ignore(string gameRoot)
    {
        var state = Load();
        if (state.IgnoredGames.Contains(gameRoot, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
        state.IgnoredGames.Add(gameRoot);
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}

/// <summary>"Prove it": the self-test, run from the app and shown step by step.</summary>
public partial class SelfTestViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<SelfTestRow> Steps { get; } = [];

    [ObservableProperty] private string _summary = "This checks your connections for real: it asks what the internet sees for each one, and measures your gaming line while the other one downloads.";
    [ObservableProperty] private bool _running;

    [RelayCommand]
    private Task Run() => RunAsync();

    public async Task RunAsync()
    {
        if (Running)
        {
            return;
        }
        Running = true;
        try
        {
            var state = await main.Client.StartSelfTestAsync();
            Show(state);
            while (state.Running)
            {
                await Task.Delay(1000);
                state = await main.Client.GetSelfTestAsync();
                Show(state);
            }
        }
        catch (ServiceUnavailableException)
        {
            Summary = "The NetRoute service isn't answering, so the test can't run.";
        }
        catch (NetRouteServiceException ex)
        {
            Summary = ex.Error.FriendlyMessage;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Summary = @"The test hit a problem. Details are in %LocalAppData%\NetRoute\gui.log.";
        }
        finally
        {
            Running = false;
        }
    }

    private void Show(SelfTestDto state)
    {
        Steps.Clear();
        foreach (var step in state.Steps)
        {
            Steps.Add(new SelfTestRow(step.Title, step.Detail, step.State));
        }
        Summary = state.Summary;
    }
}

public sealed record SelfTestRow(string Title, string? Detail, SelfTestState State)
{
    public string Glyph => State switch
    {
        SelfTestState.Pass => "\uE73E",
        SelfTestState.Warn => "\uE7BA",
        SelfTestState.Fail => "\uE711",
        SelfTestState.Running => "\uE895",
        _ => "\uEA3A"
    };

    public Brush Brush => Ui.Res(State switch
    {
        SelfTestState.Pass => "GoodBrush",
        SelfTestState.Warn => "WarnBrush",
        SelfTestState.Fail => "BadBrush",
        SelfTestState.Running => "DownloadsBrush",
        _ => "MutedBrush"
    });
}

/// <summary>What each app used, per connection, over the last week.</summary>
public partial class HistoryViewModel(MainViewModel main) : ObservableObject
{
    private const double BarWidth = 220;
    private string? _folder;

    public ObservableCollection<HistoryRow> Days { get; } = [];
    public ObservableCollection<HistoryRow> Apps { get; } = [];
    public ObservableCollection<ConnectionRow> Connections { get; } = [];

    [ObservableProperty] private string? _note = "Reading…";

    public async Task LoadAsync(int days = 7)
    {
        UsageHistoryDto history;
        try
        {
            history = await main.Client.GetUsageHistoryAsync(days);
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or NetRouteServiceException)
        {
            Note = "The NetRoute service isn't answering, so there's no history to show.";
            return;
        }

        _folder = history.Folder;
        Days.Clear();
        Apps.Clear();
        if (history.Problem is { } problem)
        {
            Note = problem;
            return;
        }

        var biggestDay = history.Days.Count == 0 ? 1 : history.Days.Max(d => d.DownBytes + d.UpBytes);
        foreach (var day in history.Days.OrderByDescending(d => d.Day).ThenByDescending(d => d.DownBytes))
        {
            Days.Add(Row($"{day.Day}  ·  {day.Adapter}", day.Adapter, day.DownBytes, day.UpBytes, biggestDay));
        }

        var biggestApp = history.TopApps.Count == 0 ? 1 : history.TopApps.Max(a => a.DownBytes + a.UpBytes);
        foreach (var app in history.TopApps)
        {
            Apps.Add(Row($"{app.App}  ·  {app.Adapter}", app.Adapter, app.DownBytes, app.UpBytes, biggestApp));
        }

        try
        {
            foreach (var seen in await main.Client.GetConnectionHistoryAsync(40))
            {
                Connections.Add(new ConnectionRow(seen.App, seen.Host, seen.Adapter ?? "not tied to one network",
                    $"{seen.Protocol.ToString().ToUpperInvariant()}  ·  {seen.Last:HH:mm}"));
            }
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or NetRouteServiceException)
        {
            // An older service without this command: the rest of the screen still works.
        }

        Note = history.Days.Count == 0
            ? "Nothing recorded yet. NetRoute writes usage while your apps are actually using the network."
            : null;
    }

    private HistoryRow Row(string title, string adapter, double down, double up, double biggest)
    {
        var card = main.Roles.FirstOrDefault(r => string.Equals(r.AdapterName, adapter, StringComparison.OrdinalIgnoreCase));
        return new HistoryRow(title, $"↓ {Format.Bytes(down)}   ↑ {Format.Bytes(up)}",
            Math.Max(2, (down + up) / Math.Max(1, biggest) * BarWidth),
            card?.RoleBrush ?? Ui.Res("DefaultBrush"));
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_folder is null || !System.IO.Directory.Exists(_folder))
        {
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}

public sealed record HistoryRow(string Title, string Detail, double BarWidth, Brush Accent);

public sealed record ConnectionRow(string App, string Host, string Adapter, string Detail);
