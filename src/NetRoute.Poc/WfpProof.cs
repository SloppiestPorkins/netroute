using System.Diagnostics;
using System.Security.Principal;
using NetRoute.Core.Adapters;
using NetRoute.Core.Config;
using NetRoute.Core.Policy;
using NetRoute.Windows.Wfp;

namespace NetRoute.Poc;

/// <summary>
/// Proves that WFP enforcement is real: that a rule pinning an application to one role
/// actually stops that application reaching the network through any other adapter.
///
/// <para>The method is deliberately falsifiable. It pins <c>curl.exe</c> to the Gaming
/// role, then runs curl twice — once bound to the Gaming adapter, once bound to the
/// other one. Correct enforcement produces a success and a failure. If both succeed,
/// the filters are not doing anything and the test says so; §24 does not allow
/// "a filter exists" to be reported as protection.</para>
///
/// <para>Note what this does not prove. These filters block, they do not redirect —
/// that needs the bind-redirect callout driver. An unmodified application pinned to a
/// role it would not naturally use gets blocked, not moved. That is the honest state
/// of the user-mode half and the reason the driver is on the plan.</para>
/// </summary>
internal static class WfpProof
{
    private const string CurlPath = @"C:\Windows\System32\curl.exe";

    public static async Task<int> RunAsync()
    {
        if (!IsElevated())
        {
            Console.WriteLine("This proof needs administrator rights to manage WFP filters.");
            Console.WriteLine("Re-run from an elevated terminal.");
            return 2;
        }

        if (!File.Exists(CurlPath))
        {
            Console.WriteLine($"Could not find {CurlPath}, which this proof uses as the test application.");
            return 2;
        }

        var discovery = new AdapterDiscovery();
        var usable = discovery.DiscoverSelectable().Where(a => a.Ipv4Address is not null).ToList();

        if (usable.Count < 2)
        {
            Console.WriteLine($"Need two connected adapters; found {usable.Count}.");
            return 1;
        }

        var gaming = usable[0];
        var other = usable[1];

        Console.WriteLine("=== WFP ENFORCEMENT PROOF ===");
        Console.WriteLine();
        Console.WriteLine($"  Test application : {CurlPath}");
        Console.WriteLine($"  Pinned to        : Gaming -> {gaming.Name} ({gaming.Ipv4Address})");
        Console.WriteLine($"  Must be blocked  : {other.Name} ({other.Ipv4Address})");
        Console.WriteLine();

        Console.WriteLine("Baseline, before any filters are installed:");
        var baselineGaming = await CurlVia(gaming);
        var baselineOther = await CurlVia(other);
        Console.WriteLine($"  via {gaming.Name,-10} {Describe(baselineGaming)}");
        Console.WriteLine($"  via {other.Name,-10} {Describe(baselineOther)}");

        if (!baselineGaming.Success || !baselineOther.Success)
        {
            // Without a working baseline on both adapters, a later failure cannot be
            // attributed to enforcement rather than to the network.
            Console.WriteLine();
            Console.WriteLine("  INCONCLUSIVE - both adapters must work before filtering to attribute the result.");
            return 1;
        }

        var config = BuildConfig(gaming);
        var plan = new PolicyResolver(discovery).Resolve(config);

        using var session = WfpSession.Open();
        var enforcer = new WfpEnforcer(session, discovery);

        var result = enforcer.Apply(plan);

        Console.WriteLine();
        Console.WriteLine($"Installed {result.TotalFilters} filters across {result.Applied.Count} rule(s).");
        foreach (var failure in result.Failed)
        {
            Console.WriteLine($"  FAILED {failure.Enforcement.Rule.App.DisplayName}: {failure.Error.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("With enforcement active:");
        var enforcedGaming = await CurlVia(gaming);
        var enforcedOther = await CurlVia(other);
        Console.WriteLine($"  via {gaming.Name,-10} {Describe(enforcedGaming)}");
        Console.WriteLine($"  via {other.Name,-10} {Describe(enforcedOther)}");

        Console.WriteLine();
        var verdict = Verdict(enforcedGaming.Success, enforcedOther.Success, gaming.Name, other.Name);
        Console.WriteLine(verdict.Text);

        Console.WriteLine();
        Console.WriteLine("Removing filters...");
        enforcer.EmergencyDisable();

        var restored = await CurlVia(other);
        Console.WriteLine($"  via {other.Name,-10} {Describe(restored)}");
        Console.WriteLine(restored.Success
            ? "  Normal networking restored."
            : "  WARNING: traffic still blocked after removing filters. Investigate before trusting cleanup.");

        return verdict.Passed && restored.Success ? 0 : 1;
    }

    private static (bool Passed, string Text) Verdict(
        bool gamingWorked, bool otherWorked, string gamingName, string otherName)
    {
        return (gamingWorked, otherWorked) switch
        {
            (true, false) =>
                (true, $"  PROVEN - curl works on {gamingName} and is blocked on {otherName}. " +
                       "Per-application, per-interface enforcement is real."),

            (true, true) =>
                (false, $"  FAILED - curl still reached the internet via {otherName}. " +
                        "The block filter is not matching; enforcement is not doing anything."),

            (false, false) =>
                (false, "  FAILED - curl is blocked on both adapters. The permit filter is not matching, " +
                        "so the rule is blocking traffic it was supposed to allow."),

            (false, true) =>
                (false, $"  FAILED - enforcement is inverted: blocked on {gamingName}, allowed on {otherName}.")
        };
    }

    private static NetRouteConfig BuildConfig(NetworkAdapter gaming)
    {
        var curl = AppIdentity.ForExecutable(CurlPath, "curl");

        return new NetRouteConfig
        {
            SetupCompleted = true,
            RoleBindings =
            [
                new RoleBinding
                {
                    Role = RoleId.Gaming,
                    AdapterLuid = gaming.Luid,
                    AdapterGuid = gaming.Guid,
                    LastKnownName = gaming.Name
                }
            ],
            AppRules = [AppRule.Create(curl, RoleId.Gaming)]
        };
    }

    /// <summary>
    /// Runs curl bound to a specific source address. Binding is what lets us aim the
    /// request at one adapter without needing redirection.
    /// </summary>
    private static async Task<CurlResult> CurlVia(NetworkAdapter adapter)
    {
        var info = new ProcessStartInfo
        {
            FileName = CurlPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("--silent");
        info.ArgumentList.Add("--max-time");
        info.ArgumentList.Add("10");
        info.ArgumentList.Add("--interface");
        info.ArgumentList.Add(adapter.Ipv4Address!.ToString());
        info.ArgumentList.Add("https://api.ipify.org");

        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CurlResult(process.ExitCode == 0 && stdout.Trim().Length > 0, stdout.Trim(), stderr.Trim());
    }

    private static string Describe(CurlResult result)
        => result.Success ? $"OK      egress {result.Output}" : "BLOCKED";

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record CurlResult(bool Success, string Output, string Error);
}
