namespace NetRoute.Ipc;

/// <summary>
/// Describes rules that failed to install without implying the rest are off. The service used to
/// report enforcement as unavailable when any one rule failed, so four stale browser paths made
/// a working setup of thirty-four rules look dead. The count is derived from the per-rule errors
/// already in the status, so the wire format is unchanged.
/// </summary>
public static class RuleFailures
{
    public static IReadOnlyList<AppStatusDto> Failed(this ServiceStatusDto status)
        => status.Apps.Where(a => a.Error is not null).ToList();

    /// <summary>Null when every rule is installed.</summary>
    public static string? Summary(this ServiceStatusDto status)
    {
        var failed = status.Failed().Count;
        if (failed == 0)
        {
            return null;
        }

        var total = status.Apps.Count;
        var still = total - failed;
        var rules = failed == 1 ? "1 rule" : $"{failed} rules";
        return still == 0
            ? $"{rules} couldn't be applied, so no app is being kept on its network."
            : $"{rules} couldn't be applied. {(still == 1 ? "The other rule is" : $"The other {still} rules are")} still enforced.";
    }

    /// <summary>Short form for a status pill.</summary>
    public static string? Headline(this ServiceStatusDto status)
    {
        var failed = status.Failed().Count;
        return failed switch { 0 => null, 1 => "1 rule needs attention", _ => $"{failed} rules need attention" };
    }
}
