namespace NetRoute.Core.Policy;

/// <summary>
/// Apps that host and find games on the local network, which NetRoute must never move.
///
/// <para>Moving an app means the split-tunnel driver rewrites its socket binds, and its own
/// documentation is explicit that this costs three things with no general mitigation: a moved
/// app can't bind 0.0.0.0 or ::, its multicast group joins stop matching traffic, and it can't
/// reach localhost over UDP.</para>
///
/// <para>Minecraft needs all three. A LAN world is announced from a wildcard UDP socket to the
/// multicast address 224.0.2.60:4445, the game itself runs as a child of the launcher (and
/// exclusion is inherited by children), and the launcher is built on CEF, which talks to
/// localhost. Moving it therefore breaks LAN worlds and can stop it starting at all — which is
/// exactly what happened on 24 September 2026. Breaking an app quietly is the failure §24
/// exists to prevent, so these keep Windows routing and NetRoute says so in "Why?".</para>
/// </summary>
public static class LocalHostingApps
{
    /// <summary>Matched against the program path, install folder and name.</summary>
    private static readonly string[] Names =
    [
        "minecraft", "curseforge", "prismlauncher", "multimc", "atlauncher", "gdlauncher",
        "modrinth", "techniclauncher", "ftbapp", "javaw"
    ];

    private static readonly string[] Packages =
    [
        "Microsoft.MinecraftUWP", "Microsoft.4297127D64EC6", "Microsoft.MinecraftEducationEdition"
    ];

    public static bool Includes(AppIdentity app)
    {
        if (app.PackageFamilyName is { } family && Packages.Any(p => family.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        var haystack = $"{app.ExecutablePath} {app.InstallLocation} {app.DisplayName}";
        return Names.Any(name => haystack.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Why it isn't moved, in the user's words.</summary>
    public static string Explain(AppIdentity app)
        => $"{app.DisplayName} hosts and finds games on your local network. Moving an app rewrites its network " +
           "sockets, which breaks LAN worlds and can stop it starting, so NetRoute leaves it on Windows routing.";
}
