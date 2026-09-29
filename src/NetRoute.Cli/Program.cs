using System.ComponentModel;
using System.ServiceProcess;
using NetRoute.Core.Policy;
using NetRoute.Ipc;

const string Usage = """
        Usage: netroute <command>

          status                         networks, apps and whether enforcement is active
          adapters                       list network connections
          setup <gaming> <downloads>     choose your Gaming and Download connections, e.g. setup Ethernet Wi-Fi
          add <app or .exe> <network>    route an app: gaming | downloads | default
          move <app> <network>           change an app's network
          remove <app>                   stop routing an app
          apps | roles | why <app>       details
          pause [minutes] | resume       stop or restart enforcement, keeping your rules
          system-downloads on|off        keep Windows Update, Store and Xbox downloads on Downloads
          pause-downloads on|off         pause download apps while a game is running
          quiet-hours <from> <to>|off    hold downloads between these hours, e.g. quiet-hours 18 23
          selftest                       prove the separation works, end to end
          history [days]                 how much each app used, per connection
          update [--feed <url>|--off]    check for a newer NetRoute, fetch it, and install it
          emergency-disable              remove every NetRoute rule from Windows right now
          fix-routes                     give Windows one default connection when two are tied
        """;

// Role glyphs and arrows print as '??' under the console's default code page.
Console.OutputEncoding = System.Text.Encoding.UTF8;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
    using var client = new NamedPipeNetRouteClient();
    try
    {
        switch (command)
        {
            case "status": PrintStatus(await client.GetStatusAsync()); break;
            case "roles": PrintRoles((await client.GetStatusAsync()).Roles); break;
            case "apps": PrintApps((await client.GetStatusAsync()).Apps); break;
            case "why" when args.Length > 1: return PrintWhy(await client.GetStatusAsync(), string.Join(' ', args.Skip(1)));
            case "pause":
            {
                int? minutes = args.Length > 1 && int.TryParse(args[1], out var m) && m > 0 ? m : null;
                await client.SetEnforcementPausedAsync(true, minutes);
                Console.WriteLine(minutes is { } mins
                    ? $"NetRoute enforcement paused for {mins} minutes. It turns back on by itself."
                    : "NetRoute enforcement paused until you run 'netroute resume'.");
                break;
            }
            case "system-downloads" when args.Length > 1 && args[1] is "on" or "off":
            {
                var on = args[1] == "on";
                await client.SetSystemDownloadsAsync(on);
                Console.WriteLine(on
                    ? "Windows Update, Store and Xbox downloads now use Downloads."
                    : "Windows Update, Store and Xbox downloads now follow Windows' default connection.");
                break;
            }
            case "resume": await client.SetEnforcementPausedAsync(false); Console.WriteLine("NetRoute enforcement resumed."); break;
            case "emergency-disable": await client.EmergencyDisableAsync(); Console.WriteLine("All NetRoute filters removed and enforcement paused."); break;
            case "adapters": PrintAdapters(await client.GetAdaptersAsync()); break;
            case "setup" when args.Length >= 3: return await SetupAsync(client, args[1], args[2]);
            case "add" when args.Length >= 3: return await AddAsync(client, string.Join(' ', args[1..^1]), args[^1]);
            case "move" when args.Length >= 3: return await MoveAsync(client, string.Join(' ', args[1..^1]), args[^1]);
            case "remove" when args.Length >= 2: return await RemoveAsync(client, string.Join(' ', args[1..]));
            case "pause-downloads" when args.Length > 1 && args[1] is "on" or "off":
            {
                var on = args[1] == "on";
                await client.SetPauseDownloadsAsync(on);
                Console.WriteLine(on
                    ? "Download apps will be blocked while a game is running, and start again when you stop."
                    : "Download apps keep running while you play.");
                break;
            }
            case "quiet-hours" when args.Length > 1 && args[1] == "off":
                await client.SetQuietHoursAsync(null, null);
                Console.WriteLine("Quiet hours are off.");
                break;
            case "quiet-hours" when args.Length > 2 && int.TryParse(args[1], out var from) && int.TryParse(args[2], out var to):
                await client.SetQuietHoursAsync(from, to);
                Console.WriteLine($"Downloads are held back between {from:00}:00 and {to:00}:00.");
                break;
            case "selftest": return await SelfTestAsync(client);
            case "update": return await UpdateAsync(client, args);
            case "history": return await HistoryAsync(client, args.Length > 1 && int.TryParse(args[1], out var requested) ? requested : 7);
            case "cleanup-driver": return LocalCleanup(removeSublayers: args.Contains("--remove-sublayers"));
            case "fix-routes":
            {
                var result = await client.FixRouteTieAsync();
                Console.WriteLine(result.Message);
                return result.Fixed ? 0 : 1;
            }
            default: Console.Error.WriteLine(Usage); return 1;
        }
        return 0;
    }
    catch (ServiceUnavailableException) when (command == "emergency-disable") { return StopServiceFallback(); }
    catch (ServiceUnavailableException) when (command == "fix-routes") { return LocalFixRoutes(); }
    catch (ServiceUnavailableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    catch (NetRouteServiceException ex) { Console.Error.WriteLine(ex.Error.FriendlyMessage); return 1; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}

/// <summary>
/// Updates: where to look, what is there, and installing it.
///
/// <para>Installing runs setup, which asks for administrator itself. The service will not do it:
/// it runs as LocalSystem, and a service that can replace its own program on the strength of a
/// web address is a worse thing to have on a PC than a manual update.</para>
/// </summary>
static async Task<int> UpdateAsync(INetRouteClient client, string[] args)
{
    var feed = Array.FindIndex(args, a => a is "--feed" or "-f");
    if (feed >= 0 && feed + 1 < args.Length)
    {
        await client.SetUpdateSettingsAsync(args[feed + 1], automatic: !args.Contains("--manual"));
        Console.WriteLine($"NetRoute will look at {args[feed + 1]}"
                          + (args.Contains("--manual") ? " when you run 'netroute update'." : " once a day."));
    }
    else if (args.Contains("--off"))
    {
        var was = await client.GetUpdateSettingsAsync();
        await client.SetUpdateSettingsAsync(was.FeedUrl, automatic: false);
        Console.WriteLine("NetRoute won't look for updates by itself. 'netroute update' still checks.");
        return 0;
    }

    var settings = await client.GetUpdateSettingsAsync();
    Console.WriteLine($"You have NetRoute {settings.CurrentVersion}.");
    if (settings.FeedUrl is null)
    {
        Console.WriteLine("Nowhere to look, so NetRoute never calls anywhere.");
        Console.WriteLine("Put it back with: netroute update --feed " + NetRoute.Core.Config.Updates.Feed);
        return 0;
    }

    var found = await client.CheckForUpdateAsync();
    if (found is null)
    {
        Console.WriteLine("You are on the newest version.");
        return 0;
    }
    if (found.State == UpdateState.Failed)
    {
        Console.Error.WriteLine(found.Problem);
        Console.Error.WriteLine("Releases are at " + NetRoute.Core.Config.Updates.Releases);
        return 1;
    }
    Console.WriteLine($"NetRoute {found.Version} is available." + (found.Notes is { Length: > 0 } notes ? " " + notes : ""));

    if (found.State != UpdateState.Ready)
    {
        if (found.Sha256 is not { Length: 64 })
        {
            Console.WriteLine("It publishes no sha256, so NetRoute won't fetch it for you.");
            Console.WriteLine("Get it from " + NetRoute.Core.Config.Updates.ReleaseFor(found.Version));
            return 0;
        }
        found = await client.DownloadUpdateAsync();
        while (found is { State: UpdateState.Downloading })
        {
            Console.Write((char)13 + "Fetching... " + found.Fraction.ToString("P0").PadRight(8));
            await Task.Delay(500);
            found = (await client.GetUpdateSettingsAsync()).Available;
        }
        Console.WriteLine();
    }

    if (found is not { State: UpdateState.Ready, ReadyPath: { Length: > 0 } path })
    {
        Console.Error.WriteLine(found?.Problem ?? "The update didn't finish downloading.");
        return 1;
    }

    Console.WriteLine($"Ready: {path}");
    if (!args.Contains("--install"))
    {
        Console.WriteLine("Run 'netroute update --install' to install it. It takes under a minute and keeps your settings.");
        return 0;
    }
    try
    {
        using var setup = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(path, "/update") { UseShellExecute = true, Verb = "runas" });
        Console.WriteLine("Setup is running. NetRoute stops for a few seconds while the files are swapped.");
        return 0;
    }
    catch (Win32Exception)
    {
        Console.Error.WriteLine("Installing needs administrator permission, which wasn't given.");
        return 1;
    }
}

