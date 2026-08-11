using System.Net;
using NetRoute.Core.Adapters;

namespace NetRoute.Poc;

/// <summary>
/// NetRoute routing proof-of-concept (§50).
///
/// <para>Answers one question before any UI gets built: can two independent flows be
/// driven out of two different physical adapters at the same time, and can we
/// independently confirm which adapter each one actually used?</para>
///
/// <para>Confirming it is the important half. Anything can claim a rule is applied.
/// This probes the public egress address per flow, so a wrong answer is visible
/// rather than assumed.</para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "wfp":
                return await WfpProof.RunAsync();
            case "apps":
                return await AppDiscoveryProof.RunAsync();
        }

        var discovery = new AdapterDiscovery();
        var all = discovery.DiscoverAll();

        PrintAdapterTable(all);

        var candidates = all
            .Where(a => a.IsSelectableAsRole && a.Ipv4Address is not null)
            .ToList();

        if (candidates.Count < 2)
        {
            Console.WriteLine();
            Console.WriteLine("Need two connected adapters with IPv4 to run the routing proof.");
            Console.WriteLine($"Found {candidates.Count}.");
            return 1;
        }

        // Default to the two fastest connected adapters; allow an override by name
        // so this can be pointed at a specific pair during testing.
        var gaming = Pick(candidates, args.ElementAtOrDefault(0)) ?? candidates[0];
        var downloads = Pick(candidates, args.ElementAtOrDefault(1))
                        ?? candidates.First(a => a.Luid != gaming.Luid);

        if (gaming.Luid == downloads.Luid)
        {
            Console.WriteLine("\nGaming and Downloads resolved to the same adapter; nothing to prove.");
            return 1;
        }

        await RunProof(gaming, downloads);
        return 0;
    }

    private static async Task RunProof(NetworkAdapter gaming, NetworkAdapter downloads)
    {
        Console.WriteLine();
        Console.WriteLine("=== ROUTING PROOF ===");
        Console.WriteLine();
        Console.WriteLine($"  Gaming    -> {gaming.Name} ({gaming.Ipv4Address})");
        Console.WriteLine($"  Downloads -> {downloads.Name} ({downloads.Ipv4Address})");
        Console.WriteLine();
        Console.WriteLine("Probing both roles concurrently, TCP and UDP...");
        Console.WriteLine();

        // Concurrent by design. Running them in sequence would not distinguish
        // "both adapters work" from "both adapters work at the same time", and the
        // simultaneous case is the entire product.
        var gamingTcp = EgressProbe.TcpAsync(gaming.Ipv4Address!);
        var downloadsTcp = EgressProbe.TcpAsync(downloads.Ipv4Address!);
        var gamingUdp = EgressProbe.UdpAsync(gaming.Ipv4Address!);
        var downloadsUdp = EgressProbe.UdpAsync(downloads.Ipv4Address!);

        await Task.WhenAll(gamingTcp, downloadsTcp, gamingUdp, downloadsUdp);

        Report("Gaming    TCP", gaming, gamingTcp.Result);
        Report("Downloads TCP", downloads, downloadsTcp.Result);
        Report("Gaming    UDP", gaming, gamingUdp.Result);
        Report("Downloads UDP", downloads, downloadsUdp.Result);

        Console.WriteLine();
        Verdict("TCP", gamingTcp.Result, downloadsTcp.Result);
        Verdict("UDP", gamingUdp.Result, downloadsUdp.Result);

        WarnOnIpv6Asymmetry(gaming, downloads);
    }

    private static void Report(string label, NetworkAdapter adapter, ProbeResult result)
    {
        if (result.Success)
        {
            Console.WriteLine($"  {label}  bound {result.BoundLocalAddress,-15}  egress {result.PublicAddress}");
        }
        else
        {
            Console.WriteLine($"  {label}  bound {adapter.Ipv4Address,-15}  FAILED: {result.Error}");
        }
    }

    private static void Verdict(string protocol, ProbeResult gaming, ProbeResult downloads)
    {
        if (!gaming.Success || !downloads.Success)
        {
            Console.WriteLine($"  {protocol}: INCONCLUSIVE - a probe failed.");
            return;
        }

        if (gaming.PublicAddress == downloads.PublicAddress)
        {
            // Same egress means both adapters sit behind one uplink. Source binding may
            // still be selecting the right interface, but this test cannot show it, and
            // saying otherwise would be exactly the kind of claim §24 rules out.
            Console.WriteLine(
                $"  {protocol}: NOT PROVEN - both roles egress {gaming.PublicAddress}. " +
                "Same upstream network, so egress address cannot distinguish them.");
            return;
        }

        Console.WriteLine(
            $"  {protocol}: PROVEN - roles egress via distinct uplinks " +
            $"({gaming.PublicAddress} vs {downloads.PublicAddress}).");
    }

    private static void WarnOnIpv6Asymmetry(NetworkAdapter gaming, NetworkAdapter downloads)
    {
        if (gaming.HasIpv6Gateway == downloads.HasIpv6Gateway)
        {
            return;
        }

        var withoutV6 = gaming.HasIpv6Gateway ? downloads : gaming;
        var withV6 = gaming.HasIpv6Gateway ? gaming : downloads;

        Console.WriteLine();
        Console.WriteLine("  IPv6 ASYMMETRY DETECTED");
        Console.WriteLine($"    {withoutV6.Name} has no IPv6 default route; {withV6.Name} does.");
        Console.WriteLine($"    Apps pinned to {withoutV6.Name} will send IPv6 out of {withV6.Name},");
        Console.WriteLine("    bypassing the policy entirely. Enforcement must block IPv6 for that role.");
    }

    private static NetworkAdapter? Pick(IEnumerable<NetworkAdapter> candidates, string? name)
        => name is null
            ? null
            : candidates.FirstOrDefault(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static void PrintAdapterTable(IReadOnlyList<NetworkAdapter> adapters)
    {
        Console.WriteLine("=== DISCOVERED ADAPTERS ===");
        Console.WriteLine();
        Console.WriteLine($"{"NAME",-30} {"KIND",-9} {"STATE",-13} {"SPEED",-10} {"IPV4",-16} {"V6?",-4} LUID");
        Console.WriteLine(new string('-', 110));

        foreach (var a in adapters.OrderByDescending(a => a.IsSelectableAsRole).ThenBy(a => a.Name))
        {
            Console.WriteLine(
                $"{Truncate(a.Name, 30),-30} " +
                $"{a.Kind,-9} " +
                $"{a.State,-13} " +
                $"{a.LinkSpeedDisplay,-10} " +
                $"{a.Ipv4Address?.ToString() ?? "-",-16} " +
                $"{(a.HasIpv6Gateway ? "yes" : "no"),-4} " +
                $"0x{a.Luid:X16}");
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "~";
}
