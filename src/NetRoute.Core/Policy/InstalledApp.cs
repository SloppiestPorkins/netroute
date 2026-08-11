namespace NetRoute.Core.Policy;

/// <summary>
/// The categories the §28 "Add App" flow offers. These are presentation hints for
/// sorting a long list into something a user can navigate — never a routing decision.
/// </summary>
public enum AppCategory
{
    Other,
    Game,

    /// <summary>Steam, Epic, Xbox, Battle.net and friends. Kept distinct from Game on purpose (§37).</summary>
    Launcher,

    Browser,
    Communication
}

/// <summary>
/// An application NetRoute has discovered and could create a rule for.
///
/// <para>Distinct from <see cref="AppIdentity"/>, which is the durable thing a rule
/// stores. This carries the extra context that is only useful while choosing —
/// install location, whether it is running right now, what kind of thing it is.</para>
/// </summary>
public sealed record InstalledApp
{
    public required AppIdentity Identity { get; init; }
    public required AppCategory Category { get; init; }

    public string? InstallLocation { get; init; }

    /// <summary>Currently has at least one live process. Drives "running now" grouping in the picker.</summary>
    public bool IsRunning { get; init; }

    /// <summary>
    /// Installed through Xbox / Game Pass rather than merely being a Store package.
    ///
    /// <para>Worth distinguishing because §10 makes Game Pass a first-class entry point,
    /// and because a Game Pass title and the Xbox app itself are separate things the
    /// user will want on separate roles (§35).</para>
    /// </summary>
    public bool IsGamePass { get; init; }

    public string DisplayName => Identity.DisplayName;
}