/// <summary>Runs the service's self-test, printing each step as it lands.</summary>
static async Task<int> SelfTestAsync(INetRouteClient client)
{
    var test = await client.StartSelfTestAsync();
    var printed = 0;
    while (true)
    {
        while (printed < test.Steps.Count && test.Steps[printed].State is not (SelfTestState.Pending or SelfTestState.Running))
        {
            var step = test.Steps[printed++];
            var mark = step.State switch
            {
                SelfTestState.Pass => "OK  ",
                SelfTestState.Warn => "!!  ",
                SelfTestState.Fail => "FAIL",
                _ => "    "
            };
            Console.WriteLine($"{mark} {step.Title}");
            Console.WriteLine($"     {step.Detail}");
        }
        if (!test.Running)
        {
            break;
        }
        await Task.Delay(1000);
        test = await client.GetSelfTestAsync();
    }
    Console.WriteLine();
    Console.WriteLine(test.Summary);
    return test.Steps.Any(s => s.State == SelfTestState.Fail) ? 1 : 0;
}

static async Task<int> HistoryAsync(INetRouteClient client, int days)
{
    var history = await client.GetUsageHistoryAsync(days);
    if (history.Problem is { } problem)
    {
        Console.Error.WriteLine(problem);
        return 1;
    }
    if (history.Days.Count == 0)
    {
        Console.WriteLine("Nothing recorded yet. NetRoute writes usage while apps are actually using the network.");
        return 0;
    }
    Console.WriteLine($"Last {days} day(s), by connection:");
    foreach (var day in history.Days.GroupBy(d => d.Day))
    {
        Console.WriteLine($"  {day.Key}   " + string.Join("   ", day.Select(d => $"{d.Adapter}: {Size(d.DownBytes)} down, {Size(d.UpBytes)} up")));
    }
    Console.WriteLine("\nTop apps:");
    foreach (var app in history.TopApps.Take(12))
    {
        Console.WriteLine($"  {app.App,-24} {app.Adapter,-16} {Size(app.DownBytes)} down, {Size(app.UpBytes)} up");
    }
    Console.WriteLine($"\nDaily CSVs: {history.Folder}");
    return 0;
}

