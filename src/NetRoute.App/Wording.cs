using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;

namespace NetRoute.App;

public enum Tone { Good, Warn, Bad, Muted }

/// <summary>A status badge and the sentence under it.</summary>
public sealed record Badge(string Text, Tone Tone, string? Summary);

/// <summary>
/// What the window is allowed to claim, decided in one place with no WPF types so it can be tested.
///
/// <para>The rule: a word names the evidence behind it. "Verified" needs data seen moving on the
/// assigned connection, not just an open socket. A state that comes from the plan alone ("held")
/// says it is the rule in force, not something observed. Nothing here points at files the
/// installer doesn't carry.</para>
/// </summary>
public static class StatusWording
{
    /// <summary>What to tell someone whose driver or service isn't running. It depends on no script, only on the installer they already ran.</summary>
    public const string RepairAdvice = "Run the NetRoute installer again and choose Repair.";

    public const string DriverDown =
        "NetRoute can keep apps off the wrong network, but can't move them onto another one yet: the split-tunnel driver isn't running. " + RepairAdvice;

    public const string ServiceDown =
        "Until it's running, nothing is keeping your apps on the network you chose. Start it here (Windows will ask for permission). " +
        "If that doesn't work, " + "run the NetRoute installer again and choose Repair.";

    /// <summary>Why the update-address controls are off. The service refuses the change from anything but an elevated administrator.</summary>
    public const string UpdateSettingsNeedAdmin =
        "Changing where updates come from needs administrator rights, because it decides which installer NetRoute will offer to run. " +
        "Close NetRoute and open it with Run as administrator to change this. Checking for an update still works as you are.";

    public const string ServiceDidNotStart = "The NetRoute service didn't start. " + RepairAdvice;

    /// <summary>The badge for one app. <paramref name="live"/> is what the live strip measured, or null when nothing has been.</summary>
    public static Badge ForApp(AppStatusDto dto, LiveApp? live)
    {
        if (dto.Error is not null)
        {
            return new("Problem", Tone.Warn, dto.Error.FriendlyMessage);
        }
        if (dto.Rule.Paused)
        {
            return new("Paused", Tone.Muted, dto.VerificationSummary);
        }
        var where = dto.ObservedAdapterName ?? dto.ResolvedAdapterName ?? "the assigned connection";
        switch (dto.Verification)
        {
            case VerificationState.Verified:
                if (live is { Placement: { IsProblem: true } bad })
                {
                    return new(bad.State == PlacementState.Split ? "Partly off" : "Off target", bad.State == PlacementState.Split ? Tone.Warn : Tone.Bad, live.Headline);
                }
                // Verified needs data seen moving, not just a socket bound to the right address.
                return live is { Placement: { State: PlacementState.OnAssigned, FromTraffic: true } }
                    ? new("Verified", Tone.Good, dto.VerificationSummary)
                    : new("Unconfirmed", Tone.Muted,
                        $"Has connections open on {where}, but no data has been seen moving yet, so this isn't confirmed.");
            case VerificationState.Blocked:
                return new("Held", Tone.Bad,
                    $"{dto.Rule.Role.DisplayName()} isn't available, so this app is set to be held rather than fall back to another connection. " +
                    "That is the rule in force; NetRoute doesn't count refused connections.");
            case VerificationState.Leak:
                return new("Leak", Tone.Bad, dto.VerificationSummary);
            case VerificationState.Configured:
                return new("Applied", Tone.Muted, dto.VerificationSummary);
            case VerificationState.NotRunning:
                return new("Not running", Tone.Muted, dto.VerificationSummary);
            default:
                return new("Not managed", Tone.Muted, dto.VerificationSummary);
        }
    }

    /// <summary>The header pill. "Protected" is reserved for the case where something has been seen on its assigned connection.</summary>
    public static (string Text, Tone Tone) Header(ServiceStatusDto status)
    {
        if (status.EnforcementPaused)
        {
            return (status.PausedUntil is { } until ? $"Paused until {until.ToLocalTime():HH:mm}" : "Paused", Tone.Muted);
        }
        if (!status.EnforcementActive)
        {
            return ("Needs attention", Tone.Warn);
        }
        if (status.Headline() is { } headline)
        {
            return (headline, Tone.Warn);
        }
        var apps = status.Apps;
        if (apps.Any(a => a.Verification is VerificationState.Leak))
        {
            return ("Leak detected", Tone.Bad);
        }
        if (apps.Any(a => a.Verification is VerificationState.Blocked))
        {
            return ("Apps held", Tone.Bad);
        }
        if (!apps.Any(a => a.Action is EnforcementAction.PinToAdapter or EnforcementAction.BlockAll))
        {
            return ("Nothing to enforce yet", Tone.Muted);
        }
        if (!status.RedirectionAvailable && apps.Any(a => a.Rule.Role == RoleId.Gaming && a.Action == EnforcementAction.PinToAdapter))
        {
            return ("Apps not being moved", Tone.Warn);
        }
        return apps.Any(a => a.Verification is VerificationState.Verified)
            ? ("Protected", Tone.Good)
            : ("Rules applied, no traffic seen yet", Tone.Muted);
    }

    /// <summary>True while NetRoute is making Downloads the Windows default route (mirrors RedirectPlanner).</summary>
    public static bool ManagesDefaultRoute(ServiceStatusDto? status)
    {
        if (status is not { SetupCompleted: true, EnforcementPaused: false })
        {
            return false;
        }
        var gaming = status.Roles.FirstOrDefault(r => r.Role == RoleId.Gaming)?.Adapter;
        var downloads = status.Roles.FirstOrDefault(r => r.Role == RoleId.Downloads)?.Adapter;
        return gaming is not null && downloads is not null && gaming.Luid != downloads.Luid
               && status.Apps.Any(a => a.Rule.Role == RoleId.Gaming && a.Action != EnforcementAction.None);
    }

    /// <summary>The line under "Default" in the destination picker. It says what actually happens to such an app.</summary>
    public static string DefaultSubtitle(ServiceStatusDto? status)
        => ManagesDefaultRoute(status) ? "Downloads line, unless you add it" : "Windows decides";

    /// <summary>The note on a role card whose connection is down.</summary>
    public static string RoleOfflineNote(RoleId role, int assigned)
        => $"{assigned} app{(assigned == 1 ? " is" : "s are")} set to use {role.DisplayName()}. " +
           "Apps on Strict are held rather than falling back to another connection; apps set to fall back use Windows routing.";
}
