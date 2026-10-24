namespace NetRoute.Windows.Wfp;

/// <summary>
/// The provider and sublayer a session needs already belong to another live session: another copy
/// of NetRoute (or a proof harness) is enforcing. This is not a broken rule or a broken engine, and
/// reporting it as 19 failed rules is what it replaced. Nothing can be installed from this session
/// until the other one is gone, so <see cref="WfpSession.Open"/> refuses rather than returning a
/// session that cannot do anything.
/// </summary>
public sealed class AnotherInstanceException : InvalidOperationException
{
    /// <summary>Who holds the objects, as far as the engine could tell (empty when it could not).</summary>
    public IReadOnlyList<string> Owners { get; }

    public AnotherInstanceException(IReadOnlyList<string> owners)
        : base(Describe(owners))
    {
        Owners = owners;
    }

    private static string Describe(IReadOnlyList<string> owners)
        => owners.Count > 0
            ? $"Another copy of NetRoute is already running ({string.Join(", ", owners)}), so this copy can't install network policy. " +
              "Only one copy can enforce at a time: stop the other one first."
            : "Another copy of NetRoute, or another program using NetRoute's network-policy provider, is already running, " +
              "so this copy can't install network policy. Only one copy can enforce at a time: stop the other one first.";
}
