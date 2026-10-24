using System.Diagnostics;
using System.Security.Principal;
using NetRoute.Core.Policy;
using NetRoute.Windows.Wfp;
using NetRoute.Core.Adapters;

namespace NetRoute.Poc;

/// <summary>
/// Settles, on this machine, whether two <c>ALE_APP_ID</c> conditions in one WFP filter are ORed
/// (either program matches) or ANDed (a program would have to be both, so none ever is).
///
/// <para>It matters because <see cref="WfpEnforcer.CollapseProgramFilesByDefault"/> would replace
/// one filter per program file with one filter holding all of them. If same-field conditions AND,
/// that filter matches nothing and the rule protects nothing, with no error anywhere. So this runs
/// the real enforcer both ways and makes real connections.</para>
///
/// <para>The method is falsifiable. Three copies of curl: two in a folder the rule covers, one
/// outside it. The rule blocks the folder's programs outright (the "role unavailable" shape, so no
/// second network is needed). If same-field conditions OR, the two in the folder are blocked and
/// the outsider is not. If they AND, nothing is blocked in the collapsed run while the
/// one-filter-per-file run, which is the control, still blocks both.</para>
/// </summary>
internal static class WfpOrProof
{
    private const string CurlPath = @"C:\Windows\System32\curl.exe";

    public static async Task<int> RunAsync()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("This proof needs administrator rights to manage WFP filters. Re-run from an elevated terminal.");
                return 2;
            }
        }
        if (!File.Exists(CurlPath))
        {
            Console.WriteLine($"Could not find {CurlPath}, which this proof copies to make its test programs.");
            return 2;
        }

        var root = Path.Combine(Path.GetTempPath(), "netroute-wfp-or");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
        var covered = Path.Combine(root, "covered");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(covered);
        Directory.CreateDirectory(outside);
        var a = Path.Combine(covered, "curl_a.exe");
        var b = Path.Combine(covered, "curl_b.exe");
        var c = Path.Combine(outside, "curl_c.exe");
        File.Copy(CurlPath, a);
        File.Copy(CurlPath, b);
        File.Copy(CurlPath, c);

        try
        {
            Console.WriteLine("=== WFP SAME-FIELD CONDITION PROOF ===");
            Console.WriteLine("Question: does one WFP filter with two ALE_APP_ID conditions match EITHER program (OR) or only a program that is both (AND)?");
            Console.WriteLine("Answer line to look for at the end:  'OR CONFIRMED' (safe to collapse)  or  'AND' (never collapse).");
            Console.WriteLine("Runs beside a running NetRoute service: its filters use a separate provider and sublayer.");
            Console.WriteLine();
            Console.WriteLine("Baseline, no filters:");
            var baseline = await Probe(a, b, c);
            Print(baseline);
            if (!baseline.All(r => r.Reached))
            {
                Console.WriteLine("  INCONCLUSIVE - all three programs must reach the internet first, or a failure can't be blamed on a filter.");
                return 1;
            }

            var control = await RunMode("one filter per program file (control)", collapse: false, covered, a, b, c);
            var collapsed = await RunMode("one filter per layer, every file as a condition", collapse: true, covered, a, b, c);

            Console.WriteLine();
            if (control.Failures > 0 || control.Filters == 0 || collapsed.Failures > 0 || collapsed.Filters == 0)
            {
                // A filter the engine refused to install blocks nothing, which would read as "AND" below. It is not an answer.
                Console.WriteLine("  INCONCLUSIVE - a run installed no filters or reported a failure (see FAILED lines above). If the collapsed run was refused by the engine, that itself means do NOT collapse; otherwise fix the failure and re-run.");
                return 1;
            }
            if (!control.BlocksBoth || control.OutsiderBlocked)
            {
                Console.WriteLine("  INCONCLUSIVE - the control did not behave (it must block both covered programs and not the outsider), so the comparison means nothing.");
                return 1;
            }
            if (collapsed.BlocksBoth && !collapsed.OutsiderBlocked)
            {
                Console.WriteLine("  OR CONFIRMED - one filter with two ALE_APP_ID conditions blocked both programs and left the outsider alone.");
                Console.WriteLine($"  Filters: {control.Filters} (per file) vs {collapsed.Filters} (collapsed). Commit: {control.Commit.TotalMilliseconds:F1} ms vs {collapsed.Commit.TotalMilliseconds:F1} ms.");
                Console.WriteLine("  It is safe to set WfpEnforcer.CollapseProgramFilesByDefault to true.");
                return 0;
            }
            if (!collapsed.BlocksAny)
            {
                Console.WriteLine("  AND - the collapsed filter blocked nothing. Same-field conditions are ANDed here. Do NOT collapse; leave CollapseProgramFilesByDefault false.");
                return 1;
            }
            Console.WriteLine("  MIXED - the collapsed filter blocked some programs but not the pattern OR would give. Do not collapse; investigate.");
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed record Mode(int Filters, TimeSpan Commit, bool BlocksBoth, bool BlocksAny, bool OutsiderBlocked, int Failures);

    private static async Task<Mode> RunMode(string label, bool collapse, string covered, string a, string b, string c)
    {
        Console.WriteLine();
        Console.WriteLine($"{label}:");
        using var session = WfpSession.Open(PocIdentities.Or);
        PocIdentities.ReportCoexistence(session);
        var enforcer = new WfpEnforcer(session, new AdapterDiscovery(), collapse);

        var rule = AppRule.Create(AppIdentity.ForFolder(covered, "wfp-or proof"), RoleId.Gaming);
        var plan = new EnforcementPlan
        {
            Applications =
            [
                new AppEnforcement
                {
                    Rule = rule,
                    Action = EnforcementAction.BlockAll,
                    ResolvedAdapter = null,
                    BlockIpv6 = false,
                    Reasons = []
                }
            ],
            DegradedRoles = []
        };

        var result = enforcer.Apply(plan);
        var ownFilters = result.TotalFilters;
        Console.WriteLine($"  installed {ownFilters} filter(s) for the rule ({enforcer.InstalledFilterCount} including the always-on permits), commit {result.CommitTime.TotalMilliseconds:F1} ms");
        foreach (var failure in result.Failed)
        {
            Console.WriteLine($"  FAILED {failure.Error.Message}");
        }

        var probes = await Probe(a, b, c);
        Print(probes);
        enforcer.EmergencyDisable();

        var blockedA = !probes[0].Reached;
        var blockedB = !probes[1].Reached;
        return new Mode(ownFilters, result.CommitTime, blockedA && blockedB, blockedA || blockedB, !probes[2].Reached, result.Failed.Count);
    }

    private sealed record Reach(string Name, bool Reached);

    private static async Task<Reach[]> Probe(string a, string b, string c)
        => [await Curl(a), await Curl(b), await Curl(c)];

    private static void Print(IEnumerable<Reach> results)
    {
        foreach (var r in results)
        {
            Console.WriteLine($"  {r.Name,-12} {(r.Reached ? "reached the internet" : "BLOCKED")}");
        }
    }

    private static async Task<Reach> Curl(string path)
    {
        var info = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("--silent");
        info.ArgumentList.Add("--max-time");
        info.ArgumentList.Add("8");
        info.ArgumentList.Add("https://api.ipify.org");

        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new Reach(Path.GetFileName(path), process.ExitCode == 0 && output.Trim().Length > 0);
    }
}
