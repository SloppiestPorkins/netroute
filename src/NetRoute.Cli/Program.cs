// See https://aka.ms/new-console-template for more information
using System.ComponentModel;
using System.ServiceProcess;
using NetRoute.Core.Policy;
using NetRoute.Ipc;

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
            default: Console.Error.WriteLine("Usage: netroute status|roles|apps|why <app>|pause|resume|emergency-disable"); return 1;
        }
        return 0;
    }
    catch (ServiceUnavailableException) when (command == "emergency-disable") { return StopServiceFallback(); }
    catch (ServiceUnavailableException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    catch (NetRouteServiceException ex) { Console.Error.WriteLine(ex.Error.FriendlyMessage); return 1; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
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
