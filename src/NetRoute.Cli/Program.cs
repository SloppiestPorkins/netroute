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
    if (matches.Count == 1)
    {
        return matches[0];
    }
    Console.Error.WriteLine(matches.Count == 0 ? $"No app in your list matches '{query}'." : $"'{query}' matches several apps: {string.Join(", ", matches.Select(m => m.Rule.App.DisplayName))}.");
    return null;
}

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
    if (status.SystemDownloads is { } system) Console.WriteLine($"Windows downloads: {system.Summary}");
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
