using NetRoute.Windows.Wfp;

namespace NetRoute.Poc;

/// <summary>
/// The proofs file their filters under identities of their own, never NetRoute's. The service holds
/// NetRoute's provider and sublayer for as long as it runs, and a second dynamic session cannot add
/// filters into a sublayer another session owns, so a proof sharing them would fail every filter (or,
/// now, refuse to start). With their own, a proof runs beside the service and measures what it
/// actually does; its filters are cut away when the proof exits.
/// </summary>
internal static class PocIdentities
{
    public static readonly WfpIdentity Wfp = WfpIdentity.ForInstance("poc-wfp");
    public static readonly WfpIdentity Or = WfpIdentity.ForInstance("poc-wfp-or");
    public static readonly WfpIdentity Measure = WfpIdentity.ForInstance("poc-wfp-measure");

    /// <summary>
    /// Says so when the real service's filters are live in the engine, because they are part of what
    /// the proof is measuring or competing with.
    /// </summary>
    public static void ReportCoexistence(WfpSession session)
    {
        try
        {
            var service = session.EnumerateFilterIds(WfpIdentity.Default.ProviderKey).Count;
            Console.WriteLine(service > 0
                ? $"NetRoute's own provider has {service} filter(s) installed right now (the running service). This proof uses a separate provider/sublayer and runs beside them; its numbers include the service's filters being present."
                : "NetRoute's own provider has no filters installed right now (service stopped, paused or idle).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(Could not list NetRoute's own filters: {ex.Message})");
        }
    }
}
