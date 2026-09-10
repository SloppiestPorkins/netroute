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
          pause | resume                 stop or restart enforcement, keeping your rules
          emergency-disable              remove every NetRoute rule from Windows right now
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
            case "pause": await client.SetEnforcementPausedAsync(true); Console.WriteLine("NetRoute enforcement paused."); break;
            case "resume": await client.SetEnforcementPausedAsync(false); Console.WriteLine("NetRoute enforcement resumed."); break;
            case "emergency-disable": await client.EmergencyDisableAsync(); Console.WriteLine("All NetRoute filters removed and enforcement paused."); break;
            case "adapters": PrintAdapters(await client.GetAdaptersAsync()); break;
            case "setup" when args.Length >= 3: return await SetupAsync(client, args[1], args[2]);
            case "add" when args.Length >= 3: return await AddAsync(client, string.Join(' ', args[1..^1]), args[^1]);
            case "move" when args.Length >= 3: return await MoveAsync(client, string.Join(' ', args[1..^1]), args[^1]);
            case "remove" when args.Length >= 2: return await RemoveAsync(client, string.Join(' ', args[1..]));
            default: Console.Error.WriteLine(Usage); return 1;
        }
        return 0;
    }
    catch (ServiceUnavailableException) when (command == "emergency-disable") { return StopServiceFallback(); }
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
    var matches = status.Apps.Where(a => a.Rule.App.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
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
        return matches[0].Identity;
    }

    Console.Error.WriteLine(matches.Count == 0
        ? $"No installed or running app matches '{target}'. Pass the full path to its .exe instead."
        : $"'{target}' matches several apps; be more specific or pass the .exe path:\n  " +
          string.Join("\n  ", matches.Take(10).Select(m => $"{m.DisplayName}  ({m.Identity.ExecutablePath ?? m.Identity.PackageFamilyName})")));
    return null;
}

static void PrintStatus(ServiceStatusDto status)
{
    Console.WriteLine(status.EnforcementPaused ? "Enforcement: paused" : status.EnforcementActive ? "Enforcement: active" : "Enforcement: unavailable");
    if (status.LastError is { } error) Console.WriteLine($"Problem: {error.FriendlyMessage}");
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

static int StopServiceFallback()
{
    try
    {
        using var service = new ServiceController("NetRoute");
        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
        Console.WriteLine("NetRoute service stopped. Its dynamic WFP session closed, so Windows removed all filters.");
        return 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
    {
        Console.Error.WriteLine("The service could not be stopped. Run this exact command from an elevated terminal:");
        Console.Error.WriteLine("net stop NetRoute");
        return ex is Win32Exception { NativeErrorCode: 5 } ? 1 : 2;
    }
}
