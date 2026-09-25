namespace NetRoute.Core.Policy;

public enum AppIdentityKind
{
    /// <summary>Ordinary desktop application, identified by executable path.</summary>
    Win32,

    /// <summary>
    /// Packaged application (Store, Game Pass). Identified by package family name,
    /// and by AUMID when a specific application within the package is meant.
    /// </summary>
    Packaged,

    /// <summary>
    /// A folder: every program inside it, whatever each one is called. For a games library,
    /// where naming every game as it is installed would never end.
    /// </summary>
    Folder
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
    /// Folder the app was installed to, when discovery knew it. The split-tunnel driver matches
    /// on program files, and a game often runs as a different .exe from the one its launcher
    /// starts (DayZ_BE.exe starts DayZ_x64.exe). Knowing the folder lets NetRoute cover all of them.
    /// </summary>
    public string? InstallLocation { get; init; }

    /// <summary>
    /// A stable key for this identity, used for config equality and rule lookup.
    /// Prefers the most specific stable identifier available.
    /// </summary>
    public string StableKey => Kind switch
    {
        AppIdentityKind.Packaged => $"pkg:{Aumid ?? PackageFamilyName ?? DisplayName}".ToLowerInvariant(),
        AppIdentityKind.Folder => $"dir:{(InstallLocation ?? DisplayName).TrimEnd('\\')}".ToLowerInvariant(),
        _ => $"exe:{ExecutablePath ?? DisplayName}".ToLowerInvariant()
    };

    public static AppIdentity ForExecutable(string path, string? displayName = null) => new()
    {
        Kind = AppIdentityKind.Win32,
        DisplayName = displayName ?? Path.GetFileNameWithoutExtension(path),
        ExecutablePath = path
    };

    /// <summary>Every program in a folder, for example a whole Steam library.</summary>
    public static AppIdentity ForFolder(string path, string? displayName = null) => new()
    {
        Kind = AppIdentityKind.Folder,
        DisplayName = displayName ?? new DirectoryInfo(path.TrimEnd('\\')).Name,
        InstallLocation = path.TrimEnd('\\')
    };

    public static AppIdentity ForPackage(string familyName, string displayName, string? aumid = null) => new()
    {
        Kind = AppIdentityKind.Packaged,
        DisplayName = displayName,
        PackageFamilyName = familyName,
        Aumid = aumid
    };
}
