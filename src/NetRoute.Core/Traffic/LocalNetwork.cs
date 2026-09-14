using System.Net;

namespace NetRoute.Core.Traffic;

/// <summary>
/// Destinations on the local network rather than the internet: private ranges, link-local,
/// multicast and broadcast.
///
/// <para>Traffic to them goes through whichever adapter that network is on. A TV on the
/// Ethernet LAN can only be reached through Ethernet, whatever Steam's rule says. None of it
/// is internet traffic, so it is never a leak and no rule blocks it; the rules are about which
/// internet connection an app uses. Carrier-grade NAT (100.64/10) is deliberately not here:
/// that is the ISP's side, not the user's network.</para>
/// </summary>
public static class LocalNetwork
{
    public static IReadOnlyList<(IPAddress Network, int PrefixLength)> Ranges { get; } =
    [
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("169.254.0.0"), 16),
        (IPAddress.Parse("224.0.0.0"), 4),
        (IPAddress.Parse("255.255.255.255"), 32),
        (IPAddress.Parse("fe80::"), 10),
        (IPAddress.Parse("fc00::"), 7),
        (IPAddress.Parse("ff00::"), 8)
    ];

    public static bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        var bytes = address.GetAddressBytes();
        foreach (var (network, prefix) in Ranges)
        {
            var net = network.GetAddressBytes();
            if (net.Length == bytes.Length && PrefixMatches(bytes, net, prefix))
            {
                return true;
            }
        }
        return false;
    }

    private static bool PrefixMatches(byte[] address, byte[] network, int prefix)
    {
        var whole = prefix / 8;
        for (var i = 0; i < whole; i++)
        {
            if (address[i] != network[i])
            {
                return false;
            }
        }
        var bits = prefix % 8;
        if (bits == 0)
        {
            return true;
        }
        var mask = (byte)(0xFF << (8 - bits));
        return (address[whole] & mask) == (network[whole] & mask);
    }
}
