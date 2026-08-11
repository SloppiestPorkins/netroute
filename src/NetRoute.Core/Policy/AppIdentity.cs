namespace NetRoute.Core.Policy;

public enum AppIdentityKind
{
    /// <summary>Ordinary desktop application, identified by executable path.</summary>
    Win32,

    /// <summary>
    /// Packaged application (Store, Game Pass). Identified by package family name,
    /// and by AUMID when a specific application within the package is meant.
    /// </summary>
    Packaged
}

/// <summary>
/// How NetRoute recognises an application across launches.
///
/// <para>§11 is explicit that package identity and application identity are not the
/// same thing: one package can contain several applications, and the AUMID is what
/// names the application rather than merely the package. Both are stored, because
/// WFP filters key on the package SID (derived from the family name) while the UI
/// and process discovery need the AUMID to tell sibling applications apart.</para>
/// </summary>
public sealed record AppIdentity
{
    public required AppIdentityKind Kind { get; init; }

    /// <summary>Name shown to the user, e.g. "Halo Infinite".</summary>
    public required string DisplayName { get; init; }

    /// <summary>Full path to the executable. Required for <see cref="AppIdentityKind.Win32"/>.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Package family name, e.g. "Microsoft.HaloInfinite_8wekyb3d8bbwe".</summary>
    public string? PackageFamilyName { get; init; }

    /// <summary>Application User Model ID — identifies the application inside the package.</summary>
    public string? Aumid { get; init; }

    /// <summary>Publisher, for disambiguating two apps with the same executable name.</summary>
    public string? Publisher { get; init; }

    /// <summary>
    /// A stable key for this identity, used for config equality and rule lookup.
    /// Prefers the most specific stable identifier available.
    /// </summary>
    public string StableKey => Kind switch
    {
        AppIdentityKind.Packaged => $"pkg:{Aumid ?? PackageFamilyName ?? DisplayName}".ToLowerInvariant(),
        _ => $"exe:{ExecutablePath ?? DisplayName}".ToLowerInvariant()
    };

    public static AppIdentity ForExecutable(string path, string? displayName = null) => new()
    {
        Kind = AppIdentityKind.Win32,
        DisplayName = displayName ?? Path.GetFileNameWithoutExtension(path),
        ExecutablePath = path
    };

    public static AppIdentity ForPackage(string familyName, string displayName, string? aumid = null) => new()
    {
        Kind = AppIdentityKind.Packaged,
        DisplayName = displayName,
        PackageFamilyName = familyName,
        Aumid = aumid
    };
}
