using Microsoft.Extensions.Logging.Abstractions;
using NetRoute.Core.Adapters;
using NetRoute.Ipc;
using NetRoute.Service;

namespace NetRoute.Poc;

/// <summary>
/// Runs the service's own self-test without the service, so it can be checked on a machine
/// where NetRoute isn't installed yet. Needs no administrator rights: it only binds sockets,
/// pings and downloads.
/// </summary>
internal static class SelfTestProof
{
    public static async Task<int> RunAsync()
    {
        var adapters = new AdapterDiscovery().DiscoverSelectable()
            .Where(a => a.Ipv4Address is not null && a.HasIpv4Gateway)
            .OrderBy(a => a.Ipv4Metric)
            .ToList();
        if (adapters.Count == 0)
        {
            Console.WriteLine("No usable connection found.");
            return 1;
        }

        var gaming = adapters[0];
        var downloads = adapters.Count > 1 ? adapters[1] : adapters[0];
        Console.WriteLine($"Gaming    : {gaming.Name} ({gaming.Ipv4Address})");
        Console.WriteLine($"Downloads : {downloads.Name} ({downloads.Ipv4Address})\n");

        var runner = new SelfTestRunner(NullLogger<SelfTestRunner>.Instance);
        var state = runner.Start(new SelfTestInput(gaming, downloads, RedirectionAvailable: true, EnforcementPaused: false,
            VerifiedApps: ["(not checked here)"]));

        var shown = 0;
        while (true)
        {
            while (shown < state.Steps.Count && state.Steps[shown].State is not (SelfTestState.Pending or SelfTestState.Running))
            {
                var step = state.Steps[shown++];
                Console.WriteLine($"{step.State,-5} {step.Title}\n      {step.Detail}");
            }
            if (!state.Running)
            {
                break;
            }
            await Task.Delay(500);
            state = runner.State;
        }
        Console.WriteLine($"\n{state.Summary}");
        return state.Steps.Any(s => s.State == SelfTestState.Fail) ? 1 : 0;
    }
}