static string Size(double bytes) => bytes switch
{
    < 1024 => $"{bytes:0} B",
    < 1024 * 1024 => $"{bytes / 1024:0.0} KB",
    < 1024d * 1024 * 1024 => $"{bytes / 1024 / 1024:0.0} MB",
    _ => $"{bytes / 1024 / 1024 / 1024:0.00} GB"
};

static void PrintAdapters(IEnumerable<AdapterDto> adapters)
{
    foreach (var a in adapters.OrderByDescending(a => a.Selectable).ThenBy(a => a.Name))
    {
        var marker = a.Selectable ? "  " : "- ";
        Console.WriteLine($"{marker}{a.Name,-30} {a.State,-12} {a.LinkSpeed,-10} {a.Ipv4Address ?? "-",-16} {(a.HasIpv6Route ? "" : "IPv4 only")}");
    }
    Console.WriteLine("\n(- = not usable as Gaming/Downloads: no internet route, virtual, or disconnected)");
}

static async Task<int> SetupAsync(INetRouteClient client, string gamingQuery, string downloadsQuery)
{
    var adapters = (await client.GetAdaptersAsync()).Where(a => a.Selectable).ToList();
    var gaming = FindAdapter(adapters, gamingQuery);
    var downloads = FindAdapter(adapters, downloadsQuery);
    if (gaming is null || downloads is null)
    {
        return 1;
    }
    if (gaming.Luid == downloads.Luid)
    {
        Console.WriteLine("Warning: Gaming and Downloads are the same connection, so NetRoute can't separate them.");
    }
    await client.CompleteSetupAsync(gaming.Luid, downloads.Luid);
    Console.WriteLine($"✓ You're ready.\n  Games     → {gaming.Name}\n  Downloads → {downloads.Name}");
    return 0;
}

