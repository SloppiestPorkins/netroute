using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Text.RegularExpressions;
using NetRoute.Core.Adapters;
using NetRoute.Windows.Split;

namespace NetRoute.Poc;

/// <summary>
/// Proves that the split-tunnel driver really moves an unmodified app onto another adapter.
///
/// <para>This is the test the whole product depends on. Unlike the WFP proof, it never
/// passes --interface: curl and nslookup make ordinary unbound connections. If they
/// still come out through the non-default adapter's ISP, the driver moved them. TCP is
/// checked with curl; UDP with nslookup's direct TXT query to ns1.google.com, which
/// answers with the querying socket's public address.</para>
///
/// <para>The driver is always Reset on the way out, including on failure. Reset is its off
/// switch; stopping the driver without one crashes Windows.</para>
/// </summary>
internal static partial class SplitProof
{
    private const string Curl = @"C:\Windows\System32\curl.exe";
    private const string Nslookup = @"C:\Windows\System32\nslookup.exe";

    // ns1.google.com, by address. Querying it by name let nslookup pick IPv6 on the first
    // run, which tested IPv6 routing rather than UDP.
    private const string Ns1V4 = "216.239.32.10";
    private const string Ns1V6 = "2001:4860:4802:32::a";

    public static async Task<int> RunAsync()
    {
        if (!IsElevated())
        {
            Console.WriteLine("Run this from an administrator terminal: the driver only accepts administrators.");
            return 2;
        }

        var usable = new AdapterDiscovery().DiscoverSelectable()
            .Where(a => a.Ipv4Address is not null && a.HasIpv4Gateway)
            .OrderBy(a => a.Ipv4Metric)
            .ToList();
        if (usable.Count < 2)
        {
            Console.WriteLine($"Need two connected adapters; found {usable.Count}.");
            return 1;
        }

        // The driver keeps split apps OFF the "tunnel" address and moves them ONTO the
        // "internet" one. Treating the default-route adapter as the tunnel means a pass can
        // only come from the driver: without it, these apps would use the default adapter.
        var stay = usable[0];
        var move = usable[1];

        Console.WriteLine("=== SPLIT-TUNNEL DRIVER PROOF ===\n");
        Console.WriteLine($"  Windows default : {stay.Name} ({stay.Ipv4Address})");
        Console.WriteLine($"  Move apps onto  : {move.Name} ({move.Ipv4Address})");
        Console.WriteLine($"  Apps            : curl.exe (TCP), nslookup.exe (UDP), never bound to an interface\n");

        var stayTcp = await Run(Curl, "--silent", "--max-time", "10", "--interface", stay.Ipv4Address!.ToString(), "https://api.ipify.org");
        var moveTcp = await Run(Curl, "--silent", "--max-time", "10", "--interface", move.Ipv4Address!.ToString(), "https://api.ipify.org");
        var before = await Run(Curl, "--silent", "--max-time", "10", "https://api.ipify.org");
        Console.WriteLine($"Baseline: {stay.Name} egress {Show(stayTcp)}, {move.Name} egress {Show(moveTcp)}, unbound curl {Show(before)}");

        if (stayTcp is null || moveTcp is null || stayTcp == moveTcp)
        {
            Console.WriteLine("\nINCONCLUSIVE - the two adapters must both work and reach different public addresses.");
            return 1;
        }

        SplitTunnelDriver driver;
        try
        {
            driver = SplitTunnelDriver.Open();
        }
        catch (SplitTunnelException ex)
        {
            Console.WriteLine($"\n{ex.Message}\n  ({ex.InnerException?.Message})");
            Console.WriteLine("  Install Mullvad VPN, stop its service, and start the driver - see docs/SPLIT-TUNNEL.md.");
            return 2;
        }

        var passed = false;
        using (driver)
        {
            try
            {
                SplitTunnelSublayers.EnsureCreated();
                driver.Reinitialize(SplitTunnelSublayers.Baseline, SplitTunnelSublayers.Dns);
                driver.RegisterIpAddresses(stay.Ipv4Address, move.Ipv4Address, null, null);
                driver.SetConfiguration([DevicePaths.ToNtPath(Curl), DevicePaths.ToNtPath(Nslookup)]);
                Console.WriteLine($"\nDriver state: {driver.GetState()} (expect Engaged)");

                var tcp = await Run(Curl, "--silent", "--max-time", "10", "https://api.ipify.org");
                var udp = ParseTxtAddress(await Run(Nslookup, "-timeout=5", "-type=TXT", "o-o.myaddr.l.google.com", Ns1V4));
                var v6 = ParseTxtAddress(await Run(Nslookup, "-timeout=5", "-type=TXT", "o-o.myaddr.l.google.com", Ns1V6));

                Console.WriteLine($"\nWith splitting on:");
                Console.WriteLine($"  curl (TCP)     egress {Show(tcp)}   expected {moveTcp}");
                Console.WriteLine($"  nslookup (UDP) egress {Show(udp)}");
                if (v6 is not null)
                {
                    // Informational, not pass/fail. The driver can only move IPv6 onto a connection
                    // that has IPv6. When the target has none, the app's IPv6 keeps leaving through
                    // the default adapter. That is the section 23 bypass, and the reason NetRoute
                    // blocks IPv6 for apps on an IPv4-only network, so they fall back to IPv4 and
                    // get moved.
                    Console.WriteLine($"  IPv6 (info)    egress {v6} - not moved: {move.Name} has no IPv6, so NetRoute blocks IPv6 for these apps in real use.");
                }

                var tcpMoved = tcp == moveTcp;
                Console.WriteLine();
                Console.WriteLine(tcpMoved
                    ? $"  TCP: PROVEN - an unbound curl left through {move.Name}, not the default {stay.Name}."
                    : tcp == stayTcp
                        ? $"  TCP: FAILED - curl still egresses via the default adapter {stay.Name}."
                        : tcp is null ? "  TCP: FAILED - curl was blocked." : $"  TCP: FAILED - unexpected egress {tcp}.");

                // The UDP answer is the address Google saw. Compare it with what the same
                // query returns once splitting is off, rather than assuming it equals the
                // TCP address: on this machine the Wi-Fi uplink shows different addresses to
                // HTTP and DNS (carrier NAT), see docs/RESEARCH.md.
                driver.Reset();
                var udpDefault = ParseTxtAddress(await Run(Nslookup, "-timeout=5", "-type=TXT", "o-o.myaddr.l.google.com", Ns1V4));
                var udpMoved = udp is not null && udp != udpDefault;
                Console.WriteLine(udpMoved
                    ? $"  UDP: PROVEN - nslookup's query left from {udp}, not the default {udpDefault}."
                    : udp is null ? "  UDP: FAILED - nslookup got no answer while split." : $"  UDP: FAILED - same egress ({udp}) with and without splitting.");

                passed = tcpMoved && udpMoved;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nFAILED: {ex.Message}\n  {ex.InnerException?.Message}");
            }
            finally
            {
                try { driver.Reset(); } catch (Exception ex) { Console.WriteLine($"WARNING: reset failed: {ex.Message}"); }
            }
        }

        var after = await Run(Curl, "--silent", "--max-time", "10", "https://api.ipify.org");
        Console.WriteLine($"\nAfter reset: unbound curl egress {Show(after)} (expect {stayTcp})");
        return passed && after == stayTcp ? 0 : 1;
    }

    private static string Show(string? value) => value ?? "BLOCKED/none";

    private static string? ParseTxtAddress(string? output)
    {
        if (output is null)
        {
            return null;
        }
        var match = QuotedAddress().Match(output);
        return match.Success && IPAddress.TryParse(match.Groups[1].Value, out _) ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("\"([0-9a-fA-F.:]+)\"")]
    private static partial Regex QuotedAddress();

    private static async Task<string?> Run(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }
        using var p = Process.Start(info)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var trimmed = output.Trim();
        return p.ExitCode == 0 && trimmed.Length > 0 ? trimmed : (exe == Nslookup && trimmed.Length > 0 ? trimmed : null);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
