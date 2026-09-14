using System.Diagnostics;
using NetRoute.Core.Adapters;

namespace NetRoute.Poc;

/// <summary>
/// Prints Windows' IPv4 default routes as NetRoute reads them (raw GetIpForwardTable2 rows),
/// next to PowerShell's view of the same routes, and whether they tie. The two tables should
/// agree; if they don't, the row offsets in IpHelper are wrong.
/// </summary>
internal static class RouteTableProof
{
    public static int Run()
    {
        var adapters = new AdapterDiscovery().DiscoverAll();
        var routes = new DefaultRouteTable().ReadIpv4();

        Console.WriteLine("=== DEFAULT ROUTES (NetRoute, GetIpForwardTable2) ===\n");
        foreach (var r in routes)
        {
            var a = adapters.FirstOrDefault(x => x.Luid == r.Luid);
            var total = a is null ? "?" : (r.RouteMetric + a.Ipv4Metric).ToString();
            Console.WriteLine($"  {a?.Name ?? "?",-28} ifIndex {r.InterfaceIndex,-4} route {r.RouteMetric,-5} interface {a?.Ipv4Metric.ToString() ?? "?",-5} total {total}");
        }

        Console.WriteLine("\n=== DEFAULT ROUTES (PowerShell, Get-NetRoute) ===");
        var info = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "-NoProfile", "-Command",
                     "Get-NetRoute -DestinationPrefix 0.0.0.0/0 | Format-Table ifIndex,InterfaceAlias,RouteMetric," +
                     "@{n='InterfaceMetric';e={(Get-NetIPInterface -InterfaceIndex $_.ifIndex -AddressFamily IPv4).InterfaceMetric}} -AutoSize | Out-String -Width 200" })
        {
            info.ArgumentList.Add(arg);
        }
        using (var p = Process.Start(info)!)
        {
            Console.WriteLine(p.StandardOutput.ReadToEnd().TrimEnd());
            p.WaitForExit();
        }

        var tie = RouteTies.Find(adapters, routes);
        Console.WriteLine(tie is null
            ? "\nNo tie: Windows has one default connection."
            : $"\nTIE: {tie.Names} share total cost {tie.Cost}. Run 'netroute fix-routes' (admin) to fix it.");
        return 0;
    }
}
