using System.Security.Cryptography;
using System.Text;

namespace NetRoute.Windows.Wfp;

/// <summary>
/// The names a <see cref="WfpSession"/> files its provider, sublayer and filters under.
///
/// <para>The service uses <see cref="Default"/>. A proof harness that must run while the service is
/// enforcing uses <see cref="ForInstance"/>, which gives it a provider and sublayer of its own, so
/// its dynamic session never collides with the service's (a second dynamic session cannot add
/// filters into a sublayer another session owns).</para>
///
/// <para>Everything that cleans up "our" objects, the enumerate-by-provider emergency disable
/// included, is keyed on <see cref="ProviderKey"/>, so one identity never touches another's.</para>
/// </summary>
public sealed record WfpIdentity(Guid ProviderKey, Guid SubLayerKey, string SessionName)
{
    /// <summary>The real NetRoute: identifies every object the service owns, so uninstall removes ours and nothing else (§44).</summary>
    public static WfpIdentity Default { get; } = new(
        new Guid("6E9D1F2A-3B7C-4E58-9A21-D4C8B5E70A13"),
        new Guid("B41C7E90-52A8-4D3F-8C16-7F2E9A4B60D5"),
        "NetRoute");

    /// <summary>
    /// A separate identity derived from <paramref name="instance"/>: the same name always gives the same
    /// keys (so a crashed proof's objects are recognisable), and never the keys of <see cref="Default"/>.
    /// </summary>
    public static WfpIdentity ForInstance(string instance)
    {
        var name = instance?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("An instance name is required.", nameof(instance));
        }

        return new WfpIdentity(Derive("provider", name), Derive("sublayer", name), $"NetRoute ({name})");
    }

    public bool IsDefault => ProviderKey == Default.ProviderKey;

    // A name-based (version 5 style) GUID, so the value is stable across runs and machines.
    private static Guid Derive(string purpose, string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"NetRoute.Wfp.Instance.v1|{purpose}|{name}"));
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}
