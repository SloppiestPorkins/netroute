namespace NetRoute.Core.Config;

/// <summary>
/// Where NetRoute comes from.
///
/// <para>The feed is a small JSON document in the repository, next to the code it describes, and
/// the releases page is where the installers actually live. Both are here rather than spread
/// through the app so that forking the project and pointing it at your own repository is one
/// edit, and so the address a user is about to be sent to is written down in one place.</para>
/// </summary>
public static class Updates
{
    public const string Repository = "https://github.com/SloppiestPorkins/netroute";

    /// <summary>The update document: version, url, sha256, notes. Read over https, nothing else.</summary>
    public const string Feed = "https://raw.githubusercontent.com/SloppiestPorkins/netroute/main/updates.json";

    /// <summary>Where a person goes when NetRoute can't fetch the update for them.</summary>
    public const string Releases = Repository + "/releases";

    /// <summary>The page to send someone to for this version, or the releases index.</summary>
    public static string ReleaseFor(string? version)
        => string.IsNullOrWhiteSpace(version) || version == "0.0.0" ? Releases : Releases + "/tag/v" + version;
}
