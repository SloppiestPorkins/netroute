using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;
using NetRoute.Windows.Traffic;

namespace NetRoute.Service;

/// <summary>
/// Verification and the live connection list, both from what the network is actually doing.
///
/// <para>This runs in the service rather than the app because only LocalSystem can see which
/// program owns every connection, games and launchers included.</para>
/// </summary>
public sealed class ConnectionVerifier(TrafficObserver observer, ILogger<ConnectionVerifier> logger) : IAppVerifier, IConnectionSource
{
    public IReadOnlyList<AppVerification> Verify(EnforcementPlan plan, DateTimeOffset policyAppliedAt)
    {
        var connections = Snapshot();
        return plan.Applications.Select(a => TrafficVerdicts.For(a, connections, policyAppliedAt)).ToList();
    }

    public IReadOnlyList<ConnectionDto> GetConnections() => Snapshot()
        .Where(c => !c.IsLoopback)
        .Select(c => new ConnectionDto
        {
            ProcessId = c.ProcessId,
            ProcessName = c.ProcessName,
            ExecutablePath = c.ExecutablePath,
            PackageFamilyName = c.PackageFamilyName,
            Protocol = c.Protocol,
            LocalEndpoint = c.Local.ToString(),
            RemoteEndpoint = c.Remote?.ToString(),
            InterfaceLuid = c.AdapterLuid,
            InterfaceName = c.AdapterName,
            InterfaceIndex = c.InterfaceIndex,
            State = c.State
        })
        .ToList();

    private IReadOnlyList<ObservedConnection> Snapshot()
    {
        try
        {
            return observer.Snapshot();
        }
        catch (Exception ex)
        {
            // Observation failing must never take status reporting down with it.
            logger.LogWarning(ex, "Reading the connection tables failed.");
            return [];
        }
    }
}
