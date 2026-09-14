using System.Net;
using System.Security.Principal;
using NetRoute.Core.Adapters;
using NetRoute.Windows.Traffic;

namespace NetRoute.Poc;

/// <summary>Checks for what the service measures: per-adapter ping (probe) and per-app speed (meter, needs admin).</summary>
internal static class MeterProof
{
    public static int Probe()
    {
        var target = IPAddress.Parse("1.1.1.1");
        Console.WriteLine("=== PING 1.1.1.1 FROM EACH ADAPTER ===\n");
        foreach (var adapter in new AdapterDiscovery().DiscoverSelectable().Where(a => a.Ipv4Address is not null && a.HasIpv4Gateway))
        {
            var results = Enumerable.Range(0, 5).Select(_ => LinkProbe.Ping(adapter.Ipv4Address!, target)).ToList();
            Console.WriteLine($"  {adapter.Name,-24} {string.Join("  ", results.Select(r => r is null ? "lost" : $"{r} ms"))}");
        }
        return 0;
    }

    public static int Meter()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("Run this from an administrator terminal: kernel network tracing needs it.");
                return 2;
            }
        }

        using var meter = new ProcessNetworkMeter(new AdapterDiscovery());
        meter.Start();
        if (!meter.Running)
        {
            Console.WriteLine(meter.Problem);
            return 2;
        }
        Console.WriteLine("=== PER-APP SPEED (ETW), 6 readings ===");
        meter.Rates();
        for (var i = 0; i < 6; i++)
        {
            Thread.Sleep(2000);
            Console.WriteLine($"\n--- {DateTime.Now:T}");
            foreach (var r in meter.Rates().OrderByDescending(r => r.DownBytesPerSecond + r.UpBytesPerSecond).Take(10))
            {
                Console.WriteLine($"  {r.ProcessName,-26} {r.AdapterName ?? "?",-12} down {r.DownBytesPerSecond / 1024,8:0} KB/s   up {r.UpBytesPerSecond / 1024,8:0} KB/s");
            }
        }
        return 0;
    }
}
