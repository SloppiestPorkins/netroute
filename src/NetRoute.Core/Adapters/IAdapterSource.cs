namespace NetRoute.Core.Adapters;

/// <summary>
/// Supplies the current set of network interfaces.
///
/// <para>Exists so policy resolution can be tested against constructed adapter states —
/// offline roles, IPv4-only uplinks, missing adapters — which are the cases that matter
/// most and are the hardest to produce on demand against real hardware.</para>
/// </summary>
public interface IAdapterSource
{
    IReadOnlyList<NetworkAdapter> DiscoverAll();
}