static AdapterDto? FindAdapter(IReadOnlyList<AdapterDto> adapters, string query)
{
    var matches = adapters.Where(a => a.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 0)
    {
        matches = adapters.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                      || a.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    if (matches.Count == 1)
    {
        return matches[0];
    }
    Console.Error.WriteLine(matches.Count == 0
        ? $"No usable connection matches '{query}'. Run 'netroute adapters' to see them."
        : $"'{query}' matches several connections: {string.Join(", ", matches.Select(m => m.Name))}.");
    return null;
}

static async Task<int> AddAsync(INetRouteClient client, string target, string roleText)
{
    if (ParseRole(roleText) is not { } role)
    {
        return 1;
    }
    var app = await ResolveAppAsync(target);
    if (app is null)
    {
        return 1;
    }
    var added = await client.AddRuleAsync(app, role);
    Console.WriteLine($"{app.DisplayName} → {role.Glyph()} {role.DisplayName()}");
    Console.WriteLine($"  {added.VerificationSummary}");
    return 0;
}

static async Task<int> MoveAsync(INetRouteClient client, string query, string roleText)
{
    if (ParseRole(roleText) is not { } role || SingleApp(await client.GetStatusAsync(), query) is not { } app)
    {
        return 1;
    }
    await client.UpdateRuleAsync(app.Rule with { Role = role, Mode = role == RoleId.Default ? RoutingMode.Default : RoutingMode.Strict });
    Console.WriteLine($"{app.Rule.App.DisplayName} → {role.Glyph()} {role.DisplayName()}");
    return 0;
}

static async Task<int> RemoveAsync(INetRouteClient client, string query)
{
    if (SingleApp(await client.GetStatusAsync(), query) is not { } app)
    {
        return 1;
    }
    await client.RemoveRuleAsync(app.Rule.Id);
    Console.WriteLine($"Removed {app.Rule.App.DisplayName}.");
    return 0;
}

static AppStatusDto? SingleApp(ServiceStatusDto status, string query)
{
    // An exact name wins, so "Minecraft" still works when "Minecraft Launcher" is also in the list.
    var matches = status.Apps.Where(a => a.Rule.App.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 0)
    {
        matches = status.Apps.Where(a => a.Rule.App.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    if (matches.Count > 1)
    {
        // Two rules can share a display name and differ only by which program they point at:
        // the Xbox app registers XboxPcApp.exe and XboxPcAppFT.exe, both called "Xbox App".
        // Matching the path as well means there is always a way to name the one you mean.
        var byPath = status.Apps
            .Where(a => Identifier(a).Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byPath.Count == 1)
        {
            return byPath[0];
        }
    }
    if (matches.Count == 1)
    {
        return matches[0];
    }
    if (matches.Count == 0)
    {
        Console.Error.WriteLine($"No app in your list matches '{query}'.");
        return null;
    }

    // Say what would tell them apart, rather than printing the same name twice.
    Console.Error.WriteLine($"'{query}' matches several apps. Use enough of one of these to pick one:");
    foreach (var match in matches)
    {
        Console.Error.WriteLine($"  {match.Rule.App.DisplayName}  ({Identifier(match)})");
    }
    return null;
}

/// <summary>What distinguishes one rule from another with the same name.</summary>
static string Identifier(AppStatusDto app)
    => app.Rule.App.ExecutablePath ?? app.Rule.App.InstallLocation ?? app.Rule.App.PackageFamilyName ?? app.Rule.App.DisplayName;

static RoleId? ParseRole(string text)
{
    switch (text.ToLowerInvariant())
    {
        case "gaming" or "game" or "g": return RoleId.Gaming;
        case "downloads" or "download" or "d": return RoleId.Downloads;
        case "default" or "windows": return RoleId.Default;
        default:
            Console.Error.WriteLine($"'{text}' isn't a network. Use gaming, downloads or default.");
            return null;
    }
}

/// <summary>
/// An .exe path is used as-is. Otherwise the name is looked up among installed and running
/// apps, packaged ones included, so a Game Pass title can be added by name without going
/// anywhere near WindowsApps (§10).
/// </summary>
static async Task<AppIdentity?> ResolveAppAsync(string target)
{
    // A folder means every program inside it, which is how you route a whole games library.
    if (Directory.Exists(target))
    {
        return AppIdentity.ForFolder(Path.GetFullPath(target));
    }

    if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
    {
        var full = Path.GetFullPath(target);
        var description = System.Diagnostics.FileVersionInfo.GetVersionInfo(full).FileDescription;
        return AppIdentity.ForExecutable(full, string.IsNullOrWhiteSpace(description) ? null : description);
    }

    var packaged = await new NetRoute.Windows.Apps.PackagedAppDiscovery().DiscoverAsync();
    var all = new NetRoute.Windows.Apps.Win32AppDiscovery().Discover().Concat(packaged).ToList();

    var matches = all.Where(a => a.DisplayName.Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 0)
    {
        matches = all.Where(a => a.DisplayName.Contains(target, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    if (matches.Count == 1)
    {
        // Keep the install folder so every program file of a game can be moved, not just one.
        return matches[0].Identity with { InstallLocation = matches[0].InstallLocation };
    }

    Console.Error.WriteLine(matches.Count == 0
        ? $"No installed or running app matches '{target}'. Pass the full path to its .exe instead."
        : $"'{target}' matches several apps; be more specific or pass the .exe path:\n  " +
          string.Join("\n  ", matches.Take(10).Select(m => $"{m.DisplayName}  ({m.Identity.ExecutablePath ?? m.Identity.PackageFamilyName})")));
    return null;
}

static void PrintStatus(ServiceStatusDto status)
{
    Console.WriteLine(status.EnforcementPaused
        ? status.PausedUntil is { } until ? $"Enforcement: paused until {until.ToLocalTime():HH:mm}" : "Enforcement: paused"
        : status.EnforcementActive ? "Enforcement: active" : "Enforcement: unavailable");
    if (status.Update is { } update) Console.WriteLine($"Update: NetRoute {update.Version} is available - {update.Url}");
    if (status.SystemDownloads is { } system) Console.WriteLine($"Windows downloads: {system.Summary}");
    if (status.DownloadsPause is { } pause && (pause.Enabled || pause.QuietHours is not null))
    {
        Console.WriteLine(pause.PausedFor is { } reason
            ? $"Downloads: paused, {reason}."
            : "Downloads: will pause"
              + (pause.Enabled ? " while a game is running" : "")
              + (pause.QuietHours is { } window ? $"{(pause.Enabled ? ", and" : "")} between {window}" : "") + ".");
    }
    if (status.LastError is { } error) Console.WriteLine($"Problem: {error.FriendlyMessage}");
    if (status.RedirectSummary is { } redirect) Console.WriteLine($"Moving apps: {redirect}");
    if (status.RouteTie is { } tie) Console.WriteLine($"Problem: {tie.Message} Run 'netroute fix-routes' to fix it.");
    PrintRoles(status.Roles);
    PrintApps(status.Apps);
}

static void PrintRoles(IEnumerable<RoleStatusDto> roles)
{
    foreach (var role in roles) Console.WriteLine($"{role.Role.Glyph()} {role.Role.DisplayName(),-10} {role.Health,-10} {role.Adapter?.Name ?? role.LastKnownName ?? "Unassigned"} ({role.AssignedApps} apps)");
}

static void PrintApps(IEnumerable<AppStatusDto> apps)
{
    foreach (var app in apps) Console.WriteLine($"{app.Rule.Role.Glyph()} {app.Rule.App.DisplayName,-28} {app.Action,-18} {app.VerificationSummary}");
}

static int PrintWhy(ServiceStatusDto status, string query)
{
    var matches = status.Apps.Where(a => a.Rule.App.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 0) { Console.Error.WriteLine($"No app contains '{query}'."); return 1; }
    foreach (var app in matches)
    {
        Console.WriteLine($"{app.Rule.App.DisplayName}:");
        foreach (var reason in app.Reasons) Console.WriteLine($"  {(reason.Good ? "OK" : "!!")} {reason.Text}");
    }
    return 0;
}

/// <summary>
/// Emergency Disable when the service can't be reached (§43). Stopping the service removes
/// its WFP filters, because they live in a dynamic session, and its shutdown resets the
/// driver. The local cleanup afterwards covers a service that crashed instead of stopping.
/// </summary>
static int StopServiceFallback()
{
    try
    {
        using var service = new ServiceController("NetRoute");
        if (service.Status != ServiceControllerStatus.Stopped)
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        Console.WriteLine("NetRoute service stopped. Windows removed all of its filters.");
    }
    catch (InvalidOperationException)
    {
        // Not installed, or not running. Nothing to stop.
    }
    catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
    {
        Console.Error.WriteLine("Run this from an administrator terminal:  netroute emergency-disable");
        return 1;
    }
    return LocalCleanup(removeSublayers: false);
}

/// <summary>fix-routes without the service. Changing interface metrics needs an administrator terminal.</summary>
static int LocalFixRoutes()
{
    var adapters = new NetRoute.Core.Adapters.AdapterDiscovery().DiscoverAll();
    if (NetRoute.Core.Adapters.RouteTies.Find(adapters, new NetRoute.Core.Adapters.DefaultRouteTable().ReadIpv4()) is not { } tie)
    {
        Console.WriteLine("Windows already has one default connection. Nothing to fix.");
        return 0;
    }

    ulong? downloads = null;
    try
    {
        downloads = new NetRoute.Core.Config.ConfigStore().Load().BindingFor(RoleId.Downloads)?.AdapterLuid;
    }
    catch (Exception)
    {
        // No readable config: fall back to the fastest tied connection.
    }
    var preferred = tie.Adapters.FirstOrDefault(a => a.Luid == downloads) ?? tie.Adapters.OrderByDescending(a => a.LinkSpeedBps).First();
    try
    {
        Console.WriteLine(new NetRoute.Windows.Split.DefaultRouteManager().ResolveTie(tie.Adapters, preferred));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Couldn't change the connection settings: {ex.Message}");
        Console.Error.WriteLine("Run this from an administrator terminal:  netroute fix-routes");
        return 1;
    }
}

/// <summary>Resets the split-tunnel driver and restores the default route, without the service.</summary>
static int LocalCleanup(bool removeSublayers)
{
    var ok = true;
    try
    {
        using var driver = NetRoute.Windows.Split.SplitTunnelDriver.Open();
        driver.Reset();
        Console.WriteLine("Split-tunnel driver reset. No apps are being moved.");
    }
    catch (NetRoute.Windows.Split.SplitTunnelException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 2 })
    {
        Console.WriteLine("Split-tunnel driver isn't running. Nothing to reset.");
    }
    catch (Exception ex)
    {
        ok = false;
        Console.Error.WriteLine($"Couldn't reset the split-tunnel driver: {ex.Message}");
    }

    try
    {
        new NetRoute.Windows.Split.DefaultRouteManager().Restore();
        Console.WriteLine("Windows' default connection settings are back to how they were.");
    }
    catch (Exception ex)
    {
        ok = false;
        Console.Error.WriteLine($"Couldn't restore the default connection settings: {ex.Message}");
    }

    if (removeSublayers)
    {
        try
        {
            NetRoute.Windows.Split.SplitTunnelSublayers.Remove();
            Console.WriteLine("NetRoute's WFP sublayers removed.");
        }
        catch (Exception ex)
        {
            ok = false;
            Console.Error.WriteLine($"Couldn't remove NetRoute's WFP sublayers: {ex.Message}");
        }
    }
    return ok ? 0 : 1;
}
