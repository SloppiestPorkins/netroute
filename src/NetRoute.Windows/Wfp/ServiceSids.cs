using System.Security.Cryptography;
using System.Text;

namespace NetRoute.Windows.Wfp;

/// <summary>Service SIDs (NT SERVICE\name), derived the way Windows derives them.</summary>
public static class ServiceSids
{
    /// <summary>
    /// S-1-5-80 followed by the SHA-1 of the upper-cased service name as five little-endian
    /// numbers. Computed rather than looked up, so it works whether or not the service is
    /// installed or running.
    /// </summary>
    public static string For(string serviceName)
    {
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        return "S-1-5-80-" + string.Join('-', Enumerable.Range(0, 5).Select(i => BitConverter.ToUInt32(hash, i * 4)));
    }
}
