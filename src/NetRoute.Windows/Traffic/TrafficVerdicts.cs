using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Windows.Split;

namespace NetRoute.Windows.Traffic;

/// <summary>
/// Turns observed connections into an honest verification state for one rule (§24, §25).
///
/// <para>The rules, in order:</para>
/// <list type="bullet">
/// <item>Verified needs traffic actually seen on the expected adapter. A filter existing isn't enough.</item>
/// <item>A Leak is traffic on another adapter from a connection opened after the policy took
/// effect. Connections that were already open are counted separately and reported
/// honestly, not as leaks (§19).</item>
/// <item>Loopback traffic, and sockets that can't be tied to one adapter yet, are never evidence either way.</item>
/// </list>
/// </summary>
public static class TrafficVerdicts
{
    /// <summary>Whether a connection belongs to the app a rule describes, including other program files in a game's own folder.</summary>
    public static bool Covers(AppIdentity app, ObservedConnection c)
    {
        if (app.Kind == AppIdentityKind.Packaged)
        {
            return c.PackageFamilyName is not null
                   && string.Equals(c.PackageFamilyName, app.PackageFamilyName, StringComparison.OrdinalIgnoreCase);
        }
        if (c.ExecutablePath is null)
        {
            return false;
        }
        if (string.Equals(c.ExecutablePath, app.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return SplitImagePaths.GameRoot(app) is { } root && SplitImagePaths.IsOwnFile(root, c.ExecutablePath);
    }

    public static AppVerification For(AppEnforcement app, IReadOnlyList<ObservedConnection> connections, DateTimeOffset policyAppliedAt)
    {
        var rule = app.Rule;
        // Local-network destinations (a TV, a NAS) are only reachable through the adapter their
        // network is on, so they say nothing about the rule. See LocalNetwork.
        var mine = connections
            .Where(c => !c.IsLoopback && Covers(rule.App, c) && !(c.Remote is { } remote && LocalNetwork.Contains(remote.Address)))
            .ToList();
        var attributed = mine.Where(c => c.AdapterLuid is not null).ToList();
        var busiest = attributed.GroupBy(c => c.AdapterName ?? "?").OrderByDescending(g => g.Count()).ToList();
        var observed = busiest.FirstOrDefault()?.Key;

        if (app.Action is EnforcementAction.None or EnforcementAction.FallBackToWindows)
        {
            return new AppVerification
            {
                RuleId = rule.Id,
                State = VerificationState.NotEnforced,
                ObservedAdapterName = observed,
                ActiveConnections = mine.Count,
                Summary = busiest.Count == 0
                    ? "Windows routing. No network activity right now."
                    : "Windows routing. Using " + string.Join(", ", busiest.Select(g => $"{g.Key} ({Count(g.Count(), "connection")})")) + "."
            };
        }

        var expected = app.ResolvedAdapter?.Luid;
        var elsewhere = attributed.Where(c => c.AdapterLuid != expected).ToList();
        var fresh = elsewhere.Where(c => c.FirstSeen > policyAppliedAt).ToList();
        var older = elsewhere.Count - fresh.Count;
        var onExpected = attributed.Count - elsewhere.Count;
        var leaks = fresh.Select(c => new LeakObservation
        {
            At = c.FirstSeen,
            ProcessId = c.ProcessId,
            ProcessName = c.ProcessName,
            ExpectedAdapter = app.ResolvedAdapter?.Name ?? "none (blocked)",
            ObservedAdapter = c.AdapterName ?? "another network",
            Protocol = c.Protocol,
            RemoteEndpoint = c.Remote?.ToString() ?? "(not connected)",
            Blocked = false
        }).ToList();

        if (app.Action == EnforcementAction.BlockAll)
        {
            return new AppVerification
            {
                RuleId = rule.Id,
                State = leaks.Count > 0 ? VerificationState.Leak : VerificationState.Blocked,
                ObservedAdapterName = observed,
                ActiveConnections = mine.Count,
                PreexistingConnections = older,
                Leaks = leaks,
                Summary = leaks.Count > 0
                    ? $"Leak: traffic seen on {leaks[0].ObservedAdapter} while {rule.Role.DisplayName()} is unavailable."
                    : $"{rule.Role.DisplayName()} network unavailable. Traffic blocked by Kill Switch."
            };
        }

        var name = app.ResolvedAdapter?.Name ?? "the selected network";
        var olderNote = older > 0
            ? $" {Count(older, "older connection")} still on {elsewhere.First(c => c.FirstSeen <= policyAppliedAt).AdapterName}; restart {rule.App.DisplayName} to move {(older == 1 ? "it" : "them")}."
            : string.Empty;

        if (leaks.Count > 0)
        {
            return new AppVerification
            {
                RuleId = rule.Id, State = VerificationState.Leak, ObservedAdapterName = leaks[0].ObservedAdapter,
                ActiveConnections = mine.Count, PreexistingConnections = older, Leaks = leaks,
                Summary = $"Leak: new traffic seen on {leaks[0].ObservedAdapter} instead of {name}."
            };
        }
        if (onExpected > 0)
        {
            return new AppVerification
            {
                RuleId = rule.Id, State = VerificationState.Verified, ObservedAdapterName = name,
                ActiveConnections = mine.Count, PreexistingConnections = older,
                Summary = $"Verified: using {name} ({Count(onExpected, "connection")}).{olderNote}"
            };
        }
        return new AppVerification
        {
            RuleId = rule.Id, State = VerificationState.Configured, ObservedAdapterName = observed,
            ActiveConnections = mine.Count, PreexistingConnections = older,
            Summary = older > 0 ? olderNote.Trim()
                : mine.Count > 0 ? $"Applied for {name}. Its open sockets aren't tied to one network yet."
                : $"Applied for {name}. No network activity right now."
        };
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
